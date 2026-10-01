using System.Security.Claims;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Ai;
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

public sealed record Leaderboard(string RoomId, bool Daily, int Minutes, EscapeDifficulty Difficulty, int Edition, IReadOnlyList<LeaderboardEntry> Top, LeaderboardEntry? ThisParty, IReadOnlyList<LeaderboardEntry> MyBest);

/// <param name="Minutes">The clock: 30, 45 or 60.</param>
public sealed record EscapeRoomGenerateRequest(string? Theme, ButlerDidIt.Game.Scenarios.ContentRating ContentRating, int Minutes);

public static class EscapeEndpoints
{
    private const int TopCount = 10;

    public static void MapEscapeEndpoints(this IEndpointRouteBuilder app, IConfiguration config)
    {
        // The shelf: only what a card shows (no puzzles, no answers), plus the best escape so far.
        // A signed-in host also sees the rooms written for them, newest first, before the hand-written ones.
        app.MapGet("/api/escape-rooms", async (ClaimsPrincipal user, EscapeCatalog rooms, AppDbContext db, CancellationToken ct) =>
        {
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var mine = hostId is null ? [] : await rooms.OwnedAsync(db, hostId, ct);
            var ids = mine.Concat(rooms.Rooms).Select(r => r.Id).ToList();
            var best = await db.EscapeResults.AsNoTracking().Where(r => r.Escaped && ids.Contains(r.RoomId)).AtDifficulty(EscapeDifficulty.Normal).GroupBy(r => new { r.RoomId, r.Minutes, r.Edition })
                .Select(g => new { g.Key.RoomId, g.Key.Minutes, g.Key.Edition, Best = g.Min(r => r.Score) }).ToListAsync(ct);
            // The card's best escape is on the room's current edition, at its own length on Normal; the rest are ranked on their own.
            int? Best(EscapeRoom r) => best.Where(b => b.RoomId == r.Id && (b.Minutes ?? r.TimeLimitMinutes) == r.TimeLimitMinutes && (b.Edition ?? 1) == r.Edition)
                .Min(b => (int?)b.Best);
            // Cover pictures, for the rooms the media pipeline has painted (one query for the whole shelf).
            var jobIds = ids.Select(EscapeMedia.JobId).ToList();
            var covers = await db.ScenarioMedia.AsNoTracking().Where(m => jobIds.Contains(m.ScenarioId) && m.Key == EscapeArt.Cover)
                .Select(m => new { m.ScenarioId, m.AssetId }).ToListAsync(ct);
            string? Cover(EscapeRoom r) => covers.FirstOrDefault(c => c.ScenarioId == EscapeMedia.JobId(r.Id)) is { } c ? ButlerDidIt.Api.Media.MediaStore.Url(c.AssetId) : null;
            return Results.Ok(mine.Select(r => EscapeRoomSummary.For(r, Best(r), generated: true, Cover(r)))
                .Concat(rooms.Rooms.Select(r => EscapeRoomSummary.For(r, Best(r), coverUrl: Cover(r)))));
        });

        // Write a new room from a theme. It runs in the background (GenerationWorker); the page
        // follows it with GET /api/generation/{id}, which ends with the new room's id.
        app.MapPost("/api/escape-rooms/generate", async (EscapeRoomGenerateRequest req, ClaimsPrincipal user, AppDbContext db, AiGateway ai, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await ai.IsConfiguredAsync(AiRole.Storyteller, ct))
                return Results.Problem("No Storyteller AI is set up yet. An admin can configure one under Admin → AI.", statusCode: 400);
            var theme = req.Theme?.Trim() ?? "";
            if (theme.Length is 0 or > 120) return Results.Problem("Describe the room's theme in 1–120 characters.", statusCode: 400);
            if (req.Minutes is not (30 or 45 or 60)) return Results.Problem("Choose 30, 45 or 60 minutes.", statusCode: 400);

            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            // One job at a time per host keeps costs predictable (mysteries and rooms alike).
            if (await db.GenerationJobs.AnyAsync(j => j.HostUserId == userId && (j.Status == GenerationStatus.Queued || j.Status == GenerationStatus.Running), ct))
                return Results.Problem("You already have something being written. Please wait for it to finish.", statusCode: 409);

            var now = clock.GetUtcNow();
            var job = new GenerationJobEntity
            {
                Id = Guid.NewGuid(), HostUserId = userId, ThemeSlug = "escape", Kind = GenerationKind.EscapeRoom,
                Request = GameJson.Serialize(new EscapeRoomRequest(theme, req.ContentRating, req.Minutes)),
                Status = GenerationStatus.Queued, Progress = "Waiting to start…", CreatedAt = now, UpdatedAt = now,
            };
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(ct);
            return Results.Ok(GenerationEndpoints.ToView(job));
        }).RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(ButlerDidIt.Api.Endpoints.AuthEndpoints.RequireConfirmedHost)
          .AddEndpointFilter(ButlerDidIt.Api.Plans.Access.RequireGame(GameKind.EscapeRoom));

        // Delete a room written for this host. Its results stay (they hold only times and first names),
        // and a party still playing it is told the room is no longer available.
        app.MapDelete("/api/escape-rooms/{id}", async (string id, ClaimsPrincipal user, EscapeCatalog rooms, AppDbContext db, CancellationToken ct) =>
        {
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var deleted = await db.EscapeRooms.Where(r => r.Id == id && r.OwnerUserId == hostId).ExecuteDeleteAsync(ct);
            if (deleted == 0) return Results.NotFound();
            rooms.Forget(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.Host);

        // A room's leaderboard: all time, or today's challenge. Anyone can see times; a signed-in host
        // also sees the names on their own escapes and where a given party of theirs ranked.
        // Games of different lengths and difficulties play different puzzles, so each has its own board
        // (the room's own length on Normal by default).
        app.MapGet("/api/escape-rooms/{id}/leaderboard", async (string id, bool? daily, string? party, int? minutes, string? difficulty, ClaimsPrincipal user,
            EscapeCatalog rooms, AppDbContext db, PartyService parties, CancellationToken ct) =>
        {
            if (await rooms.FindAsync(db, id, ct) is not { } room) return Results.NotFound();
            var level = EscapeDifficulty.Normal;
            if (difficulty is not null && (!Enum.TryParse(difficulty, ignoreCase: true, out level) || !Enum.IsDefined(level) || int.TryParse(difficulty, out _)))
                return Results.Problem("The difficulty is easy, normal or hard.", statusCode: 400);
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var today = PuzzleSets.For(PuzzleChoice.Daily, null, parties.Now).Seed;

            var results = db.EscapeResults.AsNoTracking().Where(r => r.RoomId == id && r.Escaped).AtLength(room, minutes ?? room.TimeLimitMinutes).AtDifficulty(level).AtEdition(room);
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
                mine = Entry(own, await results.RankInAsync(own, ct));

            var myBest = hostId is null ? [] : (await results.Where(r => r.HostUserId == hostId).OrderBy(r => r.Score).Take(5).ToListAsync(ct))
                .Select(r => Entry(r, 0)).ToList();
            return Results.Ok(new Leaderboard(id, daily == true, minutes ?? room.TimeLimitMinutes, level, room.Edition, top, mine, myBest));
        });

        // For the end-to-end tests only (Escape:ExposeAnswersForTests): the answers of a party's puzzle set,
        // so a browser test can play a room whose codes are different every time. Never switched on in production.
        if (config.GetValue<bool>("Escape:ExposeAnswersForTests"))
        {
            app.MapGet("/api/parties/{code}/escape-answers", async (string code, ClaimsPrincipal user, PartyService parties, EscapeCatalog rooms, AppDbContext db, CancellationToken ct) =>
            {
                var party = await parties.FindByCodeAsync(code, ct);
                if (party is null || party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier) || await rooms.FindAsync(db, party.ScenarioId, ct) is not { } room)
                    return Results.NotFound();
                var concrete = EscapeEngine.RoomFor(GameJson.Deserialize<EscapeState>(party.State), room);
                return Results.Ok(concrete.Puzzles.ToDictionary(p => p.Id, p => p.Answers.FirstOrDefault()));
            }).RequireAuthorization(AuthPolicies.Host);
        }
    }
}
