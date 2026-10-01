using System.Collections.Concurrent;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Parties;

/// <summary>
/// Spectator mode's messages (#112). Like the "jobs" signal, "audience" carries no data: the host's TV
/// fetches the list of watchers through its access-checked endpoint. "removed" tells a watcher's own
/// screen it can no longer watch; its token is already gone, so it can't reconnect.
/// </summary>
public sealed class Audience(IHubContext<PartyHub> hub, ILogger<Audience> log)
{
    /// <summary>The most people who can watch one party. Each one gets every update, so it's kept to a big family's worth.</summary>
    public const int MaxWatchers = 50;

    /// <summary>Someone started or stopped watching: the host's TV refreshes its list.</summary>
    public Task ChangedAsync(Guid partyId) => SendAsync(PartyHub.StageGroup(partyId), "audience");

    public async Task RemovedAsync(IEnumerable<Guid> watcherIds)
    {
        foreach (var id in watcherIds) await SendAsync(PartyHub.WatcherGroup(id), "removed");
    }

    /// <summary>
    /// Removes a party's watchers (one, or all of them) and tells their screens. Used when the host removes
    /// someone or switches watching off, and by clean-up when the party goes.
    /// </summary>
    public async Task RemoveAsync(AppDbContext db, Guid partyId, Guid? watcherId, CancellationToken ct)
    {
        var rows = db.Spectators.Where(s => s.PartyId == partyId && (watcherId == null || s.Id == watcherId));
        var ids = await rows.Select(s => s.Id).ToListAsync(ct);
        if (ids.Count == 0) return;
        await rows.ExecuteDeleteAsync(ct);
        await RemovedAsync(ids);
        await ChangedAsync(partyId);
    }

    private async Task SendAsync(string group, string message)
    {
        try
        {
            await hub.Clients.Group(group).SendAsync(message);
        }
        catch (Exception ex)
        {
            // A lost signal only means the host's list refreshes a little later (or a removed watcher sees it on reconnect).
            log.LogDebug(ex, "Could not send {Message}", message);
        }
    }
}

/// <summary>
/// Keeps cheers to a pleasant trickle (#112): each person can cheer once every couple of seconds, and a party's
/// TV gets a handful a second at most, however many are watching. In memory, per server: with several servers
/// the limits are per server, which is still a trickle.
/// </summary>
public sealed class CheerLimiter(TimeProvider clock)
{
    public static readonly TimeSpan PerPerson = TimeSpan.FromSeconds(2);
    public const int PerPartyPerSecond = 5;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _people = new();
    private readonly ConcurrentDictionary<Guid, (long Second, int Count)> _parties = new();

    /// <summary>Whether this cheer goes through. One that doesn't is simply dropped: it's only a cheer.</summary>
    public bool TryCheer(Guid partyId, Guid who)
    {
        var now = clock.GetUtcNow();
        if (_people.TryGetValue(who, out var last) && now - last < PerPerson) return false;

        var second = now.ToUnixTimeSeconds();
        // AddOrUpdate may run these functions more than once when two cheers race, so each run sets `allowed` itself.
        var allowed = true;
        _parties.AddOrUpdate(partyId, _ =>
        {
            allowed = true;
            return (second, 1);
        }, (_, c) =>
        {
            allowed = c.Second != second || c.Count < PerPartyPerSecond;
            return c.Second != second ? (second, 1) : allowed ? (second, c.Count + 1) : c;
        });
        if (!allowed) return false;
        _people[who] = now;

        // The maps only need recent entries; clear old ones now and then so they can't grow for ever.
        if (_people.Count > 10_000) foreach (var (key, at) in _people) if (now - at > PerPerson) _people.TryRemove(key, out _);
        if (_parties.Count > 10_000) foreach (var (key, c) in _parties) if (c.Second != second) _parties.TryRemove(key, out _);
        return true;
    }
}
