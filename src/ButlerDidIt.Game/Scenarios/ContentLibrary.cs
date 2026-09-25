namespace ButlerDidIt.Game.Scenarios;

/// <summary>A theme is the "box" a party is chosen from: setting, mood, art direction.</summary>
public sealed class ThemeDefinition
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string Tagline { get; init; } = "";
    public string Era { get; init; } = "";
    public string Description { get; init; } = "";
    public ThemePalette Palette { get; init; } = new();

    /// <summary>Art direction used by image generation in milestone 3 so every picture in a theme matches.</summary>
    public string ArtStyle { get; init; } = "";

    public string? Cover { get; init; }
    public int SortOrder { get; init; } = 100;
}

public sealed class ThemePalette
{
    public string Background { get; init; } = "#0f0d0b";
    public string Surface { get; init; } = "#1c1814";
    public string Accent { get; init; } = "#c8a45a";
    public string Ink { get; init; } = "#f3ead8";
}

public sealed record LoadedTheme(ThemeDefinition Theme, IReadOnlyList<Scenario> Scenarios, string Directory);

/// <summary>
/// Reads themes and scenarios from the content folder:
///
///     content/themes/&lt;slug&gt;/theme.json
///     content/themes/&lt;slug&gt;/scenarios/*.json
///     content/themes/&lt;slug&gt;/media/...
///
/// Invalid scenarios throw with every validation error listed, so a typo in a
/// JSON file fails loudly at startup instead of halfway through a party.
/// </summary>
public static class ContentLibrary
{
    public static IReadOnlyList<LoadedTheme> Load(string contentRoot)
    {
        var themesDir = Path.Combine(contentRoot, "themes");
        if (!Directory.Exists(themesDir)) return [];

        var result = new List<LoadedTheme>();
        foreach (var dir in Directory.GetDirectories(themesDir).Order())
        {
            var themeFile = Path.Combine(dir, "theme.json");
            if (!File.Exists(themeFile)) continue;
            var theme = GameJson.Deserialize<ThemeDefinition>(File.ReadAllText(themeFile));

            var scenarios = new List<Scenario>();
            var scenarioDir = Path.Combine(dir, "scenarios");
            if (Directory.Exists(scenarioDir))
            {
                foreach (var file in Directory.GetFiles(scenarioDir, "*.json").Order())
                {
                    var scenario = GameJson.Deserialize<Scenario>(File.ReadAllText(file));
                    var errors = ScenarioValidator.Validate(scenario);
                    if (errors.Count > 0)
                    {
                        throw new InvalidDataException(
                            $"Scenario {Path.GetFileName(file)} is invalid:{Environment.NewLine}- " +
                            string.Join(Environment.NewLine + "- ", errors));
                    }
                    if (scenario.ThemeSlug != theme.Slug)
                    {
                        throw new InvalidDataException($"Scenario {scenario.Id} says theme '{scenario.ThemeSlug}' but lives in '{theme.Slug}'.");
                    }
                    scenarios.Add(scenario);
                }
            }
            result.Add(new LoadedTheme(theme, scenarios, dir));
        }
        return result.OrderBy(t => t.Theme.SortOrder).ThenBy(t => t.Theme.Name).ToList();
    }
}
