using System.Security.Cryptography;
using System.Text;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Hubs;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Api.Scale;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Media;

/// <summary>
/// Creates voice clips and pictures through <see cref="MediaGateway"/>, with a
/// cache: the same text in the same voice (or the same prompt) from the same model
/// is only ever generated and paid for once, by hashing the request.
/// </summary>
public sealed class MediaService(AppDbContext db, MediaGateway media, IMediaStore store, TimeProvider clock)
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
            Id = Guid.NewGuid(), Kind = kind, Path = await store.SaveAsync(file.Bytes, file.Extension, file.ContentType, ct), ContentHash = hash,
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
            await store.DeleteAsync(asset.Path, CancellationToken.None);
            return await db.MediaAssets.AsNoTracking().Where(a => a.ContentHash == hash).Select(a => a.Id).FirstAsync(ct);
        }
    }

    /// <summary>Stores an uploaded file (e.g. a selfie) as-is, without generation or caching.</summary>
    public Task<Guid> SaveUploadAsync(MediaKind kind, byte[] bytes, string contentType, string extension, Guid? partyId, CancellationToken ct) =>
        SaveUploadAsync(kind, new MemoryStream(bytes, writable: false), bytes.Length, contentType, extension, partyId, ownerUserId: null, ct);

    /// <summary>Stores an uploaded file from a stream (e.g. a room's video), copied as it's read.</summary>
    /// <param name="ownerUserId">The host who uploaded it, for their upload allowance; null for guests' selfies.</param>
    public async Task<Guid> SaveUploadAsync(MediaKind kind, Stream content, long sizeBytes, string contentType, string extension, Guid? partyId,
        string? ownerUserId, CancellationToken ct)
    {
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(), Kind = kind, Path = await store.SaveAsync(content, extension, contentType, ct),
            ContentHash = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())), Provider = Upload,
            ContentType = contentType, SizeBytes = sizeBytes, CreatedAt = clock.GetUtcNow(), PartyId = partyId, OwnerUserId = ownerUserId,
        };
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        return asset.Id;
    }

    /// <summary>The <see cref="MediaAsset.Provider"/> of everything people upload, as opposed to what the AI makes.</summary>
    public const string Upload = "upload";

    /// <summary>
    /// Deletes the uploads among these that no room's media points at any more. A copy of a room shares its files
    /// with the original, so a file goes only when the last room using it lets go. Generated media is always kept.
    /// </summary>
    public async Task DeleteUnusedUploadsAsync(IEnumerable<Guid> assetIds, CancellationToken ct)
    {
        var ids = assetIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var used = await db.ScenarioMedia.Where(m => ids.Contains(m.AssetId)).Select(m => m.AssetId).Distinct().ToListAsync(ct);
        await DeleteUploadsAsync(ids.Except(used), ct);
    }

    /// <summary>Deletes uploaded files (row and bytes). Generated media is shared between parties, so it is never deleted here.</summary>
    public async Task DeleteUploadsAsync(IEnumerable<Guid> assetIds, CancellationToken ct)
    {
        var ids = assetIds.ToList();
        if (ids.Count == 0) return;
        var assets = await db.MediaAssets.Where(a => ids.Contains(a.Id) && a.Provider == Upload).ToListAsync(ct);
        db.MediaAssets.RemoveRange(assets);
        await db.SaveChangesAsync(ct);
        // Delete the files only after the rows are gone: a crash in between leaves an
        // unreferenced file (harmless), never a row pointing at a missing file.
        foreach (var asset in assets) await store.DeleteAsync(asset.Path, ct);
    }
}

public sealed record MediaJobView(Guid Id, MediaJobStatus Status, int Total, int Done, int Failed, string? Error);

/// <summary>
/// Prepares all the voices and pictures for a scenario in the background: the
/// narration, every NPC line, every portrait and scene. Results are shared by
/// every party that plays the same scenario.
/// </summary>
public sealed class MediaWorker(IServiceScopeFactory scopes, TimeProvider clock, JobEvents events, ClusterLock cluster, ILogger<MediaWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Resume after a restart. With several servers, leave jobs another server is still making progress on.
            var interrupted = db.MediaJobs.Where(j => j.Status == MediaJobStatus.Running);
            if (cluster.Enabled)
            {
                var staleBefore = clock.GetUtcNow() - ScaleDefaults.StaleJobAfter;
                interrupted = interrupted.Where(j => j.UpdatedAt < staleBefore);
            }
            await interrupted.ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, MediaJobStatus.Queued), stoppingToken);
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

        // A job is for a mystery, or (with an "escape:" id) for an escape room's pictures.
        var roomId = ButlerDidIt.Api.Escape.EscapeMedia.RoomId(job.ScenarioId);
        IReadOnlyList<MediaItem> wanted;
        if (roomId is not null)
        {
            var room = await sp.GetRequiredService<ButlerDidIt.Api.Escape.EscapeCatalog>().FindAsync(db, roomId, ct);
            wanted = room is not null && await gateway.ImagesConfiguredAsync(ct) ? EscapeMediaPlan.For(room) : [];
        }
        else
        {
            var scenario = await catalog.GetBaseScenarioAsync(db, job.ScenarioId, ct);
            var themeRow = await db.Themes.AsNoTracking().FirstAsync(t => t.Slug == scenario.ThemeSlug, ct);
            var theme = GameJson.Deserialize<ThemeDefinition>(themeRow.Document);
            wanted = MediaPlan.For(scenario, theme, await gateway.VoicesConfiguredAsync(ct), await gateway.ImagesConfiguredAsync(ct));
        }
        var done = await db.ScenarioMedia.Where(m => m.ScenarioId == job.ScenarioId).Select(m => m.Key).ToHashSetAsync(ct);
        var plan = wanted.Where(i => !done.Contains(i.Key)).ToList();

        job.Status = MediaJobStatus.Running;
        job.Total = plan.Count;
        job.Done = 0;
        job.Failed = 0;
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await events.ChangedAsync(job.HostUserId);

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
            await events.ChangedAsync(job.HostUserId);
        }

        job.Status = job.Done == 0 && job.Total > 0 ? MediaJobStatus.Failed : MediaJobStatus.Succeeded;
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await events.ChangedAsync(job.HostUserId);

        // New art and voices: refresh every screen of every party using this scenario (or room).
        if (roomId is null) catalog.Invalidate(job.ScenarioId);
        else sp.GetRequiredService<ButlerDidIt.Api.Escape.EscapeCatalog>().ForgetArt(roomId);
        await sp.GetRequiredService<PartyRuntime>().RefreshAsync(roomId ?? job.ScenarioId, ct);
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
