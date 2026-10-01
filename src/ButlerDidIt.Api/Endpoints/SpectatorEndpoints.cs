using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Parties;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record WatchRequest(string Name);
public sealed record WatchResponse(Guid WatcherId, string Token, string Code);
public sealed record SpectatorView(Guid Id, string Name, DateTimeOffset JoinedAt);
public sealed record SpectatorList(bool Allow, IReadOnlyList<SpectatorView> Watching);
public sealed record AllowSpectatorsRequest(bool Allow);

/// <summary>
/// Spectator mode (#112): people watch a party's TV on their own phone, without a seat.
///
/// <list type="bullet">
/// <item>Anyone with the party code can watch, before or during the game, like joining the lobby. The host can
/// switch it off, which also sends everyone watching away.</item>
/// <item>A watcher's token (<see cref="SeatTokens.NewWatchToken"/>) opens only the TV's view and the cheers.</item>
/// <item>The host sees who's watching and can remove anyone: their token stops working at once.</item>
/// </list>
/// </summary>
public static class SpectatorEndpoints
{
    public static void MapSpectatorEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Anyone with the code: watch. Rate-limited like joining, so codes can't be guessed by brute force.
        app.MapPost("/api/parties/{code}/watch", async (string code, WatchRequest req, PartyService parties, AppDbContext db, Audience audience, CancellationToken ct) =>
        {
            var party = await parties.FindByCodeAsync(code, ct);
            if (party is null) return Results.Problem("No party with that code.", statusCode: 404);
            if (party.Status == PartyStatus.Finished) return Results.Problem("This party has finished.", statusCode: 409);
            if (!party.AllowSpectators) return Results.Problem("The host isn't letting anyone watch this party.", statusCode: 403);
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 30) return Results.Problem("Pick a name of 1–30 characters.", statusCode: 400);
            if (await db.Spectators.CountAsync(s => s.PartyId == party.Id, ct) >= Audience.MaxWatchers)
                return Results.Problem($"{Audience.MaxWatchers} people are already watching this party.", statusCode: 409);

            var token = SeatTokens.NewWatchToken();
            var watcher = new Spectator
            {
                Id = Guid.NewGuid(), PartyId = party.Id, DisplayName = name, TokenHash = SeatTokens.Hash(token),
                CreatedAt = parties.Now, LastSeenAt = parties.Now,
            };
            db.Spectators.Add(watcher);
            await db.SaveChangesAsync(ct);
            await audience.ChangedAsync(party.Id);
            return Results.Ok(new WatchResponse(watcher.Id, token, party.Code));
        }).RequireRateLimiting(PartyEndpoints.JoinRateLimit);

        // ---- The host: who's watching, remove someone, switch watching on or off.
        var host = app.MapGroup("/api/parties/{code}/spectators").RequireAuthorization(AuthPolicies.Host);

        host.MapGet("/", async (string code, ClaimsPrincipal user, PartyService parties, AppDbContext db, CancellationToken ct) =>
            await HostsAsync(code, user, parties, ct) is { } party ? Results.Ok(await ListAsync(db, party, ct)) : Results.NotFound());

        host.MapDelete("/{id:guid}", async (string code, Guid id, ClaimsPrincipal user, PartyService parties, AppDbContext db, Audience audience, CancellationToken ct) =>
        {
            if (await HostsAsync(code, user, parties, ct) is not { } party) return Results.NotFound();
            await audience.RemoveAsync(db, party.Id, id, ct);
            return Results.NoContent();
        });

        host.MapPut("/", async (string code, AllowSpectatorsRequest req, ClaimsPrincipal user, PartyService parties, AppDbContext db, Audience audience, CancellationToken ct) =>
        {
            if (await HostsAsync(code, user, parties, ct) is not { } party) return Results.NotFound();
            await db.Parties.Where(p => p.Id == party.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.AllowSpectators, req.Allow), ct);
            party.AllowSpectators = req.Allow;
            // Switching it off sends everyone watching away, rather than leaving them on until they next reconnect.
            if (!req.Allow) await audience.RemoveAsync(db, party.Id, null, ct);
            return Results.Ok(await ListAsync(db, party, ct));
        });
    }

    /// <summary>A party the signed-in user hosts. Someone else's party looks the same as no party (404).</summary>
    private static async Task<Party?> HostsAsync(string code, ClaimsPrincipal user, PartyService parties, CancellationToken ct)
    {
        var party = await parties.FindByCodeAsync(code, ct);
        return party is not null && party.HostUserId == user.FindFirstValue(ClaimTypes.NameIdentifier) ? party : null;
    }

    private static async Task<SpectatorList> ListAsync(AppDbContext db, Party party, CancellationToken ct)
    {
        var watching = await db.Spectators.AsNoTracking().Where(s => s.PartyId == party.Id).OrderBy(s => s.CreatedAt)
            .Select(s => new SpectatorView(s.Id, s.DisplayName, s.CreatedAt)).ToListAsync(ct);
        return new SpectatorList(party.AllowSpectators, watching);
    }
}
