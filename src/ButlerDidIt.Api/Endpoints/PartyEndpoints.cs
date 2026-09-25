using System.Security.Claims;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

public sealed record CreatePartyRequest(string ScenarioId, PartyMode Mode, ContentRating ContentLevel, DateTimeOffset? ScheduledFor, bool UseAi = true, bool DrinkingPrompts = false);
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
    bool IsHost);

public static class PartyEndpoints
{
    public const string JoinRateLimit = "join";

    public static void MapPartyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/parties");

        // ---- Host: list and create parties
        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext db, ContentCatalog catalog, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var parties = await db.Parties.AsNoTracking()
                .Where(p => p.HostUserId == userId)
                .OrderByDescending(p => p.CreatedAt)
                .Take(50)
                .ToListAsync(ct);
            var result = new List<PartyInfo>();
            foreach (var p in parties) result.Add(await ToInfo(p, db, catalog, isHost: true, ct));
            return result;
        }).RequireAuthorization(AuthPolicies.Host);

        group.MapPost("/", async (CreatePartyRequest req, ClaimsPrincipal user, AppDbContext db, ContentCatalog catalog, PartyService parties,
            AiGateway ai, ButlerDidIt.Ai.Media.MediaGateway media, IOptions<AiOptions> aiOptions, TimeProvider clock, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            // AI-generated mysteries belong to the host who generated them.
            var owner = await db.Scenarios.AsNoTracking().Where(x => x.Id == req.ScenarioId).Select(x => x.OwnerUserId).FirstOrDefaultAsync(ct);
            if (owner is not null && owner != userId) return Results.Problem("Pick a mystery to play.", statusCode: 400);

            Scenario scenario;
            try { scenario = await catalog.GetScenarioAsync(db, req.ScenarioId, ct); }
            catch (KeyNotFoundException) { return Results.Problem("Pick a mystery to play.", statusCode: 400); }

            if (scenario.ContentRating > req.ContentLevel)
                return Results.Problem("That mystery is rated Mature. Choose the Mature content level to play it.", statusCode: 400);

            var party = new Party
            {
                Id = Guid.NewGuid(),
                Code = await UniqueCodeAsync(db, ct),
                HostUserId = userId,
                ScenarioId = scenario.Id,
                Mode = req.Mode,
                ContentLevel = req.ContentLevel,
                Status = PartyStatus.Lobby,
                CreatedAt = parties.Now,
                ScheduledFor = req.ScheduledFor,
                State = GameJson.Serialize(new GameState
                {
                    Ai = await AiFeaturesFor(req.UseAi, ai, media, aiOptions.Value, ct),
                    // Drinking games are never offered at Family parties.
                    Options = new PartyOptions { DrinkingPrompts = req.DrinkingPrompts && req.ContentLevel != ContentRating.Family },
                }),
            };
            db.Parties.Add(party);
            await db.SaveChangesAsync(ct);

            // Start preparing voices and pictures for the mystery right away, so they're
            // ready by the time guests arrive. Already-made files are reused.
            if (req.UseAi && (await media.VoicesConfiguredAsync(ct) || await media.ImagesConfiguredAsync(ct)))
                await ButlerDidIt.Api.Media.MediaWorker.EnqueueAsync(db, scenario.Id, userId, clock, ct);
            return Results.Ok(await ToInfo(party, db, catalog, isHost: true, ct));
        }).RequireAuthorization(AuthPolicies.Host);

        // ---- Public: what a guest sees on the join page
        group.MapGet("/{code}", async (string code, ClaimsPrincipal user, AppDbContext db, ContentCatalog catalog, PartyService parties, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.NotFound();
            var isHost = user.FindFirstValue(ClaimTypes.NameIdentifier) == party.HostUserId;
            return Results.Ok(await ToInfo(party, db, catalog, isHost, ct));
        });

        // ---- Guest: take a seat. Rate-limited so nobody can guess codes by brute force.
        group.MapPost("/{code}/join", async (string code, JoinRequest req, PartyService parties, AppDbContext db, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.Problem("No party with that code.", statusCode: 404);
            if (party.Status != PartyStatus.Lobby)
                return Results.Problem("This party has already started. Ask the host to add you as a pass-and-play seat.", statusCode: 409);
            return Results.Ok(await AddSeatAsync(party, req.Name, isHost: false, isLocal: false, parties, db, ct));
        }).RequireRateLimiting(JoinRateLimit);

        // ---- Host: add themselves as a player, or a pass-and-play seat that lives on this device
        group.MapPost("/{code}/seats", async (string code, AddSeatRequest req, ClaimsPrincipal user, PartyService parties, AppDbContext db, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.NotFound();
            if (party.HostUserId != user.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.Forbid();
            var isHostSeat = !req.IsLocal;
            return Results.Ok(await AddSeatAsync(party, req.Name, isHostSeat, req.IsLocal, parties, db, ct));
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

    private static async Task<SeatResponse> AddSeatAsync(Party party, string name, bool isHost, bool isLocal, PartyService parties, AppDbContext db, CancellationToken ct)
    {
        var token = SeatTokens.NewToken();
        var seatId = Guid.NewGuid();

        // The Seat row (authentication) and the engine's player entry (gameplay)
        // are saved in one SaveChanges, so they can never get out of step.
        await parties.ExecuteAsync(party.Id,
            (_, now) => new AddPlayer(now, seatId, name, isHost, isLocal),
            beforeSave: s => db.Seats.Add(new Seat
            {
                Id = seatId,
                PartyId = party.Id,
                DisplayName = name.Trim(),
                TokenHash = SeatTokens.Hash(token),
                IsHost = isHost,
                IsLocal = isLocal,
                CreatedAt = parties.Now,
                LastSeenAt = parties.Now,
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

    private static async Task<PartyInfo> ToInfo(Party p, AppDbContext db, ContentCatalog catalog, bool isHost, CancellationToken ct)
    {
        var scenario = await catalog.GetScenarioAsync(db, p.ScenarioId, ct);
        var state = GameJson.Deserialize<GameState>(p.State);
        return new PartyInfo(p.Code, scenario.Id, scenario.Title, scenario.ThemeSlug, p.Mode, p.ContentLevel, p.Status,
            p.CreatedAt, p.ScheduledFor, state.Players.Count, scenario.MaxPlayers, isHost);
    }
}
