using System.Security.Claims;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Ai;

public sealed record GenerateRequest(string ThemeSlug, int Players, ContentRating ContentRating, MysteryLength Length, string? Twist);

public sealed record GenerationJobView(
    Guid Id, string ThemeSlug, GenerationStatus Status, string Progress, string? ScenarioId, string? Error,
    IReadOnlyList<string> Warnings, DateTimeOffset CreatedAt);

public static class GenerationEndpoints
{
    public static void MapGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/generation").RequireAuthorization(AuthPolicies.Host);

        group.MapPost("/", async (GenerateRequest req, ClaimsPrincipal user, AppDbContext db, AiGateway ai, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await ai.IsConfiguredAsync(AiRole.Storyteller, ct))
                return Results.Problem("No Storyteller AI is set up yet. An admin can configure one under Admin → AI.", statusCode: 400);
            if (req.Players is < 3 or > 8) return Results.Problem("Choose 3 to 8 players.", statusCode: 400);
            if (req.Twist is { Length: > 300 }) return Results.Problem("Keep the twist under 300 characters.", statusCode: 400);
            if (!await db.Themes.AnyAsync(t => t.Slug == req.ThemeSlug, ct)) return Results.Problem("Unknown theme.", statusCode: 400);

            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            // One job at a time per host keeps costs predictable.
            if (await db.GenerationJobs.AnyAsync(j => j.HostUserId == userId && (j.Status == GenerationStatus.Queued || j.Status == GenerationStatus.Running), ct))
                return Results.Problem("You already have a mystery being written. Please wait for it to finish.", statusCode: 409);

            var now = clock.GetUtcNow();
            var job = new GenerationJobEntity
            {
                Id = Guid.NewGuid(), HostUserId = userId, ThemeSlug = req.ThemeSlug, Request = GameJson.Serialize(req),
                Status = GenerationStatus.Queued, Progress = "Waiting to start…", CreatedAt = now, UpdatedAt = now,
            };
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToView(job));
        }).AddEndpointFilter(ButlerDidIt.Api.Endpoints.AuthEndpoints.RequireConfirmedHost);

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.HostUserId == userId, ct);
            return job is null ? Results.NotFound() : Results.Ok(ToView(job));
        });
    }

    public static GenerationJobView ToView(GenerationJobEntity j) => new(
        j.Id, j.ThemeSlug, j.Status, j.Progress, j.ScenarioId, j.Error, GameJson.Deserialize<List<string>>(j.Warnings), j.CreatedAt);
}

/// <summary>
/// Writes queued mysteries in the background. Generation can take a minute or
/// more, far longer than a web request should stay open, so the browser polls
/// the job for progress instead.
/// </summary>
public sealed class GenerationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<GenerationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MarkInterruptedJobsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Generation worker loop failed");
            }
        }
    }

    /// <summary>A job left "Running" when the server stopped will never finish, so fail it with an honest message.</summary>
    private async Task MarkInterruptedJobsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.GenerationJobs.Where(j => j.Status == GenerationStatus.Running)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, GenerationStatus.Failed)
                .SetProperty(j => j.Error, "The server restarted while this mystery was being written. Please try again."), ct);
    }

    public async Task RunNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.GenerationJobs.OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(j => j.Status == GenerationStatus.Queued, ct);
        if (job is null) return;

        // Claim the job atomically so two workers (or a test and the background loop) never both run it.
        var claimed = await db.GenerationJobs.Where(j => j.Id == job.Id && j.Status == GenerationStatus.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, GenerationStatus.Running), ct);
        if (claimed == 0) return;
        await db.Entry(job).ReloadAsync(ct);

        job.Status = GenerationStatus.Running;
        job.Progress = "Starting…";
        job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        try
        {
            var req = GameJson.Deserialize<GenerateRequest>(job.Request);
            var themeRow = await db.Themes.AsNoTracking().FirstAsync(t => t.Slug == job.ThemeSlug, ct);
            var theme = GameJson.Deserialize<ThemeDefinition>(themeRow.Document);
            var generator = scope.ServiceProvider.GetRequiredService<MysteryGenerator>();
            var progress = new JobProgress(scopes, job.Id, clock);

            var result = await generator.GenerateAsync(
                new GenerationRequest(theme, req.Players, req.ContentRating, req.Length, req.Twist),
                new AiCallContext(job.HostUserId, JobId: job.Id), progress, ct);

            var s = result.Scenario;
            db.Scenarios.Add(new ScenarioEntity
            {
                Id = s.Id, ThemeSlug = s.ThemeSlug, Title = s.Title, MinPlayers = s.MinPlayers, MaxPlayers = s.MaxPlayers,
                ContentRating = s.ContentRating, Source = ScenarioSource.AiGenerated, OwnerUserId = job.HostUserId,
                Document = GameJson.Serialize(s), UpdatedAt = clock.GetUtcNow(),
            });
            job.Status = GenerationStatus.Succeeded;
            job.ScenarioId = s.Id;
            job.Progress = $"\"{s.Title}\" is ready.";
            job.Warnings = GameJson.Serialize(result.Warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Generation job {JobId} failed", job.Id);
            job.Status = GenerationStatus.Failed;
            job.Error = ex is AiException ? ex.Message : "Something went wrong while writing the mystery. Please try again.";
        }

        job.UpdatedAt = clock.GetUtcNow();
        if (job.Status == GenerationStatus.Failed)
        {
            // Don't save a half-built scenario row alongside a failed job.
            foreach (var added in db.ChangeTracker.Entries<ScenarioEntity>().Where(e => e.State == EntityState.Added).ToList())
                added.State = EntityState.Detached;
        }
        // EF only writes the columns we changed, so progress updates made meanwhile
        // (via ExecuteUpdate) don't conflict; the final message simply replaces them.
        await db.SaveChangesAsync(ct);
    }

    private sealed class JobProgress(IServiceScopeFactory scopes, Guid jobId, TimeProvider clock) : IProgress<string>
    {
        public void Report(string value)
        {
            // Fire-and-forget: progress text is cosmetic and must never slow down generation.
            _ = Task.Run(async () =>
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.GenerationJobs.Where(j => j.Id == jobId && j.Status == GenerationStatus.Running)
                    .ExecuteUpdateAsync(u => u.SetProperty(j => j.Progress, value).SetProperty(j => j.UpdatedAt, clock.GetUtcNow()));
            });
        }
    }
}
