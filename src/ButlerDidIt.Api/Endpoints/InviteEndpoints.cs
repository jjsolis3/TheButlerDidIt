using System.Net.Mail;
using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Endpoints;

public enum InviteStatus
{
    Pending,
    Used,
    Expired,
}

/// <summary>An invite on the admin's list. It never includes the link: only a hash of it is stored.</summary>
public sealed record InviteView(Guid Id, string? Email, string? Note, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    InviteStatus Status, string? UsedBy, DateTimeOffset? UsedAt);

/// <param name="Email">Optional: only this address can use the invite.</param>
/// <param name="Days">How long the link works, 1 to <see cref="InviteEndpoints.MaxDays"/> days.</param>
/// <param name="Send">Email the link to <paramref name="Email"/>. Needs email set up on the server.</param>
public sealed record CreateInviteRequest(string? Email, string? Note, int Days = 7, bool Send = false);

/// <summary>A new invite and its link. This is the only time the server ever hands the link out.</summary>
public sealed record CreatedInvite(InviteView Invite, string Link, bool Emailed);

public sealed record InviteCheckRequest(string Token);

/// <summary>What the sign-up page shows someone holding a usable invite.</summary>
public sealed record InviteInfo(string? Email, string InvitedBy, DateTimeOffset ExpiresAt);

/// <summary>
/// Invites let new hosts sign up while registration is closed (Auth:AllowRegistration=false).
/// The admin makes a one-time link and sends it (by text, chat, or email from here). The link
/// carries a random token; the database keeps only its hash, the way seat tokens are kept.
/// </summary>
public static class InviteEndpoints
{
    public const int MaxDays = 30;

    internal const string UsedMessage = "This invite has already been used. If it was you, sign in instead.";

    public static void MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/invites").RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(AdminHostEndpoints.RequireAdmin);

        admin.MapGet("/", async (AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var invites = await db.Invites.AsNoTracking().OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
            var userIds = invites.Where(i => i.UsedByUserId is not null).Select(i => i.UsedByUserId!).ToList();
            var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            var now = clock.GetUtcNow();
            return invites.Select(i => ToView(i, now, names));
        });

        admin.MapPost("/", async (CreateInviteRequest req, ClaimsPrincipal principal, AppDbContext db, UserManager<AppUser> users,
            IEmailSender email, IOptions<AppOptions> options, HttpRequest request, TimeProvider clock, ILogger<InviteEntity> log, CancellationToken ct) =>
        {
            if (req.Days is < 1 or > MaxDays)
                return Results.Problem($"An invite can last 1 to {MaxDays} days.", statusCode: StatusCodes.Status400BadRequest);
            var address = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
            // A bare address only: MailAddress also accepts "Ana <ana@example.com>", which no account could match.
            if (address is not null && (address.Length > 256 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address))
                return Results.Problem("That doesn't look like an email address.", statusCode: StatusCodes.Status400BadRequest);
            var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            if (note is { Length: > 80 })
                return Results.Problem("Keep the note to 80 characters.", statusCode: StatusCodes.Status400BadRequest);
            if (req.Send && address is null)
                return Results.Problem("Add their email address to send the invite.", statusCode: StatusCodes.Status400BadRequest);
            if (req.Send && !email.IsConfigured)
                return Results.Problem("This server can't send email. Copy the link and send it yourself.", statusCode: StatusCodes.Status400BadRequest);
            if (address is not null && await users.FindByEmailAsync(address) is not null)
                return Results.Problem($"{address} already has a host account.", statusCode: StatusCodes.Status409Conflict);

            var admin = await users.GetUserAsync(principal);
            var now = clock.GetUtcNow();
            var token = SeatTokens.NewToken();
            var invite = new InviteEntity
            {
                Id = Guid.NewGuid(), TokenHash = SeatTokens.Hash(token), Email = address, Note = note,
                CreatedByUserId = admin!.Id, CreatedAt = now, ExpiresAt = now.AddDays(req.Days),
            };
            db.Invites.Add(invite);
            await db.SaveChangesAsync(ct);

            // Using the request's own address is safe when there's no public URL: only the admin sees
            // the link, and passes it on themselves. Sending email needs the public URL anyway.
            var link = AccountLinks.Invite(options.Value.PublicUrl ?? $"{request.Scheme}://{request.Host}", token);
            var emailed = false;
            if (req.Send)
            {
                try
                {
                    await email.SendAsync(address!, $"{admin.DisplayName} invited you to The Butler Did It", AccountLinks.InviteEmail(admin.DisplayName, link, req.Days), ct);
                    emailed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The invite still works: the page shows the link to send another way.
                    log.LogError(ex, "Could not send an invite email");
                }
            }
            return Results.Ok(new CreatedInvite(ToView(invite, now, new Dictionary<string, string>()), link, emailed));
        });

        // Revoke an unused invite, or tidy a used or expired one off the list. Accounts made with it stay.
        admin.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Invites.Where(i => i.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent());

        // The sign-up page checks an invite before showing the form, so an expired link says so straight
        // away. POST keeps the token out of server logs. No rate limit needed: a token is 256 random bits.
        app.MapPost("/api/auth/invite", async (InviteCheckRequest req, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var (invite, error) = await FindUsableAsync(db, req.Token, clock.GetUtcNow(), ct);
            if (invite is null) return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            var invitedBy = await db.Users.Where(u => u.Id == invite.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            return Results.Ok(new InviteInfo(invite.Email, invitedBy ?? "The admin", invite.ExpiresAt));
        });
    }

    /// <summary>The invite for a token if it can still be used; otherwise a message that says why not.</summary>
    internal static async Task<(InviteEntity? Invite, string? Error)> FindUsableAsync(AppDbContext db, string? token, DateTimeOffset now, CancellationToken ct)
    {
        var hash = string.IsNullOrWhiteSpace(token) ? null : SeatTokens.Hash(token.Trim());
        var invite = hash is null ? null : await db.Invites.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null) return (null, "This invite link isn't valid. Check you copied all of it, or ask for a new one.");
        if (invite.UsedAt is not null) return (null, UsedMessage);
        if (invite.ExpiresAt <= now) return (null, "This invite has expired. Ask the person who sent it for a new one.");
        return (invite, null);
    }

    /// <summary>
    /// Marks the invite used by <paramref name="userId"/>. Call it inside the transaction that creates
    /// the account: the UPDATE locks the row, so if two people use one link at the same moment the second
    /// waits, then finds it used and gets false. If creating the account fails, rolling back frees the invite.
    /// </summary>
    internal static async Task<bool> ClaimAsync(AppDbContext db, Guid inviteId, string userId, DateTimeOffset now, CancellationToken ct) =>
        await db.Invites.Where(i => i.Id == inviteId && i.UsedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, now).SetProperty(i => i.UsedByUserId, userId), ct) == 1;

    private static InviteView ToView(InviteEntity i, DateTimeOffset now, IReadOnlyDictionary<string, string> names) =>
        new(i.Id, i.Email, i.Note, i.CreatedAt, i.ExpiresAt,
            i.UsedAt is not null ? InviteStatus.Used : i.ExpiresAt <= now ? InviteStatus.Expired : InviteStatus.Pending,
            i.UsedByUserId is null ? null : names.GetValueOrDefault(i.UsedByUserId), i.UsedAt);
}
