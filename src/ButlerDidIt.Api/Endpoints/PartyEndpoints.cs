using System.Security.Claims;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Games;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

/// <param name="Version">For stories with several versions: "surprise" to deal one when the evening begins (one this
/// host hasn't played, whose killer is a guest if possible), a version's id to choose it, or null for the original.</param>
/// <param name="Tone">How the AI game master plays it. The content level itself isn't asked for: it's the mystery's own rating.</param>
/// <param name="TailorWithAi">With "surprise": if no version's killer is a guest, let the AI write one.</param>
public sealed record CreatePartyRequest(string ScenarioId, PartyMode Mode, DateTimeOffset? ScheduledFor,
    bool UseAi = true, bool DrinkingPrompts = false, string? Version = null, Tone Tone = Tone.Standard, bool TailorWithAi = false);
/// <summary>Create an escape-room party: which room, and how people will play.</summary>
public sealed record CreateEscapePartyRequest(string RoomId, PartyMode Mode, DateTimeOffset? ScheduledFor = null,
    ButlerDidIt.Api.Escape.PuzzleChoice Puzzles = ButlerDidIt.Api.Escape.PuzzleChoice.Fresh, long? PuzzleSet = null, bool UseAi = true,
    /// <summary>The game's length, one of the room's lengths; null plays the room's own time limit.</summary>
    int? Minutes = null);
public sealed record JoinRequest(string Name);
public sealed record AddSeatRequest(string Name, bool IsLocal);
public sealed record SeatResponse(Guid SeatId, string Token, string Code);

public sealed record PartyInfo(
    string Code,
    string ScenarioId,
    string Title,
    string ThemeSlug,
    PartyMode Mode,
    ContentRating ContentLevel,
    PartyStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ScheduledFor,
    int PlayerCount,
    int MaxPlayers,
    bool IsHost,
    // "Surprise me": the version is dealt when the evening begins. Told to the host only.
    bool DealAtStart,
    // Which game the party plays, so the pages can show the right screens.
    GameKind Kind);

public static class PartyEndpoints
{
    public const string JoinRateLimit = "join";

    public static void MapPartyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/parties");

        // ---- Host: list and create parties
        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext db, GameModules modules, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var parties = await db.Parties.AsNoTracking()
                .Where(p => p.HostUserId == userId && p.HiddenAt == null)
                .OrderByDescending(p => p.CreatedAt)
                .Take(50)
                .ToListAsync(ct);
            var result = new List<PartyInfo>();
            foreach (var p in parties) result.Add(await ToInfo(p, db, modules, isHost: true, ct));
            return result;
        }).RequireAuthorization(AuthPolicies.Host);

        group.MapPost("/", async (CreatePartyRequest req, ClaimsPrincipal user, AppDbContext db, ContentCatalog catalog, GameModules modules, PartyService parties,
            AiGateway ai, ButlerDidIt.Ai.Media.MediaGateway media, IOptions<AiOptions> aiOptions, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            // AI-generated mysteries belong to the host who generated them.
            var row = await db.Scenarios.AsNoTracking().Where(x => x.Id == req.ScenarioId).Select(x => new { x.OwnerUserId, x.ArchivedAt }).FirstOrDefaultAsync(ct);
            if (row is not null && ((row.OwnerUserId is not null && row.OwnerUserId != userId) || row.ArchivedAt is not null))
                return Results.Problem("Pick a mystery to play.", statusCode: 400);

            // Which version of the story to play (same place and cast, different killer). With
            // "Surprise me" the party starts on the original and the version is dealt when the
            // evening begins, once everyone has a character (see PartyDealer).
            var scenarioId = req.ScenarioId;
            var dealAtStart = req.Version == VersionPicker.Surprise;
            if (req.Version is not null && !dealAtStart)
            {
                var versions = await StoryVersions.ForHostAsync(db, catalog, req.ScenarioId, userId, ct);
                if (!versions.Any(v => v.Id == req.Version)) return Results.Problem("That version doesn't belong to this mystery.", statusCode: 400);
                scenarioId = req.Version;
            }

            Scenario scenario;
            try { scenario = await catalog.GetScenarioAsync(db, scenarioId, ct); }
            catch (KeyNotFoundException) { return Results.Problem("Pick a mystery to play.", statusCode: 400); }

            var party = new Party
            {
                Id = Guid.NewGuid(),
                Code = await UniqueCodeAsync(db, ct),
                HostUserId = userId,
                Kind = GameKind.Mystery,
                ScenarioId = scenario.Id,
                Mode = req.Mode,
                // The level is the mystery's own rating, so a party can never be played "above" or "below" its story.
                ContentLevel = scenario.ContentRating,
                Status = PartyStatus.Lobby,
                CreatedAt = parties.Now,
                UpdatedAt = parties.Now,
                ScheduledFor = req.ScheduledFor,
                DealAtStart = dealAtStart,
                TailorWithAi = dealAtStart && req.TailorWithAi,
                State = GameJson.Serialize(new GameState
                {
                    Ai = await AiFeaturesFor(req.UseAi, ai, media, aiOptions.Value, ct),
                    // Drinking games are never offered at Family parties.
                    Options = new PartyOptions
                    {
                        DrinkingPrompts = req.DrinkingPrompts && scenario.ContentRating != ContentRating.Family,
                        Tone = req.Tone,
                    },
                }),
            };
            db.Parties.Add(party);
            await db.SaveChangesAsync(ct);

            // Start preparing voices and pictures for the mystery right away, so they're
            // ready by the time guests arrive. Already-made files are reused.
            if (req.UseAi && (await media.VoicesConfiguredAsync(ct) || await media.ImagesConfiguredAsync(ct)))
                await ButlerDidIt.Api.Media.MediaWorker.EnqueueAsync(db, scenario.Id, userId, clock, ct);
            return Results.Ok(await ToInfo(party, db, modules, isHost: true, ct));
        }).RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(AuthEndpoints.RequireConfirmedHost);

        // ---- Host: start an escape-room party. The room is checked, the clock doesn't start until the host says so.
        group.MapPost("/escape", async (CreateEscapePartyRequest req, ClaimsPrincipal user, AppDbContext db, GameModules modules,
            ButlerDidIt.Api.Escape.EscapeCatalog rooms, PartyService parties, AiGateway ai, ButlerDidIt.Ai.Media.MediaGateway media, TimeProvider clock, CancellationToken ct) =>
        {
            // A hand-written room, or one written for this host: nobody else can start a party with another host's room.
            if (await rooms.FindForHostAsync(db, req.RoomId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, ct) is not { } room)
                return Results.Problem("Pick an escape room to play.", statusCode: 400);
            if (req.Minutes is { } minutes && !room.PlayableLengths.Contains(minutes))
                return Results.Problem($"This room can be played in {string.Join(", ", room.PlayableLengths)} minutes.", statusCode: 400);
            var (seed, daily) = ButlerDidIt.Api.Escape.PuzzleSets.For(req.Puzzles, req.PuzzleSet, parties.Now);
            var party = new Party
            {
                Id = Guid.NewGuid(),
                Code = await UniqueCodeAsync(db, ct),
                HostUserId = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
                Kind = GameKind.EscapeRoom,
                ScenarioId = room.Id,
                Mode = req.Mode,
                ContentLevel = room.ContentRating,
                Status = PartyStatus.Lobby,
                CreatedAt = parties.Now,
                UpdatedAt = parties.Now,
                ScheduledFor = req.ScheduledFor,
                State = GameJson.Serialize(ButlerDidIt.Escape.Engine.EscapeEngine.NewGame(seed, daily, await EscapeAiFor(req.UseAi, ai, media, ct), req.Minutes)),
            };
            db.Parties.Add(party);
            await db.SaveChangesAsync(ct);
            // Paint the room's cover and stages now, so they're ready by the time the clock starts. Made once per room.
            if (req.UseAi && await media.ImagesConfiguredAsync(ct))
                await ButlerDidIt.Api.Media.MediaWorker.EnqueueAsync(db, ButlerDidIt.Api.Escape.EscapeMedia.JobId(room.Id), party.HostUserId, clock, ct);
            return Results.Ok(await ToInfo(party, db, modules, isHost: true, ct));
        }).RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(AuthEndpoints.RequireConfirmedHost);

        // ---- Host: remove a party from their list.
        // An unfinished party (never started, or abandoned halfway) is deleted outright: its code
        // stops working and any phones still open are told their seat is gone. A finished party
        // is only hidden, so its recap link keeps working and "Surprise me" remembers the version.
        group.MapDelete("/{code}", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.NotFound();
            if (party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.Forbid();

            if (party.Status == PartyStatus.Finished)
                await db.Parties.Where(p => p.Id == party.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.HiddenAt, parties.Now), ct);
            else
                await RetentionWorker.DeletePartyAsync(http.RequestServices, party.Id, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.Host);

        // ---- Public: what a guest sees on the join page
        group.MapGet("/{code}", async (string code, ClaimsPrincipal user, AppDbContext db, GameModules modules, PartyService parties, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.NotFound();
            var isHost = user.FindFirstValue(ClaimTypes.NameIdentifier) == party.HostUserId;
            return Results.Ok(await ToInfo(party, db, modules, isHost, ct));
        });

        // ---- Guest: take a seat. Rate-limited so nobody can guess codes by brute force.
        group.MapPost("/{code}/join", async (string code, JoinRequest req, PartyService parties, PartyRuntime runtime, AppDbContext db, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.Problem("No party with that code.", statusCode: 404);
            if (party.Status != PartyStatus.Lobby)
                return Results.Problem("This party has already started. Ask the host to add you as a pass-and-play seat.", statusCode: 409);
            return Results.Ok(await AddSeatAsync(party, req.Name, isHost: false, isLocal: false, runtime, db, ct));
        }).RequireRateLimiting(JoinRateLimit);

        // ---- Host: add themselves as a player, or a pass-and-play seat that lives on this device
        group.MapPost("/{code}/seats", async (string code, AddSeatRequest req, ClaimsPrincipal user, PartyService parties, PartyRuntime runtime, AppDbContext db, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.NotFound();
            if (party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.Forbid();
            var isHostSeat = !req.IsLocal;
            return Results.Ok(await AddSeatAsync(party, req.Name, isHostSeat, req.IsLocal, runtime, db, ct));
        }).RequireAuthorization(AuthPolicies.Host);
    }

    /// <summary>Switch on whichever AI features have a model assigned. With no AI configured, the party plays exactly as before.</summary>
    private static async Task<AiFeatures> AiFeaturesFor(bool useAi, AiGateway ai, ButlerDidIt.Ai.Media.MediaGateway media, AiOptions options, CancellationToken ct)
    {
        if (!useAi) return new AiFeatures();
        var actor = await ai.IsConfiguredAsync(AiRole.Actor, ct);
        var inspector = await ai.IsConfiguredAsync(AiRole.Inspector, ct);
        return new AiFeatures
        {
            NpcQuestions = actor, QuestionsPerAct = Math.Clamp(options.QuestionsPerAct, 1, 20),
            Hints = inspector, HintsPerAct = Math.Clamp(options.HintsPerAct, 1, 10),
            Verdicts = inspector,
            Voices = actor && await media.VoicesConfiguredAsync(ct),
        };
    }

    /// <summary>The escape room's game master: it speaks with the Actor role and writes hints with the Inspector, whichever are set up.</summary>
    private static async Task<ButlerDidIt.Escape.Engine.EscapeAiFeatures> EscapeAiFor(bool useAi, AiGateway ai, ButlerDidIt.Ai.Media.MediaGateway media, CancellationToken ct)
    {
        if (!useAi) return new();
        var actor = await ai.IsConfiguredAsync(AiRole.Actor, ct);
        return new()
        {
            GameMaster = actor,
            Hints = await ai.IsConfiguredAsync(AiRole.Inspector, ct),
            Voice = actor && await media.VoicesConfiguredAsync(ct),
        };
    }

    private static async Task<SeatResponse> AddSeatAsync(Party party, string name, bool isHost, bool isLocal, PartyRuntime runtime, AppDbContext db, CancellationToken ct)
    {
        var token = SeatTokens.NewToken();
        var seatId = Guid.NewGuid();

        // The Seat row (authentication) and the engine's player entry (gameplay)
        // are saved in one SaveChanges, so they can never get out of step.
        await runtime.ExecuteAsync(party.Id,
            (s, now) => s.AddPlayer(now, seatId, name, isHost, isLocal),
            beforeSave: (_, _) => db.Seats.Add(new Seat
            {
                Id = seatId,
                PartyId = party.Id,
                DisplayName = name.Trim(),
                TokenHash = SeatTokens.Hash(token),
                IsHost = isHost,
                IsLocal = isLocal,
                CreatedAt = runtime.Now,
                LastSeenAt = runtime.Now,
            }),
            ct: ct);

        return new SeatResponse(seatId, token, party.Code);
    }

    private static async Task<string> UniqueCodeAsync(AppDbContext db, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = JoinCodes.New();
            if (!await db.Parties.AnyAsync(p => p.Code == code, ct)) return code;
        }
        throw new InvalidOperationException("Could not generate a unique party code.");
    }

    private static async Task<PartyInfo> ToInfo(Party p, AppDbContext db, GameModules modules, bool isHost, CancellationToken ct)
    {
        var game = (await modules.For(p.Kind).LoadAsync(db, p, ct)).Describe(isHost);
        return new PartyInfo(p.Code, game.ContentId, game.Title, game.ThemeSlug, p.Mode, p.ContentLevel, p.Status,
            p.CreatedAt, p.ScheduledFor, game.PlayerCount, game.MaxPlayers, isHost, isHost && p.DealAtStart, p.Kind);
    }
}
