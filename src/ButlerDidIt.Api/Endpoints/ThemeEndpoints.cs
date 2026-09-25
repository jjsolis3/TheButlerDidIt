using System.Security.Claims;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record ScenarioCard(string Id, string Title, string Synopsis, int MinPlayers, int MaxPlayers, int EstimatedMinutes, ContentRating ContentRating, int CharacterCount, bool AiGenerated, bool Custom);

public sealed record ThemeCard(ThemeDefinition Theme, IReadOnlyList<ScenarioCard> Scenarios);

public static class ThemeEndpoints
{
    public static void MapThemeEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: the home page shows the themes to everyone. Only summary fields
        // are returned, never the scenario document itself (it contains the solution).
        app.MapGet("/api/themes", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var themes = await db.Themes.AsNoTracking().OrderBy(t => t.SortOrder).ToListAsync(ct);
            // Everyone sees the hand-written mysteries; a signed-in host also sees the ones they generated.
            var scenarios = await db.Scenarios.AsNoTracking()
                .Where(s => s.ArchivedAt == null && (s.OwnerUserId == null || s.OwnerUserId == userId))
                .ToListAsync(ct);
            return themes.Select(t =>
            {
                var cards = scenarios
                    .Where(s => s.ThemeSlug == t.Slug)
                    .Select(e => (Entity: e, Scenario: GameJson.Deserialize<Scenario>(e.Document)))
                    .Select(x => new ScenarioCard(x.Scenario.Id, x.Scenario.Title, x.Scenario.Synopsis, x.Scenario.MinPlayers, x.Scenario.MaxPlayers,
                        x.Scenario.EstimatedMinutes, x.Scenario.ContentRating, x.Scenario.Characters.Count, x.Entity.Source == ScenarioSource.AiGenerated, x.Entity.Source == ScenarioSource.Custom))
                    .OrderBy(s => s.AiGenerated || s.Custom).ThenBy(s => s.Title)
                    .ToList();
                return new ThemeCard(GameJson.Deserialize<ThemeDefinition>(t.Document), cards);
            }).ToList();
        });
    }
}
