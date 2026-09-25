using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Game.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Parties;

public sealed class RetentionOptions
{
    /// <summary>A lobby or unfinished game with no activity for this long is deleted entirely. 0 = never.</summary>
    public int IdlePartyDays { get; set; } = 14;

    /// <summary>A finished party's seats, notes and selfies are removed this long after its last activity. 0 = never.</summary>
    public int FinishedPartyDays { get; set; } = 30;

    /// <summary>How often the job runs.</summary>
    public double IntervalHours { get; set; } = 6;
}

/// <summary>
/// Cleans up old parties so the database doesn't grow forever and guests' data
/// isn't kept longer than needed:
/// <list type="bullet">
/// <item>Abandoned lobbies and games are deleted, which also frees their join codes.</item>
/// <item>Finished parties lose their seats (so old seat tokens stop working), private
/// notes and costume selfies. The party row stays so its recap page keeps working.</item>
/// <item>Selfies that no longer belong to any party are deleted.</item>
/// </list>
/// </summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider clock, ILogger<RetentionWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(0.01, options.Value.IntervalHours));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var result = await RunOnceAsync(stoppingToken);
                if (result.Total > 0) log.LogInformation("Retention: {Result}", result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Retention job failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public sealed record Result(int DeletedParties, int PrunedParties, int DeletedPhotos)
    {
        public int Total => DeletedParties + PrunedParties + DeletedPhotos;
    }

    /// <param name="now">Overridable so tests can "fast-forward" time.</param>
    public async Task<Result> RunOnceAsync(CancellationToken ct, DateTimeOffset? now = null)
    {
        var at = now ?? clock.GetUtcNow();
        var o = options.Value;
        var deleted = o.IdlePartyDays > 0 ? await DeleteIdlePartiesAsync(at.AddDays(-o.IdlePartyDays), ct) : 0;
        var pruned = o.FinishedPartyDays > 0 ? await PruneFinishedPartiesAsync(at.AddDays(-o.FinishedPartyDays), ct) : 0;
        var photos = await DeleteOrphanPhotosAsync(ct);
        return new Result(deleted, pruned, photos);
    }

    private async Task<int> DeleteIdlePartiesAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        List<Guid> ids;
        using (var scope = scopes.CreateScope())
        {
            ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking()
                .Where(p => p.Status != PartyStatus.Finished && p.UpdatedAt < cutoff).Select(p => p.Id).ToListAsync(ct);
        }

        foreach (var id in ids)
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            // Take the party's lock so we never delete it halfway through someone's command.
            using (await sp.GetRequiredService<PartyLocks>().AcquireAsync(id, ct))
            {
                var seatIds = await db.Seats.Where(s => s.PartyId == id).Select(s => s.Id).ToListAsync(ct);
                await db.PlayerNotes.Where(n => seatIds.Contains(n.SeatId)).ExecuteDeleteAsync(ct);
                await db.Parties.Where(p => p.Id == id).ExecuteDeleteAsync(ct); // seats go with it (cascade)
                await DeletePartyPhotosAsync(sp, id, ct);
                await NotifyRemovedAsync(sp, seatIds);
            }
        }
        return ids.Count;
    }

    private async Task<int> PruneFinishedPartiesAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        List<Guid> ids;
        using (var scope = scopes.CreateScope())
        {
            ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking()
                .Where(p => p.Status == PartyStatus.Finished && p.PrunedAt == null && p.UpdatedAt < cutoff).Select(p => p.Id).ToListAsync(ct);
        }

        foreach (var id in ids)
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var parties = sp.GetRequiredService<PartyService>();

            // Clear the photo URLs through the engine, like any other change to the game,
            // so the saved state never points at deleted files.
            var snapshot = await parties.LoadAsync(id, ct);
            foreach (var player in snapshot.State.Players.Where(p => p.PhotoUrl is not null))
                await parties.ExecuteAsync(id, (_, t) => new SetPlayerPhoto(t, player.SeatId, null), ct: ct);

            using (await sp.GetRequiredService<PartyLocks>().AcquireAsync(id, ct))
            {
                var seatIds = await db.Seats.Where(s => s.PartyId == id).Select(s => s.Id).ToListAsync(ct);
                await db.PlayerNotes.Where(n => seatIds.Contains(n.SeatId)).ExecuteDeleteAsync(ct);
                await db.Seats.Where(s => s.PartyId == id).ExecuteDeleteAsync(ct);
                await db.Parties.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.PrunedAt, clock.GetUtcNow()), ct);
                await DeletePartyPhotosAsync(sp, id, ct);
                await NotifyRemovedAsync(sp, seatIds);
            }
        }
        return ids.Count;
    }

    /// <summary>Selfies whose party no longer exists (deleted, or uploaded before selfies were linked to parties).</summary>
    private async Task<int> DeleteOrphanPhotosAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Give brand-new uploads an hour: the row is saved a moment before the game state points at it.
        var settled = clock.GetUtcNow().AddHours(-1);
        var orphans = await db.MediaAssets.AsNoTracking()
            .Where(a => a.Kind == MediaKind.Photo && a.CreatedAt < settled && (a.PartyId == null || !db.Parties.Any(p => p.Id == a.PartyId)))
            .Select(a => a.Id).ToListAsync(ct);
        await scope.ServiceProvider.GetRequiredService<MediaService>().DeleteUploadsAsync(orphans, ct);
        return orphans.Count;
    }

    private static async Task DeletePartyPhotosAsync(IServiceProvider sp, Guid partyId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var photos = await db.MediaAssets.AsNoTracking().Where(a => a.PartyId == partyId && a.Kind == MediaKind.Photo).Select(a => a.Id).ToListAsync(ct);
        await sp.GetRequiredService<MediaService>().DeleteUploadsAsync(photos, ct);
    }

    /// <summary>Phones still on the page are told their seat is gone, instead of silently failing.</summary>
    private static async Task NotifyRemovedAsync(IServiceProvider sp, List<Guid> seatIds)
    {
        var parties = sp.GetRequiredService<PartyService>();
        foreach (var seatId in seatIds) await parties.NotifySeatRemovedAsync(seatId);
    }
}
