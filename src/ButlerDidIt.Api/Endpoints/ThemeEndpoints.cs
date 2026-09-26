using System.Security.Claims;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Endpoints;

public sealed record ScenarioCard(string Id, string Title, string Synopsis, int MinPlayers, int MaxPlayers, int EstimatedMinutes, ContentRating ContentRating, int CharacterCount, bool AiGenerated, bool Custom,
    IReadOnlyList<VersionOption> Versions);

/// <summary>
/// One version of a story (same place and cast, different killer). The label is deliberately
/// bland ("Version B"), so choosing from the list gives nothing away.
/// </summary>
public sealed record VersionOption(string Id, string Label, bool PlayedByMe);

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
            // Versions are listed under their story, not as separate mysteries.
            var versionsOf = scenarios.Where(s => s.VariantOf is not null).ToLookup(s => s.VariantOf!);
            HashSet<string> played = userId is null ? [] : (await db.Parties.AsNoTracking()
                .Where(p => p.HostUserId == userId && p.Status != PartyStatus.Lobby).Select(p => p.ScenarioId).Distinct().ToListAsync(ct)).ToHashSet();
            IReadOnlyList<VersionOption> Versions(ScenarioEntity story)
            {
                var variants = versionsOf[story.Id].ToList();
                if (variants.Count == 0) return [];
                // Hand-written versions are lettered; versions the AI wrote for this host's parties
                // (listed only to them) are numbered in the order they were written.
                var written = new[] { story }.Concat(variants.Where(v => v.Source != ScenarioSource.AiGenerated).OrderBy(v => v.Id))
                    .Select((v, i) => new VersionOption(v.Id, $"Version {(char)('A' + i)}", played.Contains(v.Id)));
                var remixed = variants.Where(v => v.Source == ScenarioSource.AiGenerated).OrderBy(v => v.UpdatedAt)
                    .Select((v, i) => new VersionOption(v.Id, $"✨ AI version {i + 1}", played.Contains(v.Id)));
                return written.Concat(remixed).ToList();
            }
            return themes.Select(t =>
            {
                var cards = scenarios
                    .Where(s => s.ThemeSlug == t.Slug && s.VariantOf is null)
                    .Select(e => (Entity: e, Scenario: GameJson.Deserialize<Scenario>(e.Document)))
                    .Select(x => new ScenarioCard(x.Scenario.Id, x.Scenario.Title, x.Scenario.Synopsis, x.Scenario.MinPlayers, x.Scenario.MaxPlayers,
                        x.Scenario.EstimatedMinutes, x.Scenario.ContentRating, x.Scenario.Characters.Count, x.Entity.Source == ScenarioSource.AiGenerated, x.Entity.Source == ScenarioSource.Custom,
                        Versions(x.Entity)))
                    .OrderBy(s => s.AiGenerated || s.Custom).ThenBy(s => s.Title)
                    .ToList();
                return new ThemeCard(GameJson.Deserialize<ThemeDefinition>(t.Document), cards);
            }).ToList();
        });
    }
}
