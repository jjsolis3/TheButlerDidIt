using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record MeResponse(string Id, string Email, string DisplayName, bool IsAdmin, bool EmailConfirmed);
public sealed record AuthOptionsView(bool AllowRegistration, bool EmailEnabled, bool RequireConfirmedEmail);
public sealed record ForgotRequest(string Email);
public sealed record ResetRequest(string Email, string Token, string Password);
public sealed record ConfirmRequest(string UserId, string Token);

public sealed class AuthOptions
{
    /// <summary>Set Auth__AllowRegistration=false after creating your account to make the server invite-only.</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>
    /// When true (and email is set up), hosts must click the link in their welcome email
    /// before they can create parties or generate mysteries. Stops people signing up with
    /// someone else's address.
    /// </summary>
    public bool RequireConfirmedEmail { get; set; }
}

/// <summary>
/// Host accounts. We use ASP.NET Core Identity for password hashing and lockout,
/// but write our own small endpoints so the rules are easy to read: the first
/// account becomes the admin, and registration can be switched off.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Rate-limit policy for endpoints that send email, so the site can't be used to flood an inbox.</summary>
    public const string EmailRateLimit = "email";

    /// <summary>The same answer whether or not the account exists, so nobody can use the form to find out who has one.</summary>
    private const string ForgotReply = "If that email has a host account, a reset link is on its way. Check your inbox (and spam folder).";

    private const string BadResetLink = "This reset link is invalid or has expired. Ask for a new one.";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        // What the sign-in page should offer: a "Forgot password?" link only makes sense with email.
        group.MapGet("/options", (Microsoft.Extensions.Options.IOptions<AuthOptions> options, IEmailSender email) =>
            new AuthOptionsView(options.Value.AllowRegistration, email.IsConfigured, options.Value.RequireConfirmedEmail && email.IsConfigured));

        group.MapPost("/register", async (
            RegisterRequest req,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn,
            Microsoft.Extensions.Options.IOptions<AuthOptions> options,
            IEmailSender email,
            Microsoft.Extensions.Options.IOptions<AppOptions> app,
            ILogger<AuthOptions> log,
            CancellationToken ct) =>
        {
            var isFirstUser = !await users.Users.AnyAsync();
            if (!isFirstUser && !options.Value.AllowRegistration)
                return Results.Problem("Registration is closed on this server.", statusCode: StatusCodes.Status403Forbidden);

            var displayName = req.DisplayName.Trim();
            if (displayName.Length is < 1 or > 60)
                return Results.Problem("Display name must be 1 to 60 characters.", statusCode: StatusCodes.Status400BadRequest);

            var user = new AppUser { UserName = req.Email.Trim(), Email = req.Email.Trim(), DisplayName = displayName, IsAdmin = isFirstUser };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded)
                return Results.Problem(string.Join(" ", result.Errors.Select(e => e.Description)), statusCode: StatusCodes.Status400BadRequest);

            await SendConfirmationAsync(user, users, email, app, log, ct);
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok(ToMe(user));
        });

        group.MapPost("/login", async (LoginRequest req, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
        {
            var result = await signIn.PasswordSignInAsync(req.Email.Trim(), req.Password, isPersistent: true, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                var message = result.IsLockedOut ? "Too many attempts. Try again in a few minutes." : "Wrong email or password.";
                return Results.Problem(message, statusCode: StatusCodes.Status401Unauthorized);
            }
            var user = await users.FindByNameAsync(req.Email.Trim());
            return Results.Ok(ToMe(user!));
        });

        group.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        });

        // ---- Forgotten password: email a one-time link.
        group.MapPost("/forgot", async (ForgotRequest req, UserManager<AppUser> users, IEmailSender email,
            Microsoft.Extensions.Options.IOptions<AppOptions> app, ILogger<AuthOptions> log, CancellationToken ct) =>
        {
            if (!email.IsConfigured)
                return Results.Problem("This server can't send email yet. Ask the admin for a reset link.", statusCode: StatusCodes.Status400BadRequest);

            var user = await users.FindByEmailAsync(req.Email.Trim());
            if (user?.Email is not null)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var link = AccountLinks.Reset(app.Value.PublicUrl!, user.Email, token);
                try
                {
                    await email.SendAsync(user.Email, "Reset your password", AccountLinks.ResetEmail(user.DisplayName, link), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Still give the same answer: an error here would reveal that the account exists.
                    log.LogError(ex, "Could not send a password reset email");
                }
            }
            return Results.Ok(new { Message = ForgotReply });
        }).RequireRateLimiting(EmailRateLimit);

        group.MapPost("/reset", async (ResetRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.FindByEmailAsync(req.Email.Trim());
            if (user is null) return Results.Problem(BadResetLink, statusCode: StatusCodes.Status400BadRequest);

            var result = await users.ResetPasswordAsync(user, req.Token, req.Password);
            if (!result.Succeeded)
            {
                // A bad token and a weak password both fail here; tell them apart for a helpful message.
                var badToken = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken));
                return Results.Problem(badToken ? BadResetLink : string.Join(" ", result.Errors.Select(e => e.Description)),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Getting the link proves they own the inbox. Also clear any lockout from guessing.
            // (ResetPasswordAsync already changed the security stamp, which signs out other devices.)
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            await users.ResetAccessFailedCountAsync(user);
            await users.SetLockoutEndDateAsync(user, null);
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Ok(ToMe(user));
        });

        // ---- Email confirmation
        group.MapPost("/confirm", async (ConfirmRequest req, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(req.UserId);
            var ok = user is not null && (await users.ConfirmEmailAsync(user, req.Token)).Succeeded;
            return ok ? Results.NoContent() : Results.Problem("This confirmation link is invalid or has expired. Sign in and ask for a new one.", statusCode: 400);
        });

        group.MapPost("/resend-confirmation", async (ClaimsPrincipal principal, UserManager<AppUser> users, IEmailSender email,
            Microsoft.Extensions.Options.IOptions<AppOptions> app, ILogger<AuthOptions> log, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            if (!email.IsConfigured) return Results.Problem("This server can't send email.", statusCode: 400);
            await SendConfirmationAsync(user, users, email, app, log, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.Host).RequireRateLimiting(EmailRateLimit);

        group.MapGet("/me", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(ToMe(user));
        }).RequireAuthorization(AuthPolicies.Host);
    }

    private static MeResponse ToMe(AppUser u) => new(u.Id, u.Email ?? "", u.DisplayName, u.IsAdmin, u.EmailConfirmed);

    private static async Task SendConfirmationAsync(AppUser user, UserManager<AppUser> users, IEmailSender email,
        Microsoft.Extensions.Options.IOptions<AppOptions> app, ILogger log, CancellationToken ct)
    {
        if (!email.IsConfigured || user.EmailConfirmed || user.Email is null) return;
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        try
        {
            await email.SendAsync(user.Email, "Confirm your email", AccountLinks.ConfirmEmail(user.DisplayName, AccountLinks.Confirm(app.Value.PublicUrl!, user.Id, token)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sign-up still succeeds; they can ask for another email from the host page.
            log.LogError(ex, "Could not send a confirmation email");
        }
    }

    /// <summary>
    /// Endpoint filter for "create" actions: when the server requires confirmed emails,
    /// an unconfirmed host is asked to confirm first.
    /// </summary>
    public static async ValueTask<object?> RequireConfirmedHost(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var sp = ctx.HttpContext.RequestServices;
        var required = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value.RequireConfirmedEmail
            && sp.GetRequiredService<IEmailSender>().IsConfigured;
        if (required)
        {
            var user = await sp.GetRequiredService<UserManager<AppUser>>().GetUserAsync(ctx.HttpContext.User);
            if (user is { EmailConfirmed: false })
                return Results.Problem("Please confirm your email address first. We sent you a link when you signed up.", statusCode: StatusCodes.Status403Forbidden);
        }
        return await next(ctx);
    }
}
