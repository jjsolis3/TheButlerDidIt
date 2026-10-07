using ButlerDidIt.Api.Billing;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Plans;

public sealed class PlansOptions
{
    /// <summary>How long a new host's free trial of both games lasts.</summary>
    public int TrialDays { get; set; } = 14;
}

/// <summary>What a host's access is called on their pages. Code never decides anything from it; it checks the games.</summary>
public enum AccessPlan
{
    /// <summary>The site's admin: every game, always.</summary>
    Admin,

    /// <summary>Free access, given by the admin or an invite.</summary>
    Free,

    Subscription,
    Pass,
    Trial,

    /// <summary>The free trial is over and nothing else is in effect.</summary>
    TrialEnded,

    None,
}

/// <param name="EndsAt">When the plan shown ends; null when it doesn't. A subscription that renews counts a little past its renewal date.</param>
/// <param name="Status">For a paid plan (#101): renewing, cancelled, a payment failed…; null otherwise.</param>
/// <param name="RenewsAt">For a subscription that renews: when.</param>
/// <param name="Payments">True when the site sells plans, so pages can say "choose a plan" rather than "ask the admin".</param>
public sealed record AccessView(bool Mysteries, bool EscapeRooms, AccessPlan Plan, DateTimeOffset? EndsAt,
    PaidStatus? Status = null, DateTimeOffset? RenewsAt = null, bool Payments = false)
{
    public bool Allows(GameKind kind) => kind == GameKind.EscapeRoom ? EscapeRooms : Mysteries;
}

/// <summary>
/// Which games a host may start (#100). Their access is every grant in effect put together, so a
/// trial, a party pass and a subscription simply add up. Only <em>starting</em> something is checked
/// (RequireGame): guests, joining, a party already created, recaps and the host's own content never are,
/// so a trial that ends mid-evening never stops a party.
/// </summary>
public static class Access
{
    /// <summary>A grant counts from its start until its end, unless it was revoked.</summary>
    public static bool InEffect(AccessGrantEntity g, DateTimeOffset now) =>
        g.RevokedAt is null && g.StartsAt <= now && (g.EndsAt is null || g.EndsAt > now);

    /// <summary>Puts a host's grants together. Pure, so it's tested without a database.</summary>
    public static AccessView From(bool isAdmin, IReadOnlyCollection<AccessGrantEntity> grants, DateTimeOffset now)
    {
        if (isAdmin) return new(true, true, AccessPlan.Admin, null);
        var current = grants.Where(g => InEffect(g, now)).ToList();
        var games = current.Aggregate(GameAccess.None, (all, g) => all | g.Games);
        // The plan shown is the most lasting grant in effect: free access, then a subscription, a pass, a trial.
        var shown = current.OrderBy(g => Rank(g.Kind)).ThenByDescending(g => g.EndsAt ?? DateTimeOffset.MaxValue).FirstOrDefault();
        var plan = shown?.Kind switch
        {
            GrantKind.Comp => AccessPlan.Free,
            GrantKind.Subscription => AccessPlan.Subscription,
            GrantKind.Pass => AccessPlan.Pass,
            GrantKind.Trial => AccessPlan.Trial,
            _ => grants.Any(g => g.Kind == GrantKind.Trial) ? AccessPlan.TrialEnded : AccessPlan.None,
        };
        return new(games.HasFlag(GameAccess.Mysteries), games.HasFlag(GameAccess.EscapeRooms), plan, shown?.EndsAt, shown?.Status, shown?.RenewsAt);
    }

    private static int Rank(GrantKind kind) => kind switch
    {
        GrantKind.Comp => 0,
        GrantKind.Subscription => 1,
        GrantKind.Pass => 2,
        _ => 3,
    };

    public static async Task<AccessView> ForAsync(AppDbContext db, AppUser user, DateTimeOffset now, CancellationToken ct)
    {
        if (user.IsAdmin) return From(true, [], now);
        var grants = await db.AccessGrants.AsNoTracking().Where(g => g.UserId == user.Id).ToListAsync(ct);
        return From(false, grants, now);
    }

    /// <summary>
    /// Endpoint filter for starting a game: a party, or a mystery or room for the AI to write.
    /// The page also shows what's locked, but this is the check that counts.
    /// </summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireGame(GameKind kind) => async (ctx, next) =>
    {
        var sp = ctx.HttpContext.RequestServices;
        var user = await sp.GetRequiredService<UserManager<AppUser>>().GetUserAsync(ctx.HttpContext.User);
        if (user is null) return Results.Unauthorized();
        var db = sp.GetRequiredService<AppDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var ct = ctx.HttpContext.RequestAborted;
        var access = await ForAsync(db, user, clock.GetUtcNow(), ct);
        if (access.Allows(kind)) return await next(ctx);

        var payments = sp.GetRequiredService<BillingSetup>().Enabled;
        if (payments && user.BillingCustomerId is not null)
        {
            // Before saying no to a paying host: a renewal or a retried payment we haven't heard about may have gone through.
            await sp.GetRequiredService<BillingService>().RefreshIfDueAsync(user, ct);
            access = await ForAsync(db, user, clock.GetUtcNow(), ct);
            if (access.Allows(kind)) return await next(ctx);
        }
        var game = kind == GameKind.EscapeRoom ? "escape rooms" : "murder mysteries";
        // With payments on (#101) the host can sort it out themselves; otherwise only the admin can.
        var message = (access.Plan, payments) switch
        {
            (AccessPlan.TrialEnded, true) => $"Your free trial has ended. Choose a plan on your account page to start {game}.",
            (AccessPlan.TrialEnded, false) => $"Your free trial has ended. Ask the site's admin for access to {game}.",
            (_, true) => $"Your plan doesn't include {game}. Choose a plan on your account page.",
            _ => $"Your plan doesn't include {game}. Ask the site's admin for access.",
        };
        return Results.Problem(message, statusCode: StatusCodes.Status403Forbidden);
    };

    public static AccessGrantEntity Trial(string userId, DateTimeOffset now, int days) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Games = GameAccess.Both, Kind = GrantKind.Trial,
        StartsAt = now, EndsAt = now.AddDays(days), Note = "Free trial", CreatedAt = now,
    };

    public static AccessGrantEntity FreeAccess(string userId, DateTimeOffset now, string note) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Games = GameAccess.Both, Kind = GrantKind.Comp,
        StartsAt = now, EndsAt = null, Note = note, CreatedAt = now,
    };
}
