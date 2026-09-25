using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record MeResponse(string Id, string Email, string DisplayName, bool IsAdmin);

public sealed class AuthOptions
{
    /// <summary>Set Auth__AllowRegistration=false after creating your account to make the server invite-only.</summary>
    public bool AllowRegistration { get; set; } = true;
}

/// <summary>
/// Host accounts. We use ASP.NET Core Identity for password hashing and lockout,
/// but write our own small endpoints so the rules are easy to read: the first
/// account becomes the admin, and registration can be switched off.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", async (
            RegisterRequest req,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn,
            Microsoft.Extensions.Options.IOptions<AuthOptions> options) =>
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

        group.MapGet("/me", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(ToMe(user));
        }).RequireAuthorization(AuthPolicies.Host);
    }

    private static MeResponse ToMe(AppUser u) => new(u.Id, u.Email ?? "", u.DisplayName, u.IsAdmin);
}
