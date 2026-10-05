using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Api.Plans;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

/// <summary>The account page: who you are, what you've made, and this month's use.</summary>
public sealed record AccountView(string DisplayName, string Email, bool EmailConfirmed, bool IsAdmin,
    /// <summary>True when the server can send email, so an email change is confirmed by a link.</summary>
    bool EmailEnabled, AccountUsage Usage, AccountLibrary Library,
    /// <summary>Which games the host may start, and the plan that gives them.</summary>
    AccessView Access);

public sealed record AccountUsage(int MysteriesThisMonth, int EscapeRoomsThisMonth, int PartiesAllTime, decimal AiSpentThisMonthUsd, decimal AiBudgetUsd);

/// <param name="Parties">On the host's list (not removed from it).</param>
/// <param name="Mysteries">Their own copies and AI-written mysteries.</param>
/// <param name="EscapeRooms">Rooms the AI wrote for them.</param>
/// <param name="Escapes">Games of theirs that escaped.</param>
public sealed record AccountLibrary(int Parties, int Mysteries, int EscapeRooms, int Escapes);

public sealed record ProfileRequest(string DisplayName);
public sealed record ChangeEmailRequest(string NewEmail, string Password);
public sealed record ConfirmEmailChangeRequest(string UserId, string Email, string Token);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record DeleteAccountRequest(string Password);

/// <param name="Pending">True when a link was emailed to the new address and the change waits for it.</param>
public sealed record EmailChangeResult(bool Pending, string Message, MeResponse Me);

/// <summary>
/// A host's own account (/account). Every endpoint acts on the signed-in host, so there's no
/// user id in a request that could be changed to reach someone else's account. Changes that
/// could lock the owner out (email, password, deleting) need the current password.
/// </summary>
public static class AccountEndpoints
{
    private const string BadEmailLink = "This link is invalid or has expired. Ask for a new one on your account page.";

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").RequireAuthorization(AuthPolicies.Host);

        group.MapGet("/", async (ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, IEmailSender email,
            IOptions<AiOptions> ai, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var now = clock.GetUtcNow();
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

            var parties = db.Parties.AsNoTracking().Where(p => p.HostUserId == user.Id);
            var thisMonth = await parties.Where(p => p.CreatedAt >= monthStart).GroupBy(p => p.Kind)
                .Select(g => new { Kind = g.Key, Count = g.Count() }).ToListAsync(ct);
            var usage = new AccountUsage(
                thisMonth.Where(x => x.Kind == GameKind.Mystery).Sum(x => x.Count),
                thisMonth.Where(x => x.Kind == GameKind.EscapeRoom).Sum(x => x.Count),
                await parties.CountAsync(ct),
                await DbAiBudget.SpentThisMonthAsync(db, user.Id, clock, ct),
                ai.Value.MonthlyBudgetUsd);
            var library = new AccountLibrary(
                await parties.CountAsync(p => p.HiddenAt == null, ct),
                await db.Scenarios.CountAsync(s => s.OwnerUserId == user.Id && s.ArchivedAt == null, ct),
                await db.EscapeRooms.CountAsync(r => r.OwnerUserId == user.Id, ct),
                await db.EscapeResults.CountAsync(r => r.HostUserId == user.Id && r.Escaped, ct));
            return Results.Ok(new AccountView(user.DisplayName, user.Email ?? "", user.EmailConfirmed, user.IsAdmin, email.IsConfigured, usage, library,
                await Access.ForAsync(db, user, now, ct)));
        });

        group.MapPut("/profile", async (ProfileRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, HttpContext http) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var name = req.DisplayName?.Trim() ?? "";
            if (name.Length is < 1 or > 60) return Results.Problem("Your name must be 1 to 60 characters.", statusCode: StatusCodes.Status400BadRequest);
            user.DisplayName = name;
            await users.UpdateAsync(user);
            return Results.Ok(await AuthEndpoints.ToMeAsync(user, http));
        });

        // ---- Email. With email set up, the new address must confirm it (a link sent there), and the
        // old address is told. Without email there's nothing to confirm with, so it changes at once:
        // the current password is the check.
        group.MapPost("/email", async (ChangeEmailRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn,
            AppDbContext db, IEmailSender email, IOptions<AppOptions> options, ILogger<AccountView> log, HttpContext http, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var newEmail = req.NewEmail?.Trim() ?? "";
            if (newEmail.Length > 256 || !MailAddress.TryCreate(newEmail, out var parsed) || parsed.Address != newEmail)
                return Results.Problem("That doesn't look like an email address.", statusCode: StatusCodes.Status400BadRequest);
            if (users.NormalizeEmail(newEmail) == user.NormalizedEmail)
                return Results.Problem("That's already your email address.", statusCode: StatusCodes.Status400BadRequest);
            if (await CheckPasswordAsync(signIn, user, req.Password) is { } wrong) return wrong;
            if (await users.FindByEmailAsync(newEmail) is not null)
                return Results.Problem("Another host account already uses that email address.", statusCode: StatusCodes.Status409Conflict);

            var token = await users.GenerateChangeEmailTokenAsync(user, newEmail);
            if (email.IsConfigured)
            {
                var link = AccountLinks.ChangeEmail(options.Value.PublicUrl!, user.Id, newEmail, token);
                try
                {
                    await email.SendAsync(newEmail, "Confirm your new email address", AccountLinks.ChangeEmailEmail(user.DisplayName, link), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Could not send an email-change link");
                    return Results.Problem("The confirmation email couldn't be sent. Try again in a few minutes.", statusCode: StatusCodes.Status502BadGateway);
                }
                try
                {
                    if (user.Email is not null) await email.SendAsync(user.Email, "Your email address is changing", AccountLinks.EmailChangingNotice(user.DisplayName, newEmail), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Could not send an email-change notice to the old address");
                }
                return Results.Ok(new EmailChangeResult(true, $"We sent a link to {newEmail}. Your email address changes when you click it.", await AuthEndpoints.ToMeAsync(user, http)));
            }

            if (await ApplyEmailChangeAsync(db, users, user, newEmail, token, ct) is { } error)
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            await signIn.RefreshSignInAsync(user); // the change ended every session; keep this one
            return Results.Ok(new EmailChangeResult(false, $"Done. Sign in with {newEmail} from now on.", await AuthEndpoints.ToMeAsync(user, http)));
        }).RequireRateLimiting(AuthEndpoints.EmailRateLimit);

        // The link from the email. It may be opened on another device, so no sign-in is needed: the token proves it.
        app.MapPost("/api/account/email/confirm", async (ConfirmEmailChangeRequest req, ClaimsPrincipal principal, UserManager<AppUser> users,
            SignInManager<AppUser> signIn, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(req.UserId ?? "");
            if (user is null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Token))
                return Results.Problem(BadEmailLink, statusCode: StatusCodes.Status400BadRequest);
            if (await ApplyEmailChangeAsync(db, users, user, req.Email.Trim(), req.Token, ct) is { } error)
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            // The change ended every session. If this browser was signed in as them, keep it signed in.
            if (principal.FindFirstValue(ClaimTypes.NameIdentifier) == user.Id) await signIn.RefreshSignInAsync(user);
            return Results.Ok(await AuthEndpoints.ToMeAsync(user, http));
        });

        group.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn,
            IEmailSender email, ILogger<AccountView> log, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            // Checked first with lockout on, so a stolen session can't be used to guess the password.
            if (await CheckPasswordAsync(signIn, user, req.CurrentPassword) is { } wrong) return wrong;
            var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword ?? "");
            if (!result.Succeeded)
                return Results.Problem(string.Join(" ", result.Errors.Select(e => e.Description)), statusCode: StatusCodes.Status400BadRequest);
            // A new password ends every other session (it changes the security stamp); keep this one.
            await signIn.RefreshSignInAsync(user);
            if (email.IsConfigured && user.Email is not null)
            {
                try
                {
                    await email.SendAsync(user.Email, "Your password was changed", AccountLinks.PasswordChangedNotice(user.DisplayName), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Could not send a password-changed notice");
                }
            }
            return Results.NoContent();
        });

        // A new security stamp: every other browser fails its next session check (Auth:SessionCheckSeconds).
        group.MapPost("/sign-out-everywhere", async (ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            await users.UpdateSecurityStampAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.NoContent();
        });

        // ---- The host's usual party settings (#102): the host page starts from them.
        group.MapGet("/preferences", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
            await users.GetUserAsync(principal) is { } user ? Results.Ok(HostPreferences.For(user)) : Results.Unauthorized());

        group.MapPut("/preferences", async (HostPreferences req, ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (req.Problem() is { } problem) return Results.Problem(problem, statusCode: 400);
            user.Preferences = GameJson.Serialize(req);
            var saved = await users.UpdateAsync(user);
            return saved.Succeeded ? Results.Ok(HostPreferences.For(user)) : Results.Problem("Your settings couldn't be saved. Try again.", statusCode: 500);
        });

        // ---- "Download my data": a JSON file of what this account holds. Guests' names and notes aren't
        // included: they belong to the guests, and finished parties drop them anyway (RetentionWorker).
        group.MapGet("/export", async (ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var parties = await db.Parties.AsNoTracking().Where(p => p.HostUserId == user.Id).OrderBy(p => p.CreatedAt)
                .Select(p => new { p.Code, p.Kind, p.ScenarioId, p.Mode, p.Status, p.CreatedAt, p.ScheduledFor }).ToListAsync(ct);
            var mysteries = (await db.Scenarios.AsNoTracking().Where(s => s.OwnerUserId == user.Id).OrderBy(s => s.UpdatedAt).ToListAsync(ct))
                .Select(s => new { s.Id, s.Title, s.Source, s.UpdatedAt, Document = JsonDocument.Parse(s.Document).RootElement });
            var rooms = (await db.EscapeRooms.AsNoTracking().Where(r => r.OwnerUserId == user.Id).OrderBy(r => r.CreatedAt).ToListAsync(ct))
                .Select(r => new { r.Id, r.Title, r.CreatedAt, Document = JsonDocument.Parse(r.Document).RootElement });
            var escapes = await db.EscapeResults.AsNoTracking().Where(r => r.HostUserId == user.Id).OrderBy(r => r.FinishedAt)
                .Select(r => new { r.RoomId, r.Escaped, r.Score, r.ElapsedSeconds, r.HintsUsed, r.PlayerCount, r.Team, r.FinishedAt }).ToListAsync(ct);
            var aiUsage = await db.AiUsage.AsNoTracking().Where(u => u.HostUserId == user.Id)
                .GroupBy(u => new { u.At.Year, u.At.Month }).Select(g => new { g.Key.Year, g.Key.Month, Calls = g.Count(), CostUsd = g.Sum(u => u.CostUsd) })
                .OrderBy(x => x.Year).ThenBy(x => x.Month).ToListAsync(ct);

            var export = new
            {
                ExportedAt = clock.GetUtcNow(),
                Account = new { user.DisplayName, user.Email, user.EmailConfirmed, user.IsAdmin },
                PartySettings = HostPreferences.For(user),
                Parties = parties, Mysteries = mysteries, EscapeRooms = rooms, Escapes = escapes, AiUsage = aiUsage,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions(GameJson.Options) { WriteIndented = true });
            return Results.File(json, "application/json", "butler-did-it-account.json");
        });

        // ---- Delete the account and what it made. Leaderboard times and AI costs are kept without the
        // name: the times are public anyway, and the costs are the admin's bill.
        group.MapPost("/delete", async (DeleteAccountRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn,
            AppDbContext db, ContentCatalog catalog, EscapeCatalog rooms, ButlerDidIt.Api.Media.MediaService media, HttpContext http, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (user.IsAdmin)
                return Results.Problem("The admin account runs this site, so it can't be deleted here.", statusCode: StatusCodes.Status409Conflict);
            if (await CheckPasswordAsync(signIn, user, req.Password) is { } wrong) return wrong;
            if (await db.GenerationJobs.AnyAsync(j => j.HostUserId == user.Id && (j.Status == GenerationStatus.Queued || j.Status == GenerationStatus.Running), ct))
                return Results.Problem("The AI is still writing something for you. Wait for it to finish, then try again.", statusCode: StatusCodes.Status409Conflict);

            // Parties first, each with its seats, notes and costume selfies (files too). Phones still in one are told it's over.
            var partyIds = await db.Parties.Where(p => p.HostUserId == user.Id).Select(p => p.Id).ToListAsync(ct);
            foreach (var id in partyIds) await RetentionWorker.DeletePartyAsync(http.RequestServices, id, ct);

            var scenarioIds = await db.Scenarios.Where(s => s.OwnerUserId == user.Id).Select(s => s.Id).ToListAsync(ct);
            var roomIds = await db.EscapeRooms.Where(r => r.OwnerUserId == user.Id).Select(r => r.Id).ToListAsync(ct);
            var roomJobIds = roomIds.Select(EscapeMedia.JobId).ToList();
            // Their rooms' and mysteries' pictures, videos and sounds, and anything else they uploaded: deleted below once nothing uses them.
            var uploads = await db.ScenarioMedia.Where(m => roomJobIds.Contains(m.ScenarioId) || scenarioIds.Contains(m.ScenarioId)).Select(m => m.AssetId)
                .Union(db.MediaAssets.Where(a => a.OwnerUserId == user.Id).Select(a => a.Id)).ToListAsync(ct);
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await db.ScenarioMedia.Where(m => scenarioIds.Contains(m.ScenarioId) || roomJobIds.Contains(m.ScenarioId)).ExecuteDeleteAsync(ct);
                await db.MediaJobs.Where(j => scenarioIds.Contains(j.ScenarioId) || roomJobIds.Contains(j.ScenarioId)).ExecuteDeleteAsync(ct);
                await db.Scenarios.Where(s => s.OwnerUserId == user.Id).ExecuteDeleteAsync(ct);
                await db.EscapeRooms.Where(r => r.OwnerUserId == user.Id).ExecuteDeleteAsync(ct);
                await db.GenerationJobs.Where(j => j.HostUserId == user.Id).ExecuteDeleteAsync(ct);
                await db.EscapeResults.Where(r => r.HostUserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.HostUserId, "").SetProperty(r => r.Team, ""), ct);
                await db.AiUsage.Where(u => u.HostUserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.HostUserId, ""), ct);
                await db.MediaJobs.Where(j => j.HostUserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.HostUserId, ""), ct);
                // A file the admin's copy of one of their rooms still uses stays for that copy, no longer theirs.
                await db.MediaAssets.Where(a => a.OwnerUserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.OwnerUserId, (string?)null), ct);
                var deleted = await users.DeleteAsync(user);
                if (!deleted.Succeeded)
                    return Results.Problem(string.Join(" ", deleted.Errors.Select(e => e.Description)), statusCode: StatusCodes.Status500InternalServerError);
                await transaction.CommitAsync(ct);
            }
            // Files after the rows: a crash in between leaves a file nothing points at, never a row pointing at nothing.
            await media.DeleteUnusedUploadsAsync(uploads, ct);
            foreach (var id in scenarioIds) catalog.Invalidate(id);
            foreach (var id in roomIds)
            {
                rooms.Forget(id);
                rooms.ForgetArt(id);
            }
            await signIn.SignOutAsync();
            return Results.NoContent();
        });
    }

    /// <summary>
    /// The current password, checked with lockout on: someone using a stolen session can't guess it
    /// on these forms any faster than on the sign-in page. Null when it's right.
    /// </summary>
    private static async Task<IResult?> CheckPasswordAsync(SignInManager<AppUser> signIn, AppUser user, string? password)
    {
        var result = await signIn.CheckPasswordSignInAsync(user, password ?? "", lockoutOnFailure: true);
        if (result.Succeeded) return null;
        return Results.Problem(result.IsLockedOut ? "Too many wrong passwords. Try again in a few minutes." : "That isn't your current password.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Changes the email and the sign-in name together (hosts sign in with their email), in one
    /// transaction so they can't end up different. Returns a message when it can't.
    /// </summary>
    private static async Task<string?> ApplyEmailChangeAsync(AppDbContext db, UserManager<AppUser> users, AppUser user, string newEmail, string token, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await users.ChangeEmailAsync(user, newEmail, token);
        if (!changed.Succeeded)
        {
            var taken = changed.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateEmail));
            return taken ? "Another host account already uses that email address." : BadEmailLink;
        }
        var renamed = await users.SetUserNameAsync(user, newEmail);
        if (!renamed.Succeeded) return "Another host account already uses that email address.";
        await transaction.CommitAsync(ct);
        return null;
    }
}
