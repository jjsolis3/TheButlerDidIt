using ButlerDidIt.Ai;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Plans;
using ButlerDidIt.Api.Scale;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

/// <summary>The admin hub's first page: how the site is doing, at a glance.</summary>
public sealed record AdminOverview(
    AdminHostCounts Hosts,
    IReadOnlyList<PlanCount> Plans,
    AdminPartyCounts Parties,
    IReadOnlyList<WeekCount> Weeks,
    AdminAiSpend Ai,
    AdminRatings Ratings,
    SignUpsView SignUps,
    AdminServerView Server);

/// <param name="Active">Hosts who created a party in the last 30 days.</param>
public sealed record AdminHostCounts(int Total, int NewThisWeek, int NewThisMonth, int Active);

/// <summary>How many hosts are on each plan now (the admin isn't counted).</summary>
public sealed record PlanCount(AccessPlan Plan, int Hosts);

/// <param name="LiveNow">Games under way right now: past the lobby, not over, with a move in the last two hours.</param>
/// <param name="Mysteries">Mystery parties that began in the last 7 days.</param>
/// <param name="EscapeRooms">Escape room parties that began in the last 7 days.</param>
/// <param name="Guests">Seats at those parties, pass-and-play guests included.</param>
public sealed record AdminPartyCounts(int LiveNow, int Mysteries, int EscapeRooms, int Guests);

/// <summary>Games played to the end in one week, Monday to Sunday (UTC).</summary>
public sealed record WeekCount(DateOnly Week, int Mysteries, int EscapeRooms);

/// <param name="BudgetPerHostUsd">Each host's monthly AI allowance; 0 for none.</param>
public sealed record AdminAiSpend(decimal SpentUsd, decimal BudgetPerHostUsd, int Calls, int Failed);

/// <summary>Guests' ratings in the last 30 days, over every game.</summary>
public sealed record AdminRatings(double? Average, int Count, int TooEasy, int JustRight, int TooHard);

/// <param name="Open">Whether anyone may sign up now, or only people with an invite.</param>
/// <param name="ServerSetting">What the server's configuration (Auth:AllowRegistration) says.</param>
/// <param name="Switch">The admin's choice, which wins; null when they haven't made one.</param>
/// <param name="OpenInvites">Invite links not yet used or expired.</param>
public sealed record SignUpsView(bool Open, bool ServerSetting, bool? Switch, int OpenInvites, bool EmailEnabled, bool RequireConfirmedEmail);

/// <param name="Open">True for anyone, false for invites only, null for "as the server's configuration says".</param>
public sealed record SignUpsRequest(bool? Open);

/// <summary>How the server is set up, read from its configuration, for the things worth checking.</summary>
public sealed record AdminServerView(bool EmailEnabled, string MediaStorage, long MediaBytes, int MediaFiles, bool SeveralServers, int AiProviders, IReadOnlyList<AiRole> AiRoles);

/// <summary>Where a mystery or room came from.</summary>
public enum ContentOrigin
{
    /// <summary>Hand-written, shipped with the game.</summary>
    BuiltIn,

    /// <summary>A host's own that the admin shared with everyone.</summary>
    Shared,

    /// <summary>A host's own: written by the AI for them, or their edited copy.</summary>
    Host,
}

/// <summary>
/// One mystery or room, with how its games went: the admin's list of what to improve. A mystery's versions count on
/// its row, as on My mysteries.
/// </summary>
/// <param name="Hidden">The admin took it off the shelf.</param>
/// <param name="RecentPlays">Games in the last 30 days.</param>
/// <param name="SolveRate">Mysteries: the share of accusations that named the killer. Escape rooms: the share of games escaped.</param>
public sealed record AdminGameRow(
    GameKind Kind,
    string Id,
    string Title,
    ContentRating Shelf,
    ContentOrigin Origin,
    string? Owner,
    bool Hidden,
    int Plays,
    int RecentPlays,
    DateTimeOffset? LastPlayed,
    double? Rating,
    int Ratings,
    double? SolveRate,
    int TooEasy,
    int JustRight,
    int TooHard);

/// <summary>
/// The admin hub (#102): an overview of the site, every game with how it plays, and the sign-up switch. Hosts,
/// invites and AI keep their own endpoints (<see cref="AdminHostEndpoints"/>, <see cref="InviteEndpoints"/>,
/// <see cref="AiAdminEndpoints"/>); the hub's pages put them side by side.
/// </summary>
public static class AdminHubEndpoints
{
    private const int WeeksShown = 8;

    public static void MapAdminHubEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(RequireAdmin);

        admin.MapGet("/overview", async (AppDbContext db, TimeProvider clock, IOptions<AuthOptions> auth, IOptions<AiOptions> ai,
            IOptions<MediaOptions> media, IOptions<ScaleOptions> scale, IEmailSender email, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var weekAgo = now.AddDays(-7);
            var monthAgo = now.AddDays(-30);

            // Hosts and their plans. A plan is every grant in effect put together (Access.From), which SQL can't do,
            // so the grants are read and combined here, as on the Hosts page. A few small rows per host.
            var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.IsAdmin, u.CreatedAt }).ToListAsync(ct);
            var grants = (await db.AccessGrants.AsNoTracking().ToListAsync(ct)).ToLookup(g => g.UserId);
            var plans = users.Where(u => !u.IsAdmin)
                .GroupBy(u => Access.From(false, grants[u.Id].ToList(), now).Plan)
                .Select(g => new PlanCount(g.Key, g.Count()))
                .OrderByDescending(p => p.Hosts).ThenBy(p => p.Plan).ToList();
            var active = await db.Parties.AsNoTracking().Where(p => p.CreatedAt > monthAgo).Select(p => p.HostUserId).Distinct().CountAsync(ct);
            var hosts = new AdminHostCounts(users.Count, users.Count(u => u.CreatedAt > weekAgo), users.Count(u => u.CreatedAt > monthAgo), active);

            // This week's parties: the ones that got going. A party still in its lobby may never be played.
            var began = db.Parties.AsNoTracking().Where(p => p.CreatedAt > weekAgo && p.Status != PartyStatus.Lobby);
            var byKind = await began.GroupBy(p => p.Kind).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var twoHoursAgo = now.AddHours(-2);
            var parties = new AdminPartyCounts(
                await db.Parties.AsNoTracking().CountAsync(p => p.Status == PartyStatus.InProgress && p.UpdatedAt > twoHoursAgo, ct),
                byKind.GetValueOrDefault(GameKind.Mystery), byKind.GetValueOrDefault(GameKind.EscapeRoom),
                await began.SelectMany(p => p.Seats).CountAsync(ct));

            // The weekly chart counts games played to the end from the play records (#130), not the parties: the
            // retention job deletes abandoned parties, and with them any sign they were played, but records stay.
            var firstWeek = Monday(now).AddDays(-7 * (WeeksShown - 1));
            var finished = await db.PlayRecords.AsNoTracking().Where(r => r.FinishedAt >= firstWeek).Select(r => new { r.Kind, r.FinishedAt }).ToListAsync(ct);
            var weeks = Enumerable.Range(0, WeeksShown).Select(i =>
            {
                var start = firstWeek.AddDays(7 * i);
                var inWeek = finished.Where(r => r.FinishedAt >= start && r.FinishedAt < start.AddDays(7)).ToList();
                return new WeekCount(DateOnly.FromDateTime(start.UtcDateTime), inWeek.Count(r => r.Kind == GameKind.Mystery), inWeek.Count(r => r.Kind == GameKind.EscapeRoom));
            }).ToList();

            // AI spend this calendar month, the same month the per-host budget counts.
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var usage = db.AiUsage.AsNoTracking().Where(u => u.At >= monthStart);
            var spend = new AdminAiSpend(await usage.SumAsync(u => u.CostUsd, ct), ai.Value.MonthlyBudgetUsd,
                await usage.CountAsync(ct), await usage.CountAsync(u => !u.Success, ct));

            var votes = await db.PlayFeedback.AsNoTracking().Where(f => f.CreatedAt > monthAgo).Select(f => new { f.Rating, f.Difficulty }).ToListAsync(ct);
            var ratings = new AdminRatings(votes.Count == 0 ? null : Math.Round(votes.Average(v => v.Rating), 1), votes.Count,
                votes.Count(v => v.Difficulty == FeedbackDifficulty.TooEasy), votes.Count(v => v.Difficulty == FeedbackDifficulty.JustRight),
                votes.Count(v => v.Difficulty == FeedbackDifficulty.TooHard));

            var server = new AdminServerView(
                email.IsConfigured,
                string.Equals(media.Value.Storage, "S3", StringComparison.OrdinalIgnoreCase) ? "S3" : "Local",
                await db.MediaAssets.AsNoTracking().SumAsync(m => m.SizeBytes, ct),
                await db.MediaAssets.AsNoTracking().CountAsync(ct),
                scale.Value.MultiInstance,
                await db.AiProviders.AsNoTracking().CountAsync(ct),
                await db.AiRoles.AsNoTracking().OrderBy(r => r.Role).Select(r => r.Role).ToListAsync(ct));

            return new AdminOverview(hosts, plans, parties, weeks, spend, ratings, await SignUpsAsync(db, auth.Value, email, now, ct), server);
        });

        admin.MapGet("/games", async (AppDbContext db, EscapeCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            var monthAgo = clock.GetUtcNow().AddDays(-30);
            var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            var hidden = (await db.HiddenContent.AsNoTracking().Select(h => new { h.Kind, h.ContentId }).ToListAsync(ct))
                .Select(h => (h.Kind, h.ContentId)).ToHashSet();

            // Every game's numbers, added up in the database: one row per mystery version or room.
            var plays = (await db.PlayRecords.AsNoTracking().GroupBy(r => new { r.Kind, r.ContentId })
                .Select(g => new
                {
                    g.Key.Kind, g.Key.ContentId,
                    Plays = g.Count(), Recent = g.Count(r => r.FinishedAt > monthAgo), Last = g.Max(r => r.FinishedAt),
                    Accusers = g.Sum(r => r.Accusers ?? 0), Correct = g.Sum(r => r.Correct ?? 0), Escaped = g.Count(r => r.Escaped == true),
                }).ToListAsync(ct)).ToDictionary(x => (x.Kind, x.ContentId));
            var votes = (await db.PlayFeedback.AsNoTracking().GroupBy(f => new { f.Kind, f.ContentId })
                .Select(g => new
                {
                    g.Key.Kind, g.Key.ContentId, Count = g.Count(), Stars = g.Sum(f => f.Rating),
                    TooEasy = g.Count(f => f.Difficulty == FeedbackDifficulty.TooEasy),
                    JustRight = g.Count(f => f.Difficulty == FeedbackDifficulty.JustRight),
                    TooHard = g.Count(f => f.Difficulty == FeedbackDifficulty.TooHard),
                }).ToListAsync(ct)).ToDictionary(x => (x.Kind, x.ContentId));

            AdminGameRow Row(GameKind kind, string id, IReadOnlyCollection<string> ids, string title, ContentRating shelf, ContentOrigin origin, string? ownerId)
            {
                var p = ids.Where(i => plays.ContainsKey((kind, i))).Select(i => plays[(kind, i)]).ToList();
                var v = ids.Where(i => votes.ContainsKey((kind, i))).Select(i => votes[(kind, i)]).ToList();
                var count = p.Sum(x => x.Plays);
                var rated = v.Sum(x => x.Count);
                var accusers = p.Sum(x => x.Accusers);
                double? solve = kind == GameKind.Mystery
                    ? accusers == 0 ? null : Math.Round((double)p.Sum(x => x.Correct) / accusers, 2)
                    : count == 0 ? null : Math.Round((double)p.Sum(x => x.Escaped) / count, 2);
                return new AdminGameRow(kind, id, title, shelf, origin, ownerId is null ? null : names.GetValueOrDefault(ownerId, "an account since deleted"),
                    hidden.Contains((kind, id)), count, p.Sum(x => x.Recent), count == 0 ? null : p.Max(x => x.Last),
                    rated == 0 ? null : Math.Round((double)v.Sum(x => x.Stars) / rated, 1), rated, solve,
                    v.Sum(x => x.TooEasy), v.Sum(x => x.JustRight), v.Sum(x => x.TooHard));
            }

            // Mysteries: each one with its versions (hand-written letters and the AI's rewrites for one party). A
            // deleted mystery that parties played is archived, and leaves the list like it leaves every shelf.
            var scenarios = await db.Scenarios.AsNoTracking()
                .Select(s => new { s.Id, s.Title, s.VariantOf, s.ContentRating, s.Source, s.OwnerUserId, s.Shared, s.ArchivedAt }).ToListAsync(ct);
            var versions = scenarios.ToLookup(s => s.VariantOf ?? s.Id, s => s.Id);
            var rows = scenarios.Where(s => s.VariantOf is null && s.ArchivedAt is null).Select(s => Row(GameKind.Mystery, s.Id, versions[s.Id].ToList(), s.Title, s.ContentRating,
                s.Source == ScenarioSource.Handwritten ? ContentOrigin.BuiltIn : s.Shared ? ContentOrigin.Shared : ContentOrigin.Host,
                s.Source == ScenarioSource.Handwritten ? null : s.OwnerUserId)).ToList();

            // Escape rooms: the built-in ones from content/escape, and the hosts' own from the database.
            rows.AddRange(catalog.Rooms.Select(r => Row(GameKind.EscapeRoom, r.Id, [r.Id], r.Title, r.ContentRating, ContentOrigin.BuiltIn, null)));
            var owned = await db.EscapeRooms.AsNoTracking().Select(r => new { r.Id, r.Title, r.ContentRating, r.OwnerUserId, r.Shared }).ToListAsync(ct);
            rows.AddRange(owned.Select(r => Row(GameKind.EscapeRoom, r.Id, [r.Id], r.Title, r.ContentRating, r.Shared ? ContentOrigin.Shared : ContentOrigin.Host, r.OwnerUserId)));

            return rows.OrderByDescending(r => r.Plays).ThenBy(r => r.Kind).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        });

        // ---- Sign-ups: open to anyone, or invites only. The switch is stored (SiteSettings), so it needs no redeploy.
        admin.MapGet("/signups", async (AppDbContext db, IOptions<AuthOptions> auth, IEmailSender email, TimeProvider clock, CancellationToken ct) =>
            await SignUpsAsync(db, auth.Value, email, clock.GetUtcNow(), ct));

        admin.MapPut("/signups", async (SignUpsRequest req, AppDbContext db, IOptions<AuthOptions> auth, IEmailSender email, TimeProvider clock, CancellationToken ct) =>
        {
            var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Id == SiteSettingsEntity.SingleId, ct);
            if (row is null) db.SiteSettings.Add(row = new SiteSettingsEntity());
            row.AllowRegistration = req.Open;
            row.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return await SignUpsAsync(db, auth.Value, email, clock.GetUtcNow(), ct);
        });
    }

    private static async Task<SignUpsView> SignUpsAsync(AppDbContext db, AuthOptions auth, IEmailSender email, DateTimeOffset now, CancellationToken ct)
    {
        var chosen = await SignUps.SwitchAsync(db, ct);
        var openInvites = await db.Invites.AsNoTracking().CountAsync(i => i.UsedAt == null && i.ExpiresAt > now, ct);
        return new SignUpsView(chosen ?? auth.AllowRegistration, auth.AllowRegistration, chosen, openInvites, email.IsConfigured,
            auth.RequireConfirmedEmail && email.IsConfigured);
    }

    /// <summary>The start of this week: Monday, midnight UTC.</summary>
    public static DateTimeOffset Monday(DateTimeOffset now)
    {
        var today = now.UtcDateTime.Date;
        return new DateTimeOffset(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), TimeSpan.Zero);
    }

    private static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.GetUserAsync(ctx.HttpContext.User);
        if (user is not { IsAdmin: true }) return Results.Problem("Only the admin can open the admin hub.", statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }
}
