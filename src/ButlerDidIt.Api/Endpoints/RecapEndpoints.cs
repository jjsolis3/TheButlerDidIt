using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

public sealed record RecapPage(RecapView Recap, string HostName, DateTimeOffset PlayedAt);
public sealed record RecapSharing(bool Shared, string? Url, RecapPage Page);

/// <param name="Rank">Where the escape placed on its leaderboard (the same room, length, difficulty and edition, and the
/// same day for a daily challenge), counted when the page is opened. Null when the group didn't escape.</param>
public sealed record EscapeRecapPage(EscapeRecapView Recap, string HostName, int? Rank);
public sealed record EscapeRecapSharing(bool Shared, string? Url, EscapeRecapPage Page);

/// <summary>
/// The after-party recap, for both games. Private by default: the host can preview it, and chooses
/// whether to share it. A shared recap is reachable only through a link with 128 random bits in it,
/// so nobody can find it by guessing. Sharing works the same for every game (it only sets
/// <see cref="Party.RecapSlug"/>); each game has its own page: /recap/… for a mystery, /escape/recap/…
/// for an escape room (#111).
/// </summary>
public static class RecapEndpoints
{
    private const string NotOver = "The recap appears once the party has ended.";

    public static void MapRecapEndpoints(this IEndpointRouteBuilder app)
    {
        var host = app.MapGroup("/api/parties/{code}/recap").RequireAuthorization(AuthPolicies.Host);

        host.MapGet("/", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, ContentCatalog catalog, CancellationToken ct) =>
        {
            if (await HostsPartyAsync(code, user, parties, ct) is not { Kind: GameKind.Mystery } party) return Results.NotFound();
            if (party.Status != PartyStatus.Finished) return Results.Problem(NotOver, statusCode: 409);
            return Results.Ok(new RecapSharing(party.RecapSlug is not null, Url(party.Kind, party.RecapSlug), await PageAsync(party, db, catalog, parties, ct)));
        });

        // Sharing is the same for every game: it only sets or clears the party's random link.
        host.MapPost("/share", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, CancellationToken ct) =>
        {
            if (await HostsPartyAsync(code, user, parties, ct) is not { } party) return Results.NotFound();
            if (party.Status != PartyStatus.Finished) return Results.Problem(NotOver, statusCode: 409);
            var slug = party.RecapSlug ?? NewSlug();
            await db.Parties.Where(p => p.Id == party.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.RecapSlug, slug), ct);
            return Results.Ok(new { Url = Url(party.Kind, slug) });
        });

        host.MapDelete("/share", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, CancellationToken ct) =>
        {
            if (await HostsPartyAsync(code, user, parties, ct) is not { } party) return Results.NotFound();
            await db.Parties.Where(p => p.Id == party.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.RecapSlug, (string?)null), ct);
            return Results.NoContent();
        });

        // The host's preview of an escape room's recap, before (or after) sharing it.
        app.MapGet("/api/parties/{code}/escape-recap", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, EscapeCatalog rooms, CancellationToken ct) =>
        {
            if (await HostsPartyAsync(code, user, parties, ct) is not { Kind: GameKind.EscapeRoom } party) return Results.NotFound();
            if (party.Status != PartyStatus.Finished) return Results.Problem(NotOver, statusCode: 409);
            if (await EscapePageAsync(party, db, rooms, parties.Now, ct) is not { } page) return Results.Problem(RoomGone, statusCode: 404);
            return Results.Ok(new EscapeRecapSharing(party.RecapSlug is not null, Url(party.Kind, party.RecapSlug), page));
        }).RequireAuthorization(AuthPolicies.Host);

        // ---- Public: anyone with the link. No sign-in, no seat token.
        app.MapGet("/api/recap/{slug}", async (string slug, AppDbContext db, ContentCatalog catalog, PartyService parties, HttpContext http, CancellationToken ct) =>
        {
            if (await SharedAsync(db, slug, GameKind.Mystery, ct) is not { } party) return Results.NotFound();
            // Keep shared recaps out of search engines, even if a link gets posted somewhere public.
            http.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Ok(await PageAsync(party, db, catalog, parties, ct));
        });

        app.MapGet("/api/escape-recap/{slug}", async (string slug, AppDbContext db, EscapeCatalog rooms, PartyService parties, HttpContext http, CancellationToken ct) =>
        {
            if (await SharedAsync(db, slug, GameKind.EscapeRoom, ct) is not { } party) return Results.NotFound();
            http.Response.Headers["X-Robots-Tag"] = "noindex";
            return await EscapePageAsync(party, db, rooms, parties.Now, ct) is { } page ? Results.Ok(page) : Results.Problem(RoomGone, statusCode: 404);
        });

        // The escape recap's page itself, with a preview card for chat apps. WhatsApp, Messages and the like read
        // the page's Open Graph tags and never run its JavaScript, so the server writes the tags into index.html.
        app.MapGet("/escape/recap/{slug}", async (string slug, AppDbContext db, EscapeCatalog rooms, PartyService parties, IWebHostEnvironment env,
            IOptions<AppOptions> appOptions, HttpContext http, CancellationToken ct) =>
        {
            var html = await IndexHtmlAsync(env, ct);
            var page = await SharedAsync(db, slug, GameKind.EscapeRoom, ct) is { } party ? await EscapePageAsync(party, db, rooms, parties.Now, ct) : null;
            http.Response.Headers["X-Robots-Tag"] = "noindex";
            // An unknown or unshared link still gets the app, which says "this recap isn't available".
            if (page is not null)
            {
                var origin = appOptions.Value.PublicUrl?.TrimEnd('/') is { Length: > 0 } configured ? configured : $"{http.Request.Scheme}://{http.Request.Host}";
                html = WithPreview(html, page.Recap, $"{origin}/escape/recap/{slug}", page.Recap.CoverUrl is { } cover ? origin + cover : null);
            }
            return Results.Content(html, "text/html; charset=utf-8");
        });
    }

    private const string RoomGone = "This escape room is no longer available, so its recap can't be shown.";

    /// <summary>A party the signed-in user hosts, of any game. Someone else's party looks the same as no party (404).</summary>
    private static async Task<Party?> HostsPartyAsync(string code, ClaimsPrincipal user, PartyService parties, CancellationToken ct)
    {
        var party = await parties.FindByCodeAsync(code, ct);
        return party is not null && party.HostUserId == user.FindFirstValue(ClaimTypes.NameIdentifier) ? party : null;
    }

    /// <summary>A finished party of this game whose host shared its recap under this link.</summary>
    private static Task<Party?> SharedAsync(AppDbContext db, string slug, GameKind kind, CancellationToken ct) =>
        db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.RecapSlug == slug && p.Status == PartyStatus.Finished && p.Kind == kind, ct);

    /// <summary>
    /// The escape recap, built from the saved game and the room (the projector leaves out anything that would spoil it).
    /// Null if the room has since been deleted: the state alone doesn't hold the room's text.
    /// </summary>
    private static async Task<EscapeRecapPage?> EscapePageAsync(Party party, AppDbContext db, EscapeCatalog rooms, DateTimeOffset now, CancellationToken ct)
    {
        if (await rooms.FindAsync(db, party.ScenarioId, ct) is not { } room) return null;
        var state = GameJson.Deserialize<EscapeState>(party.State);
        var art = await rooms.ArtAsync(db, room.Id, now, ct);
        var hostName = await db.Users.AsNoTracking().Where(u => u.Id == party.HostUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return new EscapeRecapPage(EscapeProjector.Recap(state, room, art), hostName ?? "The host", await EscapeResults.RankOfPartyAsync(db, room, party.Id, ct));
    }

    /// <summary>The built app's index.html; a bare page when the front end hasn't been built (some test runs).</summary>
    private static async Task<string> IndexHtmlAsync(IWebHostEnvironment env, CancellationToken ct)
    {
        var file = env.WebRootFileProvider.GetFileInfo("index.html");
        if (!file.Exists || file.PhysicalPath is null)
            return "<!doctype html><html lang=\"en\"><head><meta charset=\"UTF-8\" /><title>The Butler Did It</title></head><body><div id=\"root\"></div></body></html>";
        return await File.ReadAllTextAsync(file.PhysicalPath, ct);
    }

    /// <summary>
    /// Adds the preview card's tags to the page: the result as its title, the team as its description and the
    /// room's cover as its picture. Every value is HTML-encoded, since team names are typed by guests.
    /// </summary>
    internal static string WithPreview(string html, EscapeRecapView recap, string url, string? imageUrl)
    {
        var title = recap.Escaped
            ? $"Escaped {recap.RoomTitle} in {recap.ElapsedSeconds / 60}:{recap.ElapsedSeconds % 60:00}"
            : $"Trapped in {recap.RoomTitle}";
        var team = string.Join(", ", recap.Team.Select(p => p.Name));
        var hints = recap.HintsUsed == 1 ? "1 hint" : $"{recap.HintsUsed} hints";
        var description = $"{(team.Length > 0 ? team + " · " : "")}{recap.SolvedCount} of {recap.PuzzleCount} puzzles · {hints}. An escape room from The Butler Did It.";
        string Meta(string property, string content) => $"<meta property=\"{property}\" content=\"{WebUtility.HtmlEncode(content)}\" />";

        var tags = string.Join("\n    ", new[]
        {
            Meta("og:type", "website"),
            Meta("og:site_name", "The Butler Did It"),
            Meta("og:title", title),
            Meta("og:description", description),
            Meta("og:url", url),
            imageUrl is null ? null : Meta("og:image", imageUrl),
            $"<meta name=\"twitter:card\" content=\"{(imageUrl is null ? "summary" : "summary_large_image")}\" />",
            "<meta name=\"robots\" content=\"noindex\" />",
        }.Where(x => x is not null));

        var at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        html = at < 0 ? tags + html : html[..at] + "    " + tags + "\n  " + html[at..];
        // An evaluator rather than a replacement string, so a "$" in a room's title is never read as "$1".
        var heading = $"<title>{WebUtility.HtmlEncode(title)} · The Butler Did It</title>";
        return System.Text.RegularExpressions.Regex.Replace(html, "<title>.*?</title>", _ => heading);
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

    private static string? Url(GameKind kind, string? slug) => slug is null ? null : kind == GameKind.EscapeRoom ? $"/escape/recap/{slug}" : $"/recap/{slug}";
}
