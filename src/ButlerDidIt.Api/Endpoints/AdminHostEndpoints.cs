using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

public sealed record HostView(string Id, string DisplayName, string Email, bool EmailConfirmed, bool IsAdmin, int Parties, bool LockedOut);
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
            var partyCounts = await db.Parties.AsNoTracking().GroupBy(p => p.HostUserId)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
            return users.Select(u => new HostView(u.Id, u.DisplayName, u.Email ?? "", u.EmailConfirmed, u.IsAdmin,
                partyCounts.GetValueOrDefault(u.Id), u.LockoutEnd > now));
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

    private static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.GetUserAsync(ctx.HttpContext.User);
        if (user is not { IsAdmin: true }) return Results.Problem("Only the admin can manage host accounts.", statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }
}
