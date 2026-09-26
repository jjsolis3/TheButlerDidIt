using System.Collections.Concurrent;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Hubs;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Parties;

/// <summary>
/// One lock per party, so commands for the same party run one at a time.
///
/// Why: if the host double-taps "Next" on a slow connection, two requests arrive
/// together. Without a lock both would read act 1, both would write act 2, and one
/// tap would be lost, or worse, clues would drop twice. Different parties never
/// block each other.
///
/// This works because we run a single server instance. With several instances
/// you'd use a distributed lock (Postgres advisory locks or Redis); the xmin
/// concurrency token on Party is the safety net in either case.
/// </summary>
public sealed class PartyLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid partyId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(partyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

public sealed record PartySnapshot(Party Party, GameState State, Scenario Scenario);

/// <summary>
/// The only place game state changes: load, apply a command with the pure
/// engine, save, then push fresh views to every connected screen.
/// </summary>
public sealed class PartyService(
    AppDbContext db,
    ContentCatalog catalog,
    PartyLocks locks,
    IHubContext<PartyHub> hub,
    TimeProvider clock)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    public async Task<PartySnapshot> LoadAsync(Guid partyId, CancellationToken ct = default)
    {
        // If this DbContext already loaded the party earlier in the same request
        // (e.g. an AI question: Begin, then Complete seconds later), EF would hand
        // back that cached copy with the old state. Detach it so we always read the
        // latest row; the xmin token then guards the save.
        foreach (var tracked in db.ChangeTracker.Entries<Party>().Where(e => e.Entity.Id == partyId).ToList())
        {
            tracked.State = EntityState.Detached;
        }

        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct)
            ?? throw new KeyNotFoundException("Party not found.");
        return new PartySnapshot(party, GameJson.Deserialize<GameState>(party.State), await catalog.GetScenarioAsync(db, party.ScenarioId, ct));
    }

    public async Task<Party?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = JoinCodes.Normalize(code);
        return await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Code == normalized, ct);
    }

    /// <param name="makeCommand">Builds the command from the current party and time.</param>
    /// <param name="beforeSave">Extra database changes that must commit together with the new state (e.g. adding a Seat row).</param>
    public Task<PartySnapshot> ExecuteAsync(
        Guid partyId,
        Func<PartySnapshot, DateTimeOffset, Command> makeCommand,
        Action<PartySnapshot>? beforeSave = null,
        CancellationToken ct = default) =>
        ChangeAsync(partyId, (s, now) => Task.FromResult((GameEngine.Apply(s.State, s.Scenario, makeCommand(s, now)), s.Scenario)), beforeSave, ct);

    /// <summary>
    /// Like ExecuteAsync, for changes that need more than one command or that switch the party
    /// to another version of its story (the dealer, when the evening begins). <paramref name="change"/>
    /// runs under the party's lock and returns the new state and the scenario it belongs to.
    /// </summary>
    public async Task<PartySnapshot> ChangeAsync(
        Guid partyId,
        Func<PartySnapshot, DateTimeOffset, Task<(GameState State, Scenario Scenario)>> change,
        Action<PartySnapshot>? beforeSave = null,
        CancellationToken ct = default)
    {
        using (await locks.AcquireAsync(partyId, ct))
        {
            var snapshot = await LoadAsync(partyId, ct);
            var now = Now;
            var (next, scenario) = await change(snapshot, now);
            if (ReferenceEquals(next, snapshot.State)) return snapshot; // nothing changed (e.g. an idle tick)

            var party = snapshot.Party;
            party.ScenarioId = scenario.Id;
            party.State = GameJson.Serialize(next);
            party.Status = next.Phase switch
            {
                Phase.Lobby => PartyStatus.Lobby,
                Phase.Finished => PartyStatus.Finished,
                _ => PartyStatus.InProgress,
            };
            party.NextDueAt = GameEngine.NextDueAt(next);
            party.UpdatedAt = now;

            var updated = snapshot with { State = next, Scenario = scenario };
            beforeSave?.Invoke(updated);
            await db.SaveChangesAsync(ct);

            await BroadcastAsync(updated, now);
            return updated;
        }
    }

    /// <summary>
    /// Sends a complete snapshot to each screen rather than a diff: the stage gets
    /// the public view, and every seat gets its own private view. Full snapshots
    /// are only a few KB, and a phone that missed a message (asleep, bad Wi-Fi)
    /// fixes itself with the next one.
    /// </summary>
    public async Task BroadcastAsync(PartySnapshot s, DateTimeOffset now)
    {
        var tasks = new List<Task>
        {
            hub.Clients.Group(PartyHub.StageGroup(s.Party.Id)).SendAsync("stage", ViewProjector.Stage(s.State, s.Scenario, now)),
        };
        foreach (var player in s.State.Players)
        {
            tasks.Add(hub.Clients.Group(PartyHub.SeatGroup(player.SeatId))
                .SendAsync("player", ViewProjector.Player(s.State, s.Scenario, player.SeatId, now)));
        }
        await Task.WhenAll(tasks);
    }

    public Task NotifySeatRemovedAsync(Guid seatId) =>
        hub.Clients.Group(PartyHub.SeatGroup(seatId)).SendAsync("removed");
}
