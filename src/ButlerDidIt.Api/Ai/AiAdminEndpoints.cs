using System.Diagnostics;
using System.Security.Claims;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Ai;

public sealed record ProviderView(Guid Id, string Name, AiProviderKind Kind, string? BaseUrl, bool HasApiKey, bool FromConfig);
public sealed record ProviderRequest(string Name, AiProviderKind Kind, string? BaseUrl, string? ApiKey);
public sealed record RoleView(AiRole Role, Guid? ProviderId, string? ProviderName, string? Model, int? MaxOutputTokens, float? Temperature);
public sealed record RoleRequest(Guid ProviderId, string Model, int? MaxOutputTokens, float? Temperature);
public sealed record PriceView(string Model, decimal InputPerMillion, decimal OutputPerMillion, decimal PerRequest = 0m);
public sealed record TestRequest(string Model);
public sealed record TestResult(bool Ok, string Message, long Milliseconds);
public sealed record AiStatus(bool Storyteller, bool Actor, bool Inspector, bool Voice, bool Illustrator, decimal BudgetUsd, decimal SpentThisMonthUsd, bool IsAdmin);

public static class AiAdminEndpoints
{
    private static AiRole? ParseRole(string name) =>
        Enum.TryParse<AiRole>(name, ignoreCase: true, out var role) && Enum.IsDefined(role) ? role : null;

    private static IResult UnknownRole(string name) =>
        Results.Problem($"Unknown role '{name}'. Use one of: {string.Join(", ", Enum.GetNames<AiRole>())}.", statusCode: 400);

    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Any host: what AI can I use, and how much have I spent?
        app.MapGet("/api/ai/status", async (ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db,
            IOptions<AiOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.Unauthorized();
            var roles = await db.AiRoles.AsNoTracking().Select(r => r.Role).ToListAsync(ct);
            return Results.Ok(new AiStatus(
                roles.Contains(AiRole.Storyteller), roles.Contains(AiRole.Actor), roles.Contains(AiRole.Inspector),
                roles.Contains(AiRole.Voice), roles.Contains(AiRole.Illustrator),
                options.Value.MonthlyBudgetUsd, await DbAiBudget.SpentThisMonthAsync(db, user.Id, clock, ct), user.IsAdmin));
        }).RequireAuthorization(AuthPolicies.Host);

        // ---- Admin only
        var admin = app.MapGroup("/api/admin/ai").RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(RequireAdmin);

        admin.MapGet("/providers", async (AppDbContext db, CancellationToken ct) =>
            (await db.AiProviders.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct)).Select(ToView));

        admin.MapPost("/providers", async (ProviderRequest req, AppDbContext db, AiKeyProtector keys, IOptions<AiOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            if (Validate(req, options.Value) is { } problem) return problem;
            if (await db.AiProviders.AnyAsync(p => p.Name == req.Name.Trim(), ct)) return Results.Problem("A provider with that name already exists.", statusCode: 409);
            var now = clock.GetUtcNow();
            var row = new AiProviderEntity
            {
                Id = Guid.NewGuid(), Name = req.Name.Trim(), Kind = req.Kind, BaseUrl = Clean(req.BaseUrl),
                EncryptedApiKey = keys.Protect(req.ApiKey), CreatedAt = now, UpdatedAt = now,
            };
            db.AiProviders.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToView(row));
        });

        admin.MapPut("/providers/{id:guid}", async (Guid id, ProviderRequest req, AppDbContext db, AiKeyProtector keys, IOptions<AiOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            if (Validate(req, options.Value) is { } problem) return problem;
            var row = await db.AiProviders.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            row.Name = req.Name.Trim();
            row.Kind = req.Kind;
            row.BaseUrl = Clean(req.BaseUrl);
            // null = keep the stored key; "" = remove it; anything else = replace it.
            if (req.ApiKey is not null) row.EncryptedApiKey = keys.Protect(req.ApiKey);
            row.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToView(row));
        });

        admin.MapDelete("/providers/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.AiRoles.AnyAsync(r => r.ProviderId == id, ct))
                return Results.Problem("This provider is still assigned to a role. Change the role first.", statusCode: 409);
            await db.AiProviders.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // Sends a tiny request straight to the provider so the admin sees at once whether the key and model work.
        admin.MapPost("/providers/{id:guid}/test", async (Guid id, TestRequest req, AppDbContext db, AiKeyProtector keys, IChatClientFactory factory, CancellationToken ct) =>
        {
            var row = await db.AiProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (row is null) return Results.NotFound();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var settings = new AiProviderSettings(row.Id, row.Name, row.Kind, row.BaseUrl, keys.Unprotect(row.EncryptedApiKey));
                using var client = factory.Create(settings, req.Model);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var reply = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with the single word OK.")],
                    new ChatOptions { ModelId = req.Model, MaxOutputTokens = 64 }, timeout.Token);
                return Results.Ok(new TestResult(true, $"Connected. The model replied: \"{Trim(reply.Text, 80)}\"", stopwatch.ElapsedMilliseconds));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Ok(new TestResult(false, Trim(ex.Message, 300), stopwatch.ElapsedMilliseconds));
            }
        });

        admin.MapGet("/roles", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.AiRoles.AsNoTracking().Include(r => r.Provider).ToListAsync(ct);
            return Enum.GetValues<AiRole>().Select(role =>
            {
                var r = rows.FirstOrDefault(x => x.Role == role);
                return new RoleView(role, r?.ProviderId, r?.Provider?.Name, r?.Model, r?.MaxOutputTokens, r?.Temperature);
            });
        });

        // The role arrives as text and is parsed here, ignoring case. The browser sends enums in
        // camelCase ("storyteller"), and ASP.NET Core's automatic enum binding for URL values
        // is case-sensitive, so binding it directly rejected every save with a bare 400.
        admin.MapPut("/roles/{roleName}", async (string roleName, RoleRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (ParseRole(roleName) is not { } role) return UnknownRole(roleName);
            if (string.IsNullOrWhiteSpace(req.Model)) return Results.Problem("Enter a model name, e.g. claude-opus-5 or gpt-5.", statusCode: 400);
            var provider = await db.AiProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.ProviderId, ct);
            if (provider is null) return Results.Problem("Unknown provider.", statusCode: 400);
            // Voices and pictures use media APIs that only OpenAI (or compatible servers) offer so far.
            if (role is AiRole.Voice or AiRole.Illustrator && provider.Kind is not (AiProviderKind.OpenAI or AiProviderKind.Fake))
                return Results.Problem($"The {role} role needs an OpenAI provider (other voice and image providers can be added later).", statusCode: 400);
            var row = await db.AiRoles.FindAsync([role], ct);
            if (row is null)
            {
                row = new AiRoleEntity { Role = role, Model = req.Model.Trim() };
                db.AiRoles.Add(row);
            }
            row.ProviderId = req.ProviderId;
            row.Model = req.Model.Trim();
            row.MaxOutputTokens = req.MaxOutputTokens is > 0 ? req.MaxOutputTokens : null;
            row.Temperature = req.Temperature;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/roles/{roleName}", async (string roleName, AppDbContext db, CancellationToken ct) =>
        {
            if (ParseRole(roleName) is not { } role) return UnknownRole(roleName);
            await db.AiRoles.Where(r => r.Role == role).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        admin.MapGet("/prices", async (AppDbContext db, CancellationToken ct) =>
            await db.AiModelPrices.AsNoTracking().OrderBy(p => p.Model)
                .Select(p => new PriceView(p.Model, p.InputPerMillion, p.OutputPerMillion, p.PerRequest)).ToListAsync(ct));

        admin.MapPut("/prices", async (PriceView req, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Model) || req.InputPerMillion < 0 || req.OutputPerMillion < 0 || req.PerRequest < 0)
                return Results.Problem("Enter a model name and non-negative prices.", statusCode: 400);
            var row = await db.AiModelPrices.FindAsync([req.Model.Trim()], ct);
            if (row is null)
            {
                row = new AiModelPriceEntity { Model = req.Model.Trim() };
                db.AiModelPrices.Add(row);
            }
            row.InputPerMillion = req.InputPerMillion;
            row.OutputPerMillion = req.OutputPerMillion;
            row.PerRequest = req.PerRequest;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/prices/{model}", async (string model, AppDbContext db, CancellationToken ct) =>
        {
            await db.AiModelPrices.Where(p => p.Model == model).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        admin.MapGet("/usage", async (int? months, AppDbContext db, IOptions<AiOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var since = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-(Math.Clamp(months ?? 3, 1, 24) - 1));
            var rows = await db.AiUsage.AsNoTracking().Where(u => u.At >= since).ToListAsync(ct);
            var emails = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email ?? u.Id, ct);

            object Group<T>(IEnumerable<IGrouping<T, AiUsageEntity>> groups) => groups
                .Select(g => new { Key = g.Key?.ToString(), Calls = g.Count(), Failed = g.Count(x => !x.Success), CostUsd = g.Sum(x => x.CostUsd),
                    InputTokens = g.Sum(x => x.InputTokens), OutputTokens = g.Sum(x => x.OutputTokens) })
                .OrderByDescending(x => x.CostUsd).ToList();

            return Results.Ok(new
            {
                BudgetUsd = options.Value.MonthlyBudgetUsd,
                Since = since,
                TotalCostUsd = rows.Sum(r => r.CostUsd),
                ByMonth = Group(rows.GroupBy(r => r.At.ToString("yyyy-MM"))),
                ByRole = Group(rows.GroupBy(r => r.Role)),
                ByModel = Group(rows.GroupBy(r => $"{r.ProviderName} · {r.Model}")),
                ByHost = Group(rows.GroupBy(r => emails.GetValueOrDefault(r.HostUserId, r.HostUserId))),
                UnpricedModels = rows.Where(r => !r.PriceKnown).Select(r => r.Model).Distinct().Order().ToList(),
                Recent = rows.OrderByDescending(r => r.At).Take(50).Select(r => new
                {
                    r.At, Role = r.Role.ToString(), r.ProviderName, r.Model, r.Purpose, r.InputTokens, r.OutputTokens, r.CostUsd, r.DurationMs, r.Success, r.Error,
                }),
            });
        });
    }

    /// <summary>Endpoint filter: only the admin account (the first host) may manage AI settings.</summary>
    private static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await users.GetUserAsync(ctx.HttpContext.User);
        if (user is not { IsAdmin: true }) return Results.Problem("Only the admin can manage AI settings.", statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    private static IResult? Validate(ProviderRequest req, AiOptions options)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 80) return Results.Problem("Give the provider a name (up to 80 characters).", statusCode: 400);
        if (req.Kind == AiProviderKind.Fake && !options.AllowFakeProvider) return Results.Problem("The Fake provider is only for automated tests.", statusCode: 400);
        if (!string.IsNullOrWhiteSpace(req.BaseUrl) && !Uri.TryCreate(req.BaseUrl, UriKind.Absolute, out _))
            return Results.Problem("The base URL must be a full URL, e.g. http://ollama:11434.", statusCode: 400);
        return null;
    }

    private static ProviderView ToView(AiProviderEntity p) => new(p.Id, p.Name, p.Kind, p.BaseUrl, p.EncryptedApiKey is not null, p.FromConfig);
    private static string? Clean(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
