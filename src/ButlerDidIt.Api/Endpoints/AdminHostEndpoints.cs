using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Plans;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

/// <param name="Joined">When the account was made (see <see cref="AppUser.CreatedAt"/>).</param>
/// <param name="LastParty">When they last created a party, a sign of whether they still play.</param>
public sealed record HostView(string Id, string DisplayName, string Email, bool EmailConfirmed, bool IsAdmin, int Parties, bool LockedOut, AccessView Access,
    DateTimeOffset? Joined, DateTimeOffset? LastParty);
public sealed record ResetLinkView(string Link, int ValidForHours);

/// <summary>
/// The admin's list of host accounts. Its main job: on a server without email, a host
/// who forgets their password asks the admin, who creates a one-time reset link here
/// and sends it to them (text message, chat…).
/// </summary>
public static class AdminHostEndpoints
{
    public static void MapAdminHostEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/hosts").RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(RequireAdmin);

        admin.MapGet("/", async (AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var parties = await db.Parties.AsNoTracking().GroupBy(p => p.HostUserId)
                .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(p => p.CreatedAt) }).ToDictionaryAsync(x => x.Key, ct);
            var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
            var grants = (await db.AccessGrants.AsNoTracking().ToListAsync(ct)).ToLookup(g => g.UserId);
            return users.Select(u => new HostView(u.Id, u.DisplayName, u.Email ?? "", u.EmailConfirmed, u.IsAdmin,
                parties.GetValueOrDefault(u.Id)?.Count ?? 0, u.LockoutEnd > now, Access.From(u.IsAdmin, grants[u.Id].ToList(), now),
                u.CreatedAt, parties.GetValueOrDefault(u.Id)?.Last));
        });

        // ---- Free access (#100): both games for good, for family, friends and testers. Taking it away
        // revokes the free grants only; a trial or pass still in effect keeps counting.
        admin.MapPost("/{id}/free-access", async (string id, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            var now = clock.GetUtcNow();
            if (!await db.AccessGrants.AnyAsync(g => g.UserId == id && g.Kind == GrantKind.Comp && g.RevokedAt == null && (g.EndsAt == null || g.EndsAt > now), ct))
            {
                db.AccessGrants.Add(Access.FreeAccess(id, now, "Given by the admin"));
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(await Access.ForAsync(db, user, now, ct));
        });

        admin.MapDelete("/{id}/free-access", async (string id, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            var now = clock.GetUtcNow();
            await db.AccessGrants.Where(g => g.UserId == id && g.Kind == GrantKind.Comp && g.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, now), ct);
            return Results.Ok(await Access.ForAsync(db, user, now, ct));
        });

        admin.MapPost("/{id}/reset-link", async (string id, UserManager<AppUser> users, IOptions<AppOptions> options, HttpRequest request) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user?.Email is null) return Results.NotFound();
            var token = await users.GeneratePasswordResetTokenAsync(user);
            // Using the request's own address is safe here: the admin sees the link and passes it on themselves.
            var baseUrl = options.Value.PublicUrl ?? $"{request.Scheme}://{request.Host}";
            return Results.Ok(new ResetLinkView(AccountLinks.Reset(baseUrl, user.Email, token), (int)AccountTokens.Lifespan.TotalHours));
        });
    }

    /// <summary>Endpoint filter: only the admin. Also guards the invites (<see cref="InviteEndpoints"/>).</summary>
    internal static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.GetUserAsync(ctx.HttpContext.User);
        if (user is not { IsAdmin: true }) return Results.Problem("Only the admin can manage host accounts.", statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }
}
