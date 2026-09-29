using System.Collections.Concurrent;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Games;
using ButlerDidIt.Api.Hubs;
using ButlerDidIt.Api.Scale;
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
/// Inside one server a SemaphoreSlim per party does the job. With several servers
/// (Scale:MultiInstance) the command also takes the party's <see cref="ClusterLock"/>,
/// so a tap arriving at another server waits too. The in-process lock is still taken
/// first, so commands queued on the same server don't each hold a database connection
/// while they wait. The xmin concurrency token on Party is the safety net in either case.
/// </summary>
public sealed class PartyLocks(ClusterLock cluster)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid partyId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(partyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return new Releaser(gate, await cluster.AcquireAsync($"party:{partyId}", ct));
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Releaser(SemaphoreSlim gate, IAsyncDisposable shared) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await shared.DisposeAsync();
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

/// <summary>Part of an NPC's answer, pushed while the AI writes it.</summary>
public sealed record NpcTypingEvent(Guid InterrogationId, string Text);

/// <summary>A mystery party as the mystery code sees it: the row, the engine's state and the scenario.</summary>
public sealed record PartySnapshot(Party Party, GameState State, Scenario Scenario);

/// <summary>
/// The only place game state changes, for every kind of game: take the party's lock,
/// load its session, apply a change, save, then push fresh views to every screen.
///
/// It knows nothing about any game's rules; the party's game module (see GameModules)
/// does. Platform features (joining, seats, selfies, the ticker) use it directly with
/// the commands every game supports. Mystery features go through <see cref="PartyService"/>.
/// </summary>
public sealed class PartyRuntime(
    AppDbContext db,
    GameModules modules,
    PartyLocks locks,
    IHubContext<PartyHub> hub,
    TimeProvider clock)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    public async Task<(Party Party, GameSession Session)> LoadAsync(Guid partyId, CancellationToken ct = default)
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
        return (party, await modules.For(party.Kind).LoadAsync(db, party, ct));
    }

    /// <param name="command">One of the commands every game supports, e.g. <c>(s, now) => s.Tick(now)</c>.</param>
    /// <param name="beforeSave">Extra database changes that must commit together with the new state (e.g. adding a Seat row).</param>
    public Task<(Party Party, GameSession Session)> ExecuteAsync(
        Guid partyId,
        Func<GameSession, DateTimeOffset, GameSession> command,
        Action<Party, GameSession>? beforeSave = null,
        CancellationToken ct = default) =>
        ChangeAsync(partyId, (_, s, now) => Task.FromResult(command(s, now)), beforeSave, ct);

    /// <summary>Runs <paramref name="change"/> under the party's lock and saves the session it returns.</summary>
    public async Task<(Party Party, GameSession Session)> ChangeAsync(
        Guid partyId,
        Func<Party, GameSession, DateTimeOffset, Task<GameSession>> change,
        Action<Party, GameSession>? beforeSave = null,
        CancellationToken ct = default)
    {
        await using (await locks.AcquireAsync(partyId, ct))
        {
            var (party, session) = await LoadAsync(partyId, ct);
            var now = Now;
            var next = await change(party, session, now);
            if (ReferenceEquals(next, session)) return (party, session); // nothing changed (e.g. an idle tick)

            party.ScenarioId = next.ContentId;
            party.State = next.Serialize();
            party.Status = next.Status;
            party.NextDueAt = next.NextDueAt;
            party.UpdatedAt = now;

            next.OnSaving(db, party, session);
            beforeSave?.Invoke(party, next);
            await db.SaveChangesAsync(ct);

            await BroadcastAsync(party.Id, next, now);
            return (party, next);
        }
    }

    /// <summary>
    /// Sends a complete snapshot to each screen rather than a diff: the stage gets
    /// the public view, and every seat gets its own private view. Full snapshots
    /// are only a few KB, and a phone that missed a message (asleep, bad Wi-Fi)
    /// fixes itself with the next one.
    /// </summary>
    public async Task BroadcastAsync(Guid partyId, GameSession session, DateTimeOffset now)
    {
        var tasks = new List<Task>
        {
            hub.Clients.Group(PartyHub.StageGroup(partyId)).SendAsync("stage", session.StageView(now)),
        };
        foreach (var seatId in session.SeatIds)
        {
            tasks.Add(hub.Clients.Group(PartyHub.SeatGroup(seatId)).SendAsync("player", session.PlayerView(seatId, now)));
        }
        await Task.WhenAll(tasks);
    }

    /// <summary>A message for the stage and every seat at once, such as an NPC's answer being typed.</summary>
    public Task SendToEveryoneAsync(Guid partyId, IEnumerable<Guid> seatIds, string method, object payload) =>
        hub.Clients.Groups([PartyHub.StageGroup(partyId), .. seatIds.Select(PartyHub.SeatGroup)]).SendAsync(method, payload);

    public Task NotifySeatRemovedAsync(Guid seatId) =>
        hub.Clients.Group(PartyHub.SeatGroup(seatId)).SendAsync("removed");
}

/// <summary>
/// The murder mystery's way into <see cref="PartyRuntime"/>: the same load / change / save cycle,
/// typed with the mystery engine's state, commands and scenario. The dealer, the AI game master,
/// the hub's player and host actions, the kit and the recap all use it.
///
/// A party of another kind is refused with a friendly message (a GameRuleException), so a mystery
/// action can never run against, say, an escape room.
/// </summary>
public sealed class PartyService(AppDbContext db, PartyRuntime runtime, TimeProvider clock)
{
    public DateTimeOffset Now => clock.GetUtcNow();
    public TimeProvider Clock => clock;

    public async Task<PartySnapshot> LoadAsync(Guid partyId, CancellationToken ct = default)
    {
        var (party, session) = await runtime.LoadAsync(partyId, ct);
        var mystery = AsMystery(party, session);
        return new PartySnapshot(party, mystery.State, mystery.Scenario);
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
        var (party, session) = await runtime.ChangeAsync(partyId, async (party, session, now) =>
        {
            var mystery = AsMystery(party, session);
            var (state, scenario) = await change(new PartySnapshot(party, mystery.State, mystery.Scenario), now);
            return mystery.With(state, scenario);
        }, beforeSave is null ? null : (party, next) =>
        {
            var mystery = (MysterySession)next;
            beforeSave(new PartySnapshot(party, mystery.State, mystery.Scenario));
        }, ct);
        var result = (MysterySession)session;
        return new PartySnapshot(party, result.State, result.Scenario);
    }

    /// <summary>
    /// An NPC's answer so far, while the AI is still writing it. Everyone at the party may
    /// see it (the finished answer is public too), so it goes to the stage and every seat.
    /// It isn't saved: the finished answer arrives in the next normal update.
    /// </summary>
    public Task SendNpcTypingAsync(PartySnapshot s, Guid interrogationId, string text) =>
        runtime.SendToEveryoneAsync(s.Party.Id, s.State.Players.Select(p => p.SeatId), "npcTyping", new NpcTypingEvent(interrogationId, text));

    public Task NotifySeatRemovedAsync(Guid seatId) => runtime.NotifySeatRemovedAsync(seatId);

    private static MysterySession AsMystery(Party party, GameSession session) =>
        session as MysterySession ?? throw new GameRuleException($"That isn't part of this {GameModules.Describe(party.Kind)} party.");
}
