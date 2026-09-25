using ButlerDidIt.Api.Data;
using ButlerDidIt.Game.Engine;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Parties;

/// <summary>
/// Drops the midway clues on time, even if nobody taps anything.
///
/// Every few seconds it asks the database "which parties have something due?"
/// using the indexed NextDueAt column, so it stays cheap however many old
/// parties are stored. Timer countdowns themselves run in the browsers.
/// </summary>
public sealed class PartyTicker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PartyTicker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let one bad party stop the ticker for everyone else.
                log.LogError(ex, "Party ticker failed");
            }
        }
    }

    public async Task TickOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        List<Guid> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            due = await db.Parties.AsNoTracking()
                .Where(p => p.NextDueAt != null && p.NextDueAt <= now)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        foreach (var partyId in due)
        {
            // A fresh scope (and so a fresh DbContext) per party keeps failures isolated.
            using var scope = scopes.CreateScope();
            var parties = scope.ServiceProvider.GetRequiredService<PartyService>();
            try
            {
                await parties.ExecuteAsync(partyId, (_, t) => new Tick(t), ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Tick failed for party {PartyId}", partyId);
            }
        }
    }
}
