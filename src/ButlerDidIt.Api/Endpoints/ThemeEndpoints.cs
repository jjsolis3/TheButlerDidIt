using ButlerDidIt.Api.Data;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record ScenarioCard(string Id, string Title, string Synopsis, int MinPlayers, int MaxPlayers, int EstimatedMinutes, ContentRating ContentRating, int CharacterCount);

public sealed record ThemeCard(ThemeDefinition Theme, IReadOnlyList<ScenarioCard> Scenarios);

public static class ThemeEndpoints
{
    public static void MapThemeEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: the home page shows the themes to everyone. Only summary fields
        // are returned, never the scenario document itself (it contains the solution).
        app.MapGet("/api/themes", async (AppDbContext db, CancellationToken ct) =>
        {
            var themes = await db.Themes.AsNoTracking().OrderBy(t => t.SortOrder).ToListAsync(ct);
            var scenarios = await db.Scenarios.AsNoTracking().ToListAsync(ct);
            return themes.Select(t =>
            {
                var cards = scenarios
                    .Where(s => s.ThemeSlug == t.Slug)
                    .Select(s => GameJson.Deserialize<Scenario>(s.Document))
                    .Select(s => new ScenarioCard(s.Id, s.Title, s.Synopsis, s.MinPlayers, s.MaxPlayers, s.EstimatedMinutes, s.ContentRating, s.Characters.Count))
                    .OrderBy(s => s.Title)
                    .ToList();
                return new ThemeCard(GameJson.Deserialize<ThemeDefinition>(t.Document), cards);
            }).ToList();
        });
    }
}
