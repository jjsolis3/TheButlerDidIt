using System.Collections.Concurrent;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Content;

public sealed class ContentOptions
{
    /// <summary>Folder containing themes/&lt;slug&gt;/... Defaults to ./content next to the app.</summary>
    public string Root { get; set; } = "content";
}

/// <summary>
/// Syncs the hand-written content folder into the database at startup and
/// serves scenarios to the game with an in-memory cache.
///
/// The database is the source of truth at runtime so AI-generated scenarios
/// (milestone 2) and hand-written ones are loaded the same way. Scenarios never
/// change during a party, so caching them after the first read is safe.
/// </summary>
public sealed class ContentCatalog(IServiceScopeFactory scopes, ILogger<ContentCatalog> log)
{
    private readonly ConcurrentDictionary<string, Scenario> _scenarioCache = new();

    public async Task SeedAsync(string contentRoot, CancellationToken ct = default)
    {
        var themes = ContentLibrary.Load(contentRoot);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        foreach (var loaded in themes)
        {
            var theme = await db.Themes.FindAsync([loaded.Theme.Slug], ct);
            var themeJson = GameJson.Serialize(loaded.Theme);
            if (theme is null)
            {
                db.Themes.Add(new ThemeEntity
                {
                    Slug = loaded.Theme.Slug, Name = loaded.Theme.Name, Document = themeJson,
                    SortOrder = loaded.Theme.SortOrder, UpdatedAt = now,
                });
            }
            else
            {
                theme.Name = loaded.Theme.Name;
                theme.Document = themeJson;
                theme.SortOrder = loaded.Theme.SortOrder;
                theme.UpdatedAt = now;
            }

            foreach (var scenario in loaded.Scenarios)
            {
                var entity = await db.Scenarios.FindAsync([scenario.Id], ct);
                var json = GameJson.Serialize(scenario);
                if (entity is null)
                {
                    entity = new ScenarioEntity
                    {
                        Id = scenario.Id, ThemeSlug = scenario.ThemeSlug, Title = scenario.Title,
                        Document = json, Source = ScenarioSource.Handwritten,
                    };
                    db.Scenarios.Add(entity);
                }
                entity.Title = scenario.Title;
                entity.ThemeSlug = scenario.ThemeSlug;
                entity.MinPlayers = scenario.MinPlayers;
                entity.MaxPlayers = scenario.MaxPlayers;
                entity.ContentRating = scenario.ContentRating;
                entity.Document = json;
                entity.UpdatedAt = now;
                _scenarioCache[scenario.Id] = scenario;
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded {Themes} themes and {Scenarios} scenarios from {Root}",
            themes.Count, themes.Sum(t => t.Scenarios.Count), contentRoot);
    }

    public async Task<Scenario> GetScenarioAsync(AppDbContext db, string id, CancellationToken ct = default)
    {
        if (_scenarioCache.TryGetValue(id, out var cached)) return cached;
        var entity = await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException($"Scenario '{id}' not found.");
        var scenario = GameJson.Deserialize<Scenario>(entity.Document);
        _scenarioCache[id] = scenario;
        return scenario;
    }
}
