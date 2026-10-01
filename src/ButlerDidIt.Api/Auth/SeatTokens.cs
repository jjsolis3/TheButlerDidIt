using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Auth;

public static class SeatTokens
{
    public const string Scheme = "SeatToken";
    public const string SeatIdClaim = "seat_id";
    public const string PartyIdClaim = "party_id";
    public const string WatcherIdClaim = "watcher_id";
    public const string HeaderName = "X-Seat-Token";

    /// <summary>A watcher's token starts with this, so the handler knows which table to look in (#112).</summary>
    public const string WatchPrefix = "w.";

    /// <summary>256 random bits, URL-safe. Unguessable, unlike the 6-letter party code.</summary>
    public static string NewToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>A token for someone watching the TV: just as random, marked so it's never mistaken for a seat's.</summary>
    public static string NewWatchToken() => WatchPrefix + NewToken();

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Guid? SeatId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SeatIdClaim), out var id) ? id : null;

    public static Guid? PartyId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(PartyIdClaim), out var id) ? id : null;

    /// <summary>The watcher this token belongs to: someone watching the TV, with no seat (#112).</summary>
    public static Guid? WatcherId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(WatcherIdClaim), out var id) ? id : null;

    /// <summary>
    /// The name a guest or watcher gave, from their token. Not the principal's Name: on a host's own phone the
    /// sign-in cookie's identity comes first, and its name is their email address.
    /// </summary>
    public static string? GuestName(this ClaimsPrincipal user) =>
        user.Identities.FirstOrDefault(i => i.AuthenticationType == Scheme)?.Name;
}

/// <summary>
/// Signs guests in using their seat token instead of a password.
///
/// Normal HTTP calls send the token in the X-Seat-Token header. For the hub,
/// SignalR sends it as a Bearer token or, because browsers can't set headers on
/// a WebSocket, in the query string as access_token. Tokens in URLs can end up
/// in logs, so we only accept that form on the hub path.
/// </summary>
public sealed class SeatTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = Request.Headers[SeatTokens.HeaderName];
        if (string.IsNullOrEmpty(token) && Request.Path.StartsWithSegments("/hubs"))
        {
            // SignalR sends "Authorization: Bearer <token>" when it can (negotiate,
            // long polling), and falls back to ?access_token= for WebSockets.
            var header = Request.Headers.Authorization.ToString();
            token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : Request.Query["access_token"];
        }
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

        var hash = SeatTokens.Hash(token);
        if (token.StartsWith(SeatTokens.WatchPrefix, StringComparison.Ordinal))
        {
            // A watcher (#112): the party it may watch and who's watching, but no seat, so every player action
            // (they all start with RequireSeat) and every seat endpoint turns it away.
            var watcher = await db.Spectators.AsNoTracking()
                .Where(s => s.TokenHash == hash)
                .Select(s => new { s.Id, s.PartyId, s.DisplayName })
                .FirstOrDefaultAsync();
            if (watcher is null) return AuthenticateResult.Fail("Unknown watch token.");
            var watching = new ClaimsIdentity(
            [
                new Claim(SeatTokens.WatcherIdClaim, watcher.Id.ToString()),
                new Claim(SeatTokens.PartyIdClaim, watcher.PartyId.ToString()),
                new Claim(ClaimTypes.Name, watcher.DisplayName),
            ], SeatTokens.Scheme);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(watching), SeatTokens.Scheme));
        }

        var seat = await db.Seats.AsNoTracking()
            .Where(s => s.TokenHash == hash)
            .Select(s => new { s.Id, s.PartyId, s.DisplayName })
            .FirstOrDefaultAsync();
        if (seat is null) return AuthenticateResult.Fail("Unknown seat token.");

        var identity = new ClaimsIdentity(
        [
            new Claim(SeatTokens.SeatIdClaim, seat.Id.ToString()),
            new Claim(SeatTokens.PartyIdClaim, seat.PartyId.ToString()),
            new Claim(ClaimTypes.Name, seat.DisplayName),
        ], SeatTokens.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SeatTokens.Scheme));
    }
}
