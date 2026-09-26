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
                entity.VariantOf = scenario.VariantOf;
                entity.Document = json;
                entity.UpdatedAt = now;
                Invalidate(scenario.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded {Themes} themes and {Scenarios} scenarios from {Root}",
            themes.Count, themes.Sum(t => t.Scenarios.Count), contentRoot);
    }

    /// <summary>
    /// The scenario as played: the document plus any generated portraits, scene
    /// art and voice clips (see MediaOverlay). Cached until <see cref="Invalidate"/>.
    /// </summary>
    public async Task<Scenario> GetScenarioAsync(AppDbContext db, string id, CancellationToken ct = default)
    {
        if (_scenarioCache.TryGetValue(id, out var cached)) return cached;
        var scenario = await GetBaseScenarioAsync(db, id, ct);
        var media = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == id)
            .ToDictionaryAsync(m => m.Key, m => Media.MediaStore.Url(m.AssetId), ct);
        var playable = MediaOverlay.Apply(scenario, media);
        _scenarioCache[id] = playable;
        return playable;
    }

    /// <summary>The scenario exactly as written, without generated media (used to plan what media to create).</summary>
    public async Task<Scenario> GetBaseScenarioAsync(AppDbContext db, string id, CancellationToken ct = default)
    {
        var entity = await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException($"Scenario '{id}' not found.");
        return GameJson.Deserialize<Scenario>(entity.Document);
    }

    /// <summary>Forget the cached copy, e.g. after new media was generated.</summary>
    public void Invalidate(string scenarioId) => _scenarioCache.TryRemove(scenarioId, out _);
}
