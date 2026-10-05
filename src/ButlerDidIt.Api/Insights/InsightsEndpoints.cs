using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Insights;

public sealed record FeedbackRequest(int Rating, FeedbackDifficulty Difficulty, string? Comment);

/// <summary>For the phone: whether it can ask yet, whether it may offer a comment box, and what this guest said.</summary>
public sealed record SeatFeedbackView(bool Open, bool CommentsAllowed, FeedbackRequest? Given);

public sealed record InsightsView(
    GameKind Kind,
    string ContentId,
    string Title,
    int Plays,
    DateTimeOffset? LastPlayed,
    int AverageMinutes,
    double AveragePlayers,
    RatingSummary Rating,
    DifficultyVotes Difficulty,
    IReadOnlyList<FeedbackComment> Comments,
    MysteryInsights? Mystery,
    EscapeInsights? Escape);

/// <param name="Stars">How many gave 1, 2, 3, 4 and 5 stars.</param>
public sealed record RatingSummary(double? Average, int Count, IReadOnlyList<int> Stars);
public sealed record DifficultyVotes(int TooEasy, int JustRight, int TooHard);
public sealed record FeedbackComment(int Rating, FeedbackDifficulty Difficulty, string Comment, DateTimeOffset At);

/// <param name="Accusers">Guests who accused someone, over every game.</param>
/// <param name="Correct">Of them, those who named the killer.</param>
public sealed record MysteryInsights(int Accusers, int Correct, IReadOnlyList<VersionInsights> Versions);

/// <summary>One version of the mystery: each has its own killer, so who gets accused only means something per version.</summary>
public sealed record VersionInsights(string Id, string Label, int Plays, int Accusers, int Correct, string KillerName, IReadOnlyList<AccusedCount> Accused);
public sealed record AccusedCount(string CharacterId, string Name, int Count, bool Killer);

/// <param name="Escaped">Games the group got out of.</param>
public sealed record EscapeInsights(int Escaped, IReadOnlyList<PuzzleInsights> Puzzles);

/// <summary>
/// One puzzle over every game that played it. <paramref name="AverageSeconds"/>: from its stage opening to its solving,
/// in the games that solved it. <paramref name="HintRate"/>: the share of games that bought a hint for it.
/// <paramref name="Stuck"/>: games that ran out of time with it unsolved in front of the group.
/// </summary>
public sealed record PuzzleInsights(string PuzzleId, string Title, string StageTitle, int Plays, int Solved, int? AverageSeconds, double HintRate, int Stuck);

/// <summary>A card's summary on My mysteries or My escape rooms.</summary>
public sealed record PlaySummary(int Plays, double? Rating, int Ratings);

/// <summary>
/// Guest feedback and the content's insights (#130): which mysteries and rooms work, and where groups get stuck.
///
/// Guests answer from their seat once the game is over. The insights are for whoever can edit the content: its owner,
/// or the admin (who alone looks after the built-in ones), the same rule as the editors. Anyone else gets a 404, so
/// nobody can find out what exists.
/// </summary>
public static class InsightsEndpoints
{
    public const int MaxComment = 280;
    private const int CommentsShown = 20;

    public static void MapInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Guests: the end-of-game card.
        app.MapGet("/api/seat/feedback", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (user.SeatId() is not { } seatId || user.PartyId() is not { } partyId) return Results.Unauthorized();
            var party = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partyId, ct);
            if (party is null) return Results.NotFound();
            var given = await db.PlayFeedback.AsNoTracking().FirstOrDefaultAsync(f => f.PartyId == partyId && f.SeatId == seatId, ct);
            return Results.Ok(new SeatFeedbackView(GameOver(party), CommentsAllowed(party),
                given is null ? null : new FeedbackRequest(given.Rating, given.Difficulty, given.Comment)));
        }).RequireAuthorization(AuthPolicies.Seat);

        app.MapPost("/api/seat/feedback", async (FeedbackRequest req, ClaimsPrincipal user, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            // A watcher's token has no seat: watchers cheer, they don't rate.
            if (user.SeatId() is not { } seatId || user.PartyId() is not { } partyId) return Results.Unauthorized();
            var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct);
            if (party is null) return Results.NotFound();
            if (!GameOver(party)) return Results.Problem("You can rate the game once it's over.", statusCode: 409);
            if (req.Rating is < 1 or > 5) return Results.Problem("Give it one to five stars.", statusCode: 400);
            if (!Enum.IsDefined(req.Difficulty)) return Results.Problem("Was it too easy, just right or too hard?", statusCode: 400);
            var comment = CommentsAllowed(party) && !string.IsNullOrWhiteSpace(req.Comment) ? req.Comment.Trim() : null;
            if (comment?.Length > MaxComment) return Results.Problem($"Keep it to {MaxComment} characters.", statusCode: 400);

            var row = await db.PlayFeedback.FirstOrDefaultAsync(f => f.PartyId == partyId && f.SeatId == seatId, ct);
            if (row is null)
            {
                row = new PlayFeedback { PartyId = partyId, SeatId = seatId, Kind = party.Kind, ContentId = party.ScenarioId, HostUserId = party.HostUserId };
                db.PlayFeedback.Add(row);
            }
            row.Rating = req.Rating;
            row.Difficulty = req.Difficulty;
            row.Comment = comment;
            row.CreatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new SeatFeedbackView(true, CommentsAllowed(party), new FeedbackRequest(row.Rating, row.Difficulty, row.Comment)));
        }).RequireAuthorization(AuthPolicies.Seat);

        // ---- Owners and the admin: what happened in every game of one mystery or room.
        var group = app.MapGroup("/api/insights").RequireAuthorization(AuthPolicies.Host);
        group.MapGet("/mystery/{id}", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            var row = await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.VariantOf == null, ct);
            if (row is null || !ScenarioEditorEndpoints.CanRead(user, row)) return Results.NotFound();
            var family = await db.Scenarios.AsNoTracking().Where(s => s.Id == id || s.VariantOf == id).Select(s => new { s.Id, s.Document }).ToListAsync(ct);
            var versions = family.ToDictionary(f => f.Id, f => GameJson.Deserialize<Scenario>(f.Document));
            var ids = versions.Keys.ToList();
            var records = await db.PlayRecords.AsNoTracking().Where(r => r.Kind == GameKind.Mystery && ids.Contains(r.ContentId)).ToListAsync(ct);
            var feedback = await db.PlayFeedback.AsNoTracking().Where(f => f.Kind == GameKind.Mystery && ids.Contains(f.ContentId)).ToListAsync(ct);
            return Results.Ok(Build(GameKind.Mystery, id, row.Title, records, feedback, Mystery(id, versions, records), null));
        });
        group.MapGet("/escape/{id}", async (string id, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, EscapeCatalog rooms, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.NotFound();
            // A built-in room is the admin's to look after; a host's room, its owner's (or the admin's).
            EscapeRoom? room = rooms.Find(id) is { } builtIn ? (user.IsAdmin ? builtIn : null)
                : await db.EscapeRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) is { } owned && (owned.OwnerUserId == user.Id || user.IsAdmin)
                    ? GameJson.Deserialize<EscapeRoom>(owned.Document) : null;
            if (room is null) return Results.NotFound();
            var records = await db.PlayRecords.AsNoTracking().Where(r => r.Kind == GameKind.EscapeRoom && r.ContentId == id).ToListAsync(ct);
            var feedback = await db.PlayFeedback.AsNoTracking().Where(f => f.Kind == GameKind.EscapeRoom && f.ContentId == id).ToListAsync(ct);
            return Results.Ok(Build(GameKind.EscapeRoom, id, room.Title, records, feedback, null, Escape(room, records)));
        });
    }

    /// <summary>Mysteries: from the reveal on (the accusations are in). Escape rooms: once the clock has stopped.</summary>
    private static bool GameOver(Party party) => party.Kind switch
    {
        GameKind.Mystery => GameJson.Deserialize<GameState>(party.State).Phase is Phase.Reveal or Phase.Awards or Phase.Finished,
        GameKind.EscapeRoom => GameJson.Deserialize<EscapeState>(party.State).Phase is EscapePhase.Escaped or EscapePhase.Failed,
        _ => false,
    };

    /// <summary>Family games may be played by children: they're asked for stars and difficulty, never free text.</summary>
    private static bool CommentsAllowed(Party party) => party.ContentLevel != ContentRating.Family;

    private static InsightsView Build(GameKind kind, string id, string title, List<PlayRecord> records, List<PlayFeedback> feedback,
        MysteryInsights? mystery, EscapeInsights? escape) => new(
        kind, id, title,
        records.Count,
        records.Count == 0 ? null : records.Max(r => r.FinishedAt),
        records.Count == 0 ? 0 : (int)Math.Round(records.Average(r => r.DurationSeconds) / 60),
        records.Count == 0 ? 0 : Math.Round(records.Average(r => r.PlayerCount), 1),
        new RatingSummary(feedback.Count == 0 ? null : Math.Round(feedback.Average(f => f.Rating), 1), feedback.Count,
            Enumerable.Range(1, 5).Select(star => feedback.Count(f => f.Rating == star)).ToList()),
        new DifficultyVotes(feedback.Count(f => f.Difficulty == FeedbackDifficulty.TooEasy), feedback.Count(f => f.Difficulty == FeedbackDifficulty.JustRight),
            feedback.Count(f => f.Difficulty == FeedbackDifficulty.TooHard)),
        feedback.Where(f => f.Comment is not null).OrderByDescending(f => f.CreatedAt).Take(CommentsShown)
            .Select(f => new FeedbackComment(f.Rating, f.Difficulty, f.Comment!, f.CreatedAt)).ToList(),
        mystery, escape);

    private static MysteryInsights Mystery(string originalId, Dictionary<string, Scenario> versions, List<PlayRecord> records)
    {
        var list = versions.OrderBy(v => v.Key == originalId ? "" : v.Key).Select(v =>
        {
            var played = records.Where(r => r.ContentId == v.Key).ToList();
            var accused = new Dictionary<string, int>();
            foreach (var r in played)
                foreach (var (who, count) in GameJson.Deserialize<PlayDetails>(r.Details).Accused ?? [])
                    accused[who] = accused.GetValueOrDefault(who) + count;
            var killer = v.Value.Solution.MurdererId;
            return new VersionInsights(v.Key, VersionLabel(originalId, v.Key, v.Value), played.Count, played.Sum(r => r.Accusers ?? 0), played.Sum(r => r.Correct ?? 0),
                v.Value.FindCharacter(killer)?.Name ?? killer,
                accused.OrderByDescending(a => a.Value)
                    .Select(a => new AccusedCount(a.Key, v.Value.FindCharacter(a.Key)?.Name ?? a.Key, a.Value, a.Key == killer)).ToList());
        }).Where(v => v.Plays > 0 || v.Id == originalId).ToList();
        return new MysteryInsights(list.Sum(v => v.Accusers), list.Sum(v => v.Correct), list);
    }

    /// <summary>"As written" for the mystery itself; a hand-written version's letter (its id ends "--b"); else the AI's.</summary>
    private static string VersionLabel(string originalId, string id, Scenario s)
    {
        if (id == originalId) return "As written";
        var suffix = id.StartsWith(originalId + "--", StringComparison.Ordinal) ? id[(originalId.Length + 2)..] : null;
        return suffix is { Length: 1 } ? $"Version {suffix.ToUpperInvariant()}" : "Written for one party by the AI";
    }

    private static EscapeInsights Escape(EscapeRoom room, List<PlayRecord> records)
    {
        var times = records.SelectMany(r => GameJson.Deserialize<PlayDetails>(r.Details).Puzzles ?? []).ToList();
        // The room's puzzles as it stands now, in stage order. A puzzle an edit removed has nothing left to improve.
        var puzzles = room.Stages.SelectMany(stage => stage.Puzzles.Select(id => (Stage: stage, Puzzle: room.FindPuzzle(id))))
            .Where(x => x.Puzzle is not null)
            .Select(x =>
            {
                var played = times.Where(t => t.PuzzleId == x.Puzzle!.Id).ToList();
                var solved = played.Where(t => t.Seconds is not null).ToList();
                return new PuzzleInsights(x.Puzzle!.Id, x.Puzzle.Title, x.Stage.Title, played.Count, solved.Count,
                    solved.Count == 0 ? null : (int)Math.Round(solved.Average(t => t.Seconds!.Value)),
                    played.Count == 0 ? 0 : Math.Round((double)played.Count(t => t.Hints > 0) / played.Count, 2),
                    played.Count(t => t.Stuck));
            }).ToList();
        return new EscapeInsights(records.Count(r => r.Escaped == true), puzzles);
    }

    /// <summary>
    /// Plays and ratings for each card on My mysteries or My escape rooms. <paramref name="cardOf"/> maps every id that
    /// can be played to its card's id: a mystery's versions add up on the mystery's card.
    /// </summary>
    public static async Task<Dictionary<string, PlaySummary>> SummariesAsync(AppDbContext db, GameKind kind, IReadOnlyDictionary<string, string> cardOf, CancellationToken ct)
    {
        var ids = cardOf.Keys.ToList();
        var plays = await db.PlayRecords.AsNoTracking().Where(r => r.Kind == kind && ids.Contains(r.ContentId))
            .GroupBy(r => r.ContentId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var ratings = await db.PlayFeedback.AsNoTracking().Where(f => f.Kind == kind && ids.Contains(f.ContentId))
            .GroupBy(f => f.ContentId).Select(g => new { g.Key, Sum = g.Sum(f => f.Rating), Count = g.Count() }).ToListAsync(ct);
        return cardOf.Values.Distinct().ToDictionary(card => card, card =>
        {
            var mine = cardOf.Where(c => c.Value == card).Select(c => c.Key).ToHashSet();
            var count = ratings.Where(r => mine.Contains(r.Key)).Sum(r => r.Count);
            return new PlaySummary(plays.Where(p => mine.Contains(p.Key)).Sum(p => p.Count),
                count == 0 ? null : Math.Round((double)ratings.Where(r => mine.Contains(r.Key)).Sum(r => r.Sum) / count, 1), count);
        });
    }
}
