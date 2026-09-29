using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Escape;

/// <summary>One line of a leaderboard. Team names appear only on the host's own escapes.</summary>
public sealed record LeaderboardEntry(int Rank, int Score, int ElapsedSeconds, int HintsUsed, int PlayerCount, DateTimeOffset FinishedAt, bool Mine, string? Team, bool ThisParty);

public sealed record Leaderboard(string RoomId, bool Daily, IReadOnlyList<LeaderboardEntry> Top, LeaderboardEntry? ThisParty, IReadOnlyList<LeaderboardEntry> MyBest);

public static class EscapeEndpoints
{
    private const int TopCount = 10;

    public static void MapEscapeEndpoints(this IEndpointRouteBuilder app, IConfiguration config)
    {
        // The shelf: only what a card shows (no puzzles, no answers), plus the best escape so far.
        app.MapGet("/api/escape-rooms", async (EscapeCatalog rooms, AppDbContext db, CancellationToken ct) =>
        {
            var best = await db.EscapeResults.AsNoTracking().Where(r => r.Escaped).GroupBy(r => r.RoomId)
                .Select(g => new { g.Key, Best = g.Min(r => r.Score) }).ToDictionaryAsync(x => x.Key, x => x.Best, ct);
            return Results.Ok(rooms.Rooms.Select(r => EscapeRoomSummary.For(r, best.TryGetValue(r.Id, out var s) ? s : null)));
        });

        // A room's leaderboard: all time, or today's challenge. Anyone can see times; a signed-in host
        // also sees the names on their own escapes and where a given party of theirs ranked.
        app.MapGet("/api/escape-rooms/{id}/leaderboard", async (string id, bool? daily, string? party, ClaimsPrincipal user,
            EscapeCatalog rooms, AppDbContext db, PartyService parties, CancellationToken ct) =>
        {
            if (rooms.Find(id) is null) return Results.NotFound();
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var today = PuzzleSets.For(PuzzleChoice.Daily, null, parties.Now).Seed;

            var results = db.EscapeResults.AsNoTracking().Where(r => r.RoomId == id && r.Escaped);
            if (daily == true) results = results.Where(r => r.Daily && r.Seed == today);
            var ranked = results.OrderBy(r => r.Score).ThenBy(r => r.FinishedAt);

            Guid? partyId = null;
            if (party is not null && await parties.FindByCodeAsync(party, ct) is { } p && p.HostUserId == hostId) partyId = p.Id;

            LeaderboardEntry Entry(EscapeResult r, int rank) => new(rank, r.Score, r.ElapsedSeconds, r.HintsUsed, r.PlayerCount, r.FinishedAt,
                Mine: hostId is not null && r.HostUserId == hostId,
                Team: hostId is not null && r.HostUserId == hostId ? r.Team : null,
                ThisParty: partyId is not null && r.PartyId == partyId);

            var top = (await ranked.Take(TopCount).ToListAsync(ct)).Select((r, i) => Entry(r, i + 1)).ToList();

            LeaderboardEntry? mine = null;
            if (partyId is not null && await results.FirstOrDefaultAsync(r => r.PartyId == partyId, ct) is { } own)
            {
                var better = await results.CountAsync(r => r.Score < own.Score || (r.Score == own.Score && r.FinishedAt < own.FinishedAt), ct);
                mine = Entry(own, better + 1);
            }

            var myBest = hostId is null ? [] : (await results.Where(r => r.HostUserId == hostId).OrderBy(r => r.Score).Take(5).ToListAsync(ct))
                .Select(r => Entry(r, 0)).ToList();
            return Results.Ok(new Leaderboard(id, daily == true, top, mine, myBest));
        });

        // For the end-to-end tests only (Escape:ExposeAnswersForTests): the answers of a party's puzzle set,
        // so a browser test can play a room whose codes are different every time. Never switched on in production.
        if (config.GetValue<bool>("Escape:ExposeAnswersForTests"))
        {
            app.MapGet("/api/parties/{code}/escape-answers", async (string code, ClaimsPrincipal user, PartyService parties, EscapeCatalog rooms, CancellationToken ct) =>
            {
                var party = await parties.FindByCodeAsync(code, ct);
                if (party is null || party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier) || rooms.Find(party.ScenarioId) is not { } room)
                    return Results.NotFound();
                var concrete = EscapeEngine.RoomFor(GameJson.Deserialize<EscapeState>(party.State), room);
                return Results.Ok(concrete.Puzzles.ToDictionary(p => p.Id, p => p.Answers.FirstOrDefault()));
            }).RequireAuthorization(AuthPolicies.Host);
        }
    }
}
