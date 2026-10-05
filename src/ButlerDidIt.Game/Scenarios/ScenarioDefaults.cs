using System.Text.Json;

namespace ButlerDidIt.Game.Scenarios;

/// <summary>
/// What a mystery leaves to its theme, filled in when it's loaded for play: the background sound. A mystery the AI
/// wrote, or one written before soundscapes existed, then sounds like the rest of its theme with nothing to edit.
///
/// Like <see cref="MediaOverlay"/>, it works on a copy through JSON, because a scenario's properties are init-only.
/// </summary>
public static class ScenarioDefaults
{
    public static Scenario Apply(Scenario scenario, ThemeDefinition? theme)
    {
        if (scenario.Soundscape is not null || theme is null) return scenario;
        var root = JsonSerializer.SerializeToNode(scenario, GameJson.Options)!.AsObject();
        root["soundscape"] = JsonSerializer.SerializeToNode(theme.Soundscape, GameJson.Options);
        return root.Deserialize<Scenario>(GameJson.Options)!;
    }
}
