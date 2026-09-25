using ButlerDidIt.Ai;
using ButlerDidIt.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Ai;

/// <summary>
/// AI settings from configuration (appsettings / environment variables).
///
/// Everything here can also be managed on the Admin → AI page. Environment
/// variables are the safer place for API keys on a server, e.g. in Coolify:
///   Ai__Providers__0__Name=Claude
///   Ai__Providers__0__Kind=Anthropic
///   Ai__Providers__0__ApiKey=sk-ant-…
///   Ai__Roles__Storyteller__Provider=Claude
///   Ai__Roles__Storyteller__Model=claude-opus-5
/// </summary>
public sealed class AiOptions
{
    /// <summary>Per-host monthly spending cap in US dollars. 0 means unlimited.</summary>
    public decimal MonthlyBudgetUsd { get; set; } = 25m;

    public int QuestionsPerAct { get; set; } = 3;
    public int HintsPerAct { get; set; } = 1;

    /// <summary>Allows the "Fake" provider (canned answers). For automated tests only.</summary>
    public bool AllowFakeProvider { get; set; }

    public List<ProviderOption> Providers { get; set; } = [];
    public Dictionary<AiRole, RoleOption> Roles { get; set; } = [];

    public sealed class ProviderOption
    {
        public string Name { get; set; } = "";
        public AiProviderKind Kind { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
    }

    public sealed class RoleOption
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public int? MaxOutputTokens { get; set; }
    }
}

/// <summary>Encrypts API keys before they are stored and decrypts them only when a call is made.</summary>
public sealed class AiKeyProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("ButlerDidIt.AiProviderKeys.v1");

    public string? Protect(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : _protector.Protect(apiKey.Trim());

    public string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try
        {
            return _protector.Unprotect(encrypted);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The Data Protection keys changed (e.g. the keys volume was lost), so
            // the stored key can't be read. The admin needs to enter it again.
            throw new AiUnavailableException("A stored AI API key can no longer be decrypted. An admin needs to re-enter it on the Admin → AI page.");
        }
    }
}

public sealed class DbAiSettingsSource(AppDbContext db, AiKeyProtector keys) : IAiSettingsSource
{
    public async Task<AiRoleSettings?> GetRoleAsync(AiRole role, CancellationToken ct)
    {
        var row = await db.AiRoles.AsNoTracking().Include(r => r.Provider).FirstOrDefaultAsync(r => r.Role == role, ct);
        if (row?.Provider is not { } p) return null;
        var provider = new AiProviderSettings(p.Id, p.Name, p.Kind, p.BaseUrl, keys.Unprotect(p.EncryptedApiKey));
        return new AiRoleSettings(role, provider, row.Model, row.MaxOutputTokens, row.Temperature);
    }
}

/// <summary>Writes usage rows in their own scope so logging never interferes with the caller's unsaved changes.</summary>
public sealed class DbAiUsageSink(IServiceScopeFactory scopes) : IAiUsageSink
{
    public async Task RecordAsync(AiUsageRecord r, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var price = await db.AiModelPrices.AsNoTracking().FirstOrDefaultAsync(p => p.Model == r.Model, ct);
        var free = r.ProviderKind is AiProviderKind.Ollama or AiProviderKind.Fake;
        var cost = price is null ? 0m : new AiModelPrice(price.InputPerMillion, price.OutputPerMillion).Cost(r.InputTokens, r.OutputTokens);

        db.AiUsage.Add(new AiUsageEntity
        {
            At = r.At, Role = r.Role, ProviderKind = r.ProviderKind, ProviderName = r.ProviderName, Model = r.Model,
            InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, CostUsd = cost, PriceKnown = free || price is not null,
            DurationMs = r.DurationMs, Success = r.Success, Error = r.Error,
            HostUserId = r.Context.HostUserId, PartyId = r.Context.PartyId, JobId = r.Context.JobId, Purpose = r.Context.Purpose,
        });
        await db.SaveChangesAsync(ct);
    }
}

public sealed class DbAiBudget(AppDbContext db, IOptions<AiOptions> options, TimeProvider clock) : IAiBudget
{
    public async Task EnsureWithinBudgetAsync(string hostUserId, CancellationToken ct)
    {
        var budget = options.Value.MonthlyBudgetUsd;
        if (budget <= 0) return;
        var spent = await SpentThisMonthAsync(db, hostUserId, clock, ct);
        if (spent >= budget)
            throw new AiBudgetExceededException(
                $"This host has used this month's AI budget (${budget:0.00}). AI features are paused until next month. The game still works without them.");
    }

    public static async Task<decimal> SpentThisMonthAsync(AppDbContext db, string hostUserId, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return await db.AiUsage.Where(u => u.HostUserId == hostUserId && u.At >= monthStart).SumAsync(u => (decimal?)u.CostUsd, ct) ?? 0m;
    }
}

/// <summary>Applies AI settings from configuration and seeds known prices at startup.</summary>
public static class AiConfigSeeder
{
    /// <summary>List prices for Claude models (US$ per million tokens). Admins add prices for other providers' models.</summary>
    private static readonly Dictionary<string, (decimal In, decimal Out)> KnownPrices = new()
    {
        ["claude-fable-5-1"] = (10m, 50m),
        ["claude-opus-5-5"] = (4m, 20m),
        ["claude-opus-5"] = (5m, 25m),
        ["claude-sonnet-5"] = (2m, 10m),
        ["claude-haiku-4-5"] = (1m, 5m),
    };

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<AiKeyProtector>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
        var now = DateTimeOffset.UtcNow;

        foreach (var (model, (input, output)) in KnownPrices)
        {
            if (await db.AiModelPrices.FindAsync([model], ct) is null)
                db.AiModelPrices.Add(new AiModelPriceEntity { Model = model, InputPerMillion = input, OutputPerMillion = output });
        }

        foreach (var p in options.Providers.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            if (p.Kind == AiProviderKind.Fake && !options.AllowFakeProvider) continue;
            var row = await db.AiProviders.FirstOrDefaultAsync(x => x.Name == p.Name, ct);
            if (row is null)
            {
                row = new AiProviderEntity { Id = Guid.NewGuid(), Name = p.Name, CreatedAt = now };
                db.AiProviders.Add(row);
            }
            row.Kind = p.Kind;
            row.BaseUrl = string.IsNullOrWhiteSpace(p.BaseUrl) ? null : p.BaseUrl;
            if (!string.IsNullOrWhiteSpace(p.ApiKey)) row.EncryptedApiKey = keys.Protect(p.ApiKey);
            row.FromConfig = true;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        foreach (var (role, r) in options.Roles)
        {
            var provider = await db.AiProviders.FirstOrDefaultAsync(x => x.Name == r.Provider, ct);
            if (provider is null || string.IsNullOrWhiteSpace(r.Model)) continue;
            var row = await db.AiRoles.FindAsync([role], ct);
            if (row is null)
            {
                row = new AiRoleEntity { Role = role, Model = r.Model };
                db.AiRoles.Add(row);
            }
            row.ProviderId = provider.Id;
            row.Model = r.Model;
            row.MaxOutputTokens = r.MaxOutputTokens;
        }
        await db.SaveChangesAsync(ct);
    }
}
