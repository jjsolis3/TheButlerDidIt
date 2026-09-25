using System.Security.Cryptography;
using System.Text;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Media;

public sealed class MediaOptions
{
    /// <summary>Folder for generated and uploaded files. In Docker this is the `media` volume at /data/media.</summary>
    public string Root { get; set; } = "data/media";
}

/// <summary>Saves files to the media folder. A small seam so S3-compatible storage (#16) can replace it later.</summary>
public sealed class MediaStore(IOptions<MediaOptions> options, IWebHostEnvironment env)
{
    private string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.Root));

    public async Task<string> SaveAsync(byte[] bytes, string extension, CancellationToken ct)
    {
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy-MM"), $"{Guid.NewGuid():N}.{extension}");
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct);
        return relative.Replace('\\', '/');
    }

    /// <summary>Resolves a stored relative path, refusing anything that escapes the media folder.</summary>
    public string? Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public void Delete(string relative)
    {
        if (Resolve(relative) is { } full) File.Delete(full);
    }

    public static string Url(Guid assetId) => $"/media/assets/{assetId}";
}

/// <summary>
/// Creates voice clips and pictures through <see cref="MediaGateway"/>, with a
/// cache: the same text in the same voice (or the same prompt) from the same model
/// is only ever generated and paid for once, by hashing the request.
/// </summary>
public sealed class MediaService(AppDbContext db, MediaGateway media, MediaStore store, TimeProvider clock)
{
    public async Task<Guid> SpeechAsync(string text, string voice, AiCallContext context, CancellationToken ct)
    {
        var model = await media.DescribeAsync(AiRole.Voice, ct) ?? throw new AiUnavailableException("No voice provider is set up.");
        return await GetOrCreateAsync(MediaKind.Audio, $"speech|{model}|{voice}|{text}", model, text,
            () => media.SpeakAsync(text, voice, context, ct), ct);
    }

    public async Task<Guid> ImageAsync(string prompt, ImageShape shape, AiCallContext context, CancellationToken ct)
    {
        var model = await media.DescribeAsync(AiRole.Illustrator, ct) ?? throw new AiUnavailableException("No image provider is set up.");
        return await GetOrCreateAsync(MediaKind.Image, $"image|{model}|{shape}|{prompt}", model, prompt,
            () => media.PaintAsync(prompt, shape, context, ct), ct);
    }

    private async Task<Guid> GetOrCreateAsync(MediaKind kind, string cacheKey, string provider, string prompt, Func<Task<MediaFile>> create, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)));
        var existing = await db.MediaAssets.AsNoTracking().Where(a => a.ContentHash == hash).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;

        var file = await create();
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(), Kind = kind, Path = await store.SaveAsync(file.Bytes, file.Extension, ct), ContentHash = hash,
            Provider = provider[..Math.Min(provider.Length, 60)], Prompt = prompt, ContentType = file.ContentType,
            SizeBytes = file.Bytes.Length, CreatedAt = clock.GetUtcNow(),
        };
        db.MediaAssets.Add(asset);
        try
        {
            await db.SaveChangesAsync(ct);
            return asset.Id;
        }
        catch (DbUpdateException)
        {
            // Two requests generated the same thing at once; keep the first and drop ours.
            db.Entry(asset).State = EntityState.Detached;
            store.Delete(asset.Path);
            return await db.MediaAssets.AsNoTracking().Where(a => a.ContentHash == hash).Select(a => a.Id).FirstAsync(ct);
        }
    }

    /// <summary>Stores an uploaded file (e.g. a selfie) as-is, without generation or caching.</summary>
    public async Task<Guid> SaveUploadAsync(MediaKind kind, byte[] bytes, string contentType, string extension, CancellationToken ct)
    {
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(), Kind = kind, Path = await store.SaveAsync(bytes, extension, ct),
            ContentHash = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())), Provider = "upload",
            ContentType = contentType, SizeBytes = bytes.Length, CreatedAt = clock.GetUtcNow(),
        };
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        return asset.Id;
    }
}

public sealed record MediaJobView(Guid Id, MediaJobStatus Status, int Total, int Done, int Failed, string? Error);

/// <summary>
/// Prepares all the voices and pictures for a scenario in the background: the
/// narration, every NPC line, every portrait and scene. Results are shared by
/// every party that plays the same scenario.
/// </summary>
public sealed class MediaWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MediaWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.MediaJobs.Where(j => j.Status == MediaJobStatus.Running)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, MediaJobStatus.Queued), stoppingToken); // resume after a restart
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Media worker loop failed");
            }
        }
    }

    public async Task RunNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var job = await db.MediaJobs.OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(j => j.Status == MediaJobStatus.Queued, ct);
        if (job is null) return;

        // Claim the job atomically: only one worker can move it from Queued to Running.
        var claimed = await db.MediaJobs.Where(j => j.Id == job.Id && j.Status == MediaJobStatus.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, MediaJobStatus.Running), ct);
        if (claimed == 0) return;
        await db.Entry(job).ReloadAsync(ct);

        var catalog = sp.GetRequiredService<ContentCatalog>();
        var gateway = sp.GetRequiredService<MediaGateway>();
        var media = sp.GetRequiredService<MediaService>();

        var scenario = await catalog.GetBaseScenarioAsync(db, job.ScenarioId, ct);
        var themeRow = await db.Themes.AsNoTracking().FirstAsync(t => t.Slug == scenario.ThemeSlug, ct);
        var theme = GameJson.Deserialize<ThemeDefinition>(themeRow.Document);
        var done = await db.ScenarioMedia.Where(m => m.ScenarioId == job.ScenarioId).Select(m => m.Key).ToHashSetAsync(ct);
        var plan = MediaPlan.For(scenario, theme, await gateway.VoicesConfiguredAsync(ct), await gateway.ImagesConfiguredAsync(ct))
            .Where(i => !done.Contains(i.Key)).ToList();

        job.Status = MediaJobStatus.Running;
        job.Total = plan.Count;
        job.Done = 0;
        job.Failed = 0;
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var context = new AiCallContext(job.HostUserId, JobId: job.Id, Purpose: "media");
        foreach (var item in plan)
        {
            try
            {
                var assetId = item.Role == AiRole.Voice
                    ? await media.SpeechAsync(item.Text, item.Voice, context with { Purpose = "voice" }, ct)
                    : await media.ImageAsync(item.Text, item.Shape, context with { Purpose = "image" }, ct);
                db.ScenarioMedia.Add(new ScenarioMediaEntity { ScenarioId = job.ScenarioId, Key = item.Key, AssetId = assetId });
                job.Done++;
            }
            catch (AiBudgetExceededException ex)
            {
                job.Error = ex.Message;
                break; // no point trying the rest
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed picture (e.g. refused by the provider's safety filter) shouldn't stop the rest.
                log.LogWarning(ex, "Media item {Key} failed for scenario {ScenarioId}", item.Key, job.ScenarioId);
                job.Failed++;
                job.Error = ex is AiException ? ex.Message : "Some items could not be created.";
            }
            job.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        job.Status = job.Done == 0 && job.Total > 0 ? MediaJobStatus.Failed : MediaJobStatus.Succeeded;
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // New art and voices: refresh every screen of every party using this scenario.
        catalog.Invalidate(job.ScenarioId);
        var parties = sp.GetRequiredService<PartyService>();
        var partyIds = await db.Parties.AsNoTracking()
            .Where(p => p.ScenarioId == job.ScenarioId && p.Status != PartyStatus.Finished).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in partyIds)
        {
            await parties.BroadcastAsync(await parties.LoadAsync(id, ct), clock.GetUtcNow());
        }
    }

    /// <summary>Queues preparation for a scenario unless one is already waiting or running.</summary>
    public static async Task<MediaJobEntity> EnqueueAsync(AppDbContext db, string scenarioId, string hostUserId, TimeProvider clock, CancellationToken ct)
    {
        var active = await db.MediaJobs.FirstOrDefaultAsync(j => j.ScenarioId == scenarioId &&
            (j.Status == MediaJobStatus.Queued || j.Status == MediaJobStatus.Running), ct);
        if (active is not null) return active;
        var now = clock.GetUtcNow();
        var job = new MediaJobEntity { Id = Guid.NewGuid(), ScenarioId = scenarioId, HostUserId = hostUserId, Status = MediaJobStatus.Queued, CreatedAt = now, UpdatedAt = now };
        db.MediaJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }
}
