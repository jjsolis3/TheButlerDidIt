using System.Security.Claims;
using System.Security.Cryptography;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record RecapPage(RecapView Recap, string HostName, DateTimeOffset PlayedAt);
public sealed record RecapSharing(bool Shared, string? Url, RecapPage Page);

/// <summary>
/// The after-party recap. Private by default: the host can preview it, and chooses
/// whether to share it. A shared recap is reachable only through a link with 128
/// random bits in it, so nobody can find it by guessing.
/// </summary>
public static class RecapEndpoints
{
    private const string NotOver = "The recap appears once the party has ended.";

    public static void MapRecapEndpoints(this IEndpointRouteBuilder app)
    {
        var host = app.MapGroup("/api/parties/{code}/recap").RequireAuthorization(AuthPolicies.Host);

        host.MapGet("/", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, ContentCatalog catalog, CancellationToken ct) =>
        {
            if (await HostsPartyAsync(code, user, parties, ct) is not { } party) return Results.NotFound();
            if (party.Status != PartyStatus.Finished) return Results.Problem(NotOver, statusCode: 409);
            return Results.Ok(new RecapSharing(party.RecapSlug is not null, Url(party.RecapSlug), await PageAsync(party, db, catalog, parties, ct)));
        });

        host.MapPost("/share", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, CancellationToken ct) =>
        {
            if (await HostsPartyAsync(code, user, parties, ct) is not { } party) return Results.NotFound();
            if (party.Status != PartyStatus.Finished) return Results.Problem(NotOver, statusCode: 409);
            var slug = party.RecapSlug ?? NewSlug();
            await db.Parties.Where(p => p.Id == party.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.RecapSlug, slug), ct);
            return Results.Ok(new { Url = Url(slug) });
        });

        host.MapDelete("/share", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, CancellationToken ct) =>
        {
            if (await HostsPartyAsync(code, user, parties, ct) is not { } party) return Results.NotFound();
            await db.Parties.Where(p => p.Id == party.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.RecapSlug, (string?)null), ct);
            return Results.NoContent();
        });

        // ---- Public: anyone with the link. No sign-in, no seat token.
        app.MapGet("/api/recap/{slug}", async (string slug, AppDbContext db, ContentCatalog catalog, PartyService parties, HttpContext http, CancellationToken ct) =>
        {
            var party = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.RecapSlug == slug && p.Status == PartyStatus.Finished, ct);
            if (party is null) return Results.NotFound();
            // Keep shared recaps out of search engines, even if a link gets posted somewhere public.
            http.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Ok(await PageAsync(party, db, catalog, parties, ct));
        });
    }

    private static async Task<Party?> HostsPartyAsync(string code, ClaimsPrincipal user, PartyService parties, CancellationToken ct)
    {
        var party = await parties.FindByCodeAsync(code, ct);
        return party is not null && party.HostUserId == user.FindFirstValue(ClaimTypes.NameIdentifier) ? party : null;
    }

    private static async Task<RecapPage> PageAsync(Party party, AppDbContext db, ContentCatalog catalog, PartyService parties, CancellationToken ct)
    {
        var state = GameJson.Deserialize<GameState>(party.State);
        var scenario = await catalog.GetScenarioAsync(db, party.ScenarioId, ct);
        var hostName = await db.Users.AsNoTracking().Where(u => u.Id == party.HostUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return new RecapPage(ViewProjector.Recap(state, scenario, parties.Now), hostName ?? "The host", party.ScheduledFor ?? party.CreatedAt);
    }

    /// <summary>16 random bytes (128 bits) from the cryptographic generator, as URL-safe text.</summary>
    private static string NewSlug() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Url(string? slug) => slug is null ? null : $"/recap/{slug}";
}
