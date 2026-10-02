using System.Security.Claims;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
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

/// <summary>Where a room in My escape rooms comes from.</summary>
public enum EscapeRoomSource
{
    /// <summary>A hand-written room from content/escape.</summary>
    BuiltIn,
    /// <summary>Written by AI for this host.</summary>
    Generated,
    /// <summary>This host's own copy of another room.</summary>
    Copy,
    /// <summary>A room the admin shared with every host.</summary>
    Shared,
}

/// <summary>A room in My escape rooms, with what this host may do to it.</summary>
/// <param name="CanShare">The admin's own room: they can share it with every host.</param>
/// <param name="CanHide">A built-in room, for the admin: they can take it off the shelf.</param>
/// <param name="Hidden">Taken off the shelf by the admin (only the admin's library lists these).</param>
/// <param name="TimesPlayed">How many of this host's parties played it.</param>
/// <param name="InUse">A party is using it now, so it can't be edited or deleted until that ends.</param>
public sealed record EscapeLibraryItem(EscapeRoomSummary Room, EscapeRoomSource Source, bool CanEdit, bool CanShare, bool CanHide, bool Hidden, int TimesPlayed, bool InUse);

/// <summary>The admin's switches, for both games. A field left out stays as it is.</summary>
/// <param name="Shared">On every host's shelf (the admin's own room or mystery).</param>
/// <param name="Hidden">Off the shelf (a built-in room or hand-written mystery).</param>
public sealed record SharingRequest(bool? Shared, bool? Hidden);

public static class EscapeEndpoints
{
    private const int TopCount = 10;

    /// <summary>Makes a shelf card for a room (see <see cref="CardsAsync"/>).</summary>
    private delegate EscapeRoomSummary CardMaker(EscapeRoom room, bool mine = false, bool copied = false, bool shared = false);

    /// <summary>
    /// Card makers for a set of rooms, with the two things that need the database read in one query each: the best
    /// escape (on the room's current edition, at its own length, on Normal; the rest are ranked on their own boards)
    /// and the cover picture, for the rooms that have one.
    /// </summary>
    private static async Task<CardMaker> CardsAsync(AppDbContext db, IReadOnlyList<EscapeRoom> shown, CancellationToken ct)
    {
        var ids = shown.Select(r => r.Id).ToList();
        var best = await db.EscapeResults.AsNoTracking().Where(r => r.Escaped && ids.Contains(r.RoomId)).AtDifficulty(EscapeDifficulty.Normal).GroupBy(r => new { r.RoomId, r.Minutes, r.Edition })
            .Select(g => new { g.Key.RoomId, g.Key.Minutes, g.Key.Edition, Best = g.Min(r => r.Score) }).ToListAsync(ct);
        int? Best(EscapeRoom r) => best.Where(b => b.RoomId == r.Id && (b.Minutes ?? r.TimeLimitMinutes) == r.TimeLimitMinutes && (b.Edition ?? 1) == r.Edition)
            .Min(b => (int?)b.Best);
        var jobIds = ids.Select(EscapeMedia.JobId).ToList();
        var covers = await db.ScenarioMedia.AsNoTracking().Where(m => jobIds.Contains(m.ScenarioId) && m.Key == EscapeArt.Cover)
            .Select(m => new { m.ScenarioId, m.AssetId }).ToListAsync(ct);
        string? Cover(EscapeRoom r) => covers.FirstOrDefault(c => c.ScenarioId == EscapeMedia.JobId(r.Id)) is { } c ? ButlerDidIt.Api.Media.MediaStore.Url(c.AssetId) : null;
        return (room, mine, copied, shared) => EscapeRoomSummary.For(room, Best(room), mine, copied, Cover(room), shared);
    }

    public static void MapEscapeEndpoints(this IEndpointRouteBuilder app, IConfiguration config)
    {
        // The shelf: only what a card shows (no puzzles, no answers), plus the best escape so far.
        // A signed-in host also sees their own rooms (written by AI for them, or their copies), newest first. Then come the
        // rooms the admin shared with every host, and the hand-written ones the admin hasn't taken off the shelf.
        app.MapGet("/api/escape-rooms", async (ClaimsPrincipal user, EscapeCatalog rooms, AppDbContext db, CancellationToken ct) =>
        {
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var mine = hostId is null ? [] : await rooms.OwnedAsync(db, hostId, ct);
            var shared = await rooms.SharedAsync(db, hostId, ct);
            var hidden = await ContentVisibility.HiddenAsync(db, GameKind.EscapeRoom, ct);
            var builtIn = rooms.Rooms.Where(r => !hidden.Contains(r.Id)).ToList();
            var cards = await CardsAsync(db, [.. mine.Select(o => o.Room), .. shared, .. builtIn], ct);
            return Results.Ok(mine.Select(o => cards(o.Room, mine: true, copied: o.Copied, shared: o.Shared))
                .Concat(shared.Select(r => cards(r, shared: true)))
                .Concat(builtIn.Select(r => cards(r))));
        });

        // My escape rooms: everything this host can manage or copy, with what they may do to each.
        // The admin also sees the built-in rooms they took off the shelf, to put them back.
        app.MapGet("/api/escape-rooms/library", async (ClaimsPrincipal user, EscapeCatalog rooms, AppDbContext db, CancellationToken ct) =>
        {
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var admin = await ContentVisibility.IsAdminAsync(db, hostId, ct);
            var mine = await rooms.OwnedAsync(db, hostId, ct);
            var shared = await rooms.SharedAsync(db, hostId, ct);
            var hidden = await ContentVisibility.HiddenAsync(db, GameKind.EscapeRoom, ct);
            var builtIn = rooms.Rooms.Where(r => admin || !hidden.Contains(r.Id)).ToList();
            var cards = await CardsAsync(db, [.. mine.Select(o => o.Room), .. shared, .. builtIn], ct);

            var ids = mine.Select(o => o.Room.Id).Concat(shared.Select(r => r.Id)).Concat(builtIn.Select(r => r.Id)).ToList();
            var played = await db.Parties.AsNoTracking()
                .Where(p => p.Kind == GameKind.EscapeRoom && p.HostUserId == hostId && p.Status != PartyStatus.Lobby && ids.Contains(p.ScenarioId))
                .GroupBy(p => p.ScenarioId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
            var ownIds = mine.Select(o => o.Room.Id).ToList();
            var busy = (await db.Parties.AsNoTracking().Where(p => p.Kind == GameKind.EscapeRoom && p.Status != PartyStatus.Finished && ownIds.Contains(p.ScenarioId))
                .Select(p => p.ScenarioId).Distinct().ToListAsync(ct)).ToHashSet();

            return Results.Ok(mine.Select(o => new EscapeLibraryItem(cards(o.Room, mine: true, copied: o.Copied, shared: o.Shared),
                    o.Copied ? EscapeRoomSource.Copy : EscapeRoomSource.Generated, CanEdit: true, CanShare: admin, CanHide: false, Hidden: false,
                    played.GetValueOrDefault(o.Room.Id), InUse: busy.Contains(o.Room.Id)))
                .Concat(shared.Select(r => new EscapeLibraryItem(cards(r, shared: true), EscapeRoomSource.Shared, CanEdit: false, CanShare: false, CanHide: false,
                    Hidden: false, played.GetValueOrDefault(r.Id), InUse: false)))
                .Concat(builtIn.Select(r => new EscapeLibraryItem(cards(r), EscapeRoomSource.BuiltIn, CanEdit: false, CanShare: false, CanHide: admin,
                    Hidden: hidden.Contains(r.Id), played.GetValueOrDefault(r.Id), InUse: false))));
        }).RequireAuthorization(AuthPolicies.Host);

        // The admin shares one of their own rooms with every host, or takes a built-in room off the shelf (or puts it back).
        app.MapPut("/api/escape-rooms/{id}/sharing", async (string id, SharingRequest req, ClaimsPrincipal user, EscapeCatalog rooms, AppDbContext db,
            TimeProvider clock, CancellationToken ct) =>
        {
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!await ContentVisibility.IsAdminAsync(db, hostId, ct))
                return Results.Problem("Only the admin can share rooms with every host or take them off the shelf.", statusCode: 403);
            if (rooms.Find(id) is not null)
            {
                if (req.Shared is not null) return Results.Problem("Built-in rooms are already on every host's shelf.", statusCode: 400);
                if (req.Hidden is { } hide) await ContentVisibility.SetHiddenAsync(db, GameKind.EscapeRoom, id, hide, clock.GetUtcNow(), ct);
                return Results.NoContent();
            }
            var row = await db.EscapeRooms.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (req.Hidden is not null) return Results.Problem("Only built-in rooms can be taken off the shelf. Stop sharing this one instead.", statusCode: 400);
            if (row.OwnerUserId != hostId) return Results.Problem("Make your own copy of this room first, then share the copy.", statusCode: 400);
            if (req.Shared is { } share)
            {
                row.Shared = share;
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.Host);

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

        // Delete one of this host's own rooms. Its results stay (they hold only times and first names),
        // and a party still playing it is told the room is no longer available.
        app.MapDelete("/api/escape-rooms/{id}", async (string id, ClaimsPrincipal user, EscapeCatalog rooms, AppDbContext db,
            ButlerDidIt.Api.Media.MediaService media, CancellationToken ct) =>
        {
            var hostId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var deleted = await db.EscapeRooms.Where(r => r.Id == id && r.OwnerUserId == hostId).ExecuteDeleteAsync(ct);
            if (deleted == 0) return Results.NotFound();
            // Its pictures, videos and sounds go too (files a copy still uses stay for the copy).
            await EscapeMediaEndpoints.ForgetRoomAsync(db, media, [id], ct);
            rooms.Forget(id);
            rooms.ForgetArt(id);
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
