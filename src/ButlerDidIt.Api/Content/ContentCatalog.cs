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
    /// The scenario as played: the document plus any generated portraits, scene art and voice clips, and the host's
    /// own uploads (see MediaOverlay). Cached until <see cref="Invalidate"/>.
    ///
    /// The AI's media is made for each version (a version's words differ). A host's uploads are kept on the original
    /// mystery and play in every version of it, so a version takes its own AI media plus the original's uploads.
    /// </summary>
    public async Task<Scenario> GetScenarioAsync(AppDbContext db, string id, CancellationToken ct = default)
    {
        if (_scenarioCache.TryGetValue(id, out var cached)) return cached;
        var scenario = await GetBaseScenarioAsync(db, id, ct);
        // The row says which mystery a version belongs to (versions the AI writes don't say so in their document).
        var original = await db.Scenarios.AsNoTracking().Where(s => s.Id == id).Select(s => s.VariantOf).FirstOrDefaultAsync(ct) ?? id;
        var rows = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == id || m.ScenarioId == original)
            .Select(m => new { m.ScenarioId, m.Key, m.AssetId }).ToListAsync(ct);
        var assetIds = rows.Select(r => r.AssetId).Distinct().ToList();
        var uploads = (await db.MediaAssets.AsNoTracking().Where(a => assetIds.Contains(a.Id) && a.Provider == Media.MediaService.Upload)
            .Select(a => a.Id).ToListAsync(ct)).ToHashSet();

        var media = rows.Where(r => r.ScenarioId == id).ToDictionary(r => r.Key, r => Media.MediaStore.Url(r.AssetId));
        var uploaded = rows.Where(r => r.ScenarioId == original && uploads.Contains(r.AssetId)).ToList();
        foreach (var r in uploaded) media[r.Key] = Media.MediaStore.Url(r.AssetId); // the host's choice wins
        var playable = MediaOverlay.Apply(scenario, media, uploaded.Select(r => r.Key).ToHashSet());
        // A mystery that names no background sound plays its theme's.
        if (playable.Soundscape is null)
        {
            var themeDoc = await db.Themes.AsNoTracking().Where(t => t.Slug == playable.ThemeSlug).Select(t => t.Document).FirstOrDefaultAsync(ct);
            playable = ScenarioDefaults.Apply(playable, themeDoc is null ? null : GameJson.Deserialize<ThemeDefinition>(themeDoc));
        }
        _originalOf[id] = original;
        _scenarioCache[id] = playable;
        return playable;
    }

    // Which mystery each cached version belongs to, so all of them can be forgotten together.
    private readonly ConcurrentDictionary<string, string> _originalOf = new();

    /// <summary>
    /// Forget a mystery and all its versions, e.g. after the host changed its media (which every version plays).
    /// Only this server forgets them; with several servers, the others keep their copies until they restart (#125).
    /// </summary>
    public void InvalidateFamily(string originalId)
    {
        _scenarioCache.TryRemove(originalId, out _);
        foreach (var (id, original) in _originalOf)
            if (original == originalId) _scenarioCache.TryRemove(id, out _);
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
