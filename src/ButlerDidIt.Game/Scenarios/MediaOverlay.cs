using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButlerDidIt.Game.Scenarios;

/// <summary>
/// Puts generated media (portraits, scene art, narration audio) into a scenario.
///
/// The scenario document stays exactly as written. Generated files are stored
/// separately as a map of media keys to URLs, and this function returns a copy of
/// the scenario with those URLs filled into the empty slots. The views and the
/// stage then use them like hand-placed media, and hand-placed media always wins.
///
/// Media keys:
///   portrait/{characterId}             character portrait
///   victim                             victim portrait
///   setting                            setting image
///   cue/prologue/{i}, cue/finale/{i}   a cue's image or audio
///   cue/{actId}/{i}                    a cue inside an act
///   line/{characterId}/{actId}/{i}     an NPC's spoken line
/// </summary>
public static class MediaOverlay
{
    public static string Portrait(string characterId) => $"portrait/{characterId}";
    public const string Victim = "victim";
    public const string Setting = "setting";
    public static string Cue(string section, int index) => $"cue/{section}/{index}";
    public static string Line(string characterId, string actId, int index) => $"line/{characterId}/{actId}/{index}";

    public static Scenario Apply(Scenario scenario, IReadOnlyDictionary<string, string> media)
    {
        if (media.Count == 0) return scenario;
        var root = JsonSerializer.SerializeToNode(scenario, GameJson.Options)!.AsObject();

        foreach (var character in root["characters"]!.AsArray().OfType<JsonObject>())
        {
            var id = character["id"]!.GetValue<string>();
            SetIfEmpty(character, "portrait", media.GetValueOrDefault(Portrait(id)));

            var priv = character["private"] as JsonObject;
            if (priv?["lines"] is not JsonObject lines) continue;
            var audio = priv["lineAudio"] as JsonObject ?? new JsonObject();
            foreach (var (actId, list) in lines)
            {
                var count = list?.AsArray().Count ?? 0;
                for (var i = 0; i < count; i++)
                {
                    if (media.TryGetValue(Line(id, actId, i), out var url) && audio[$"{actId}/{i}"] is null)
                        audio[$"{actId}/{i}"] = url;
                }
            }
            priv["lineAudio"] = audio;
        }

        if (root["victim"] is JsonObject victim) SetIfEmpty(victim, "portrait", media.GetValueOrDefault(Victim));
        if (root["setting"] is JsonObject setting) SetIfEmpty(setting, "image", media.GetValueOrDefault(Setting));

        ApplyCues(root["prologue"], "prologue", media);
        ApplyCues(root["finale"], "finale", media);
        foreach (var act in root["acts"]!.AsArray().OfType<JsonObject>())
        {
            ApplyCues(act["cues"], act["id"]!.GetValue<string>(), media);
        }

        return root.Deserialize<Scenario>(GameJson.Options)!;
    }

    private static void ApplyCues(JsonNode? cues, string section, IReadOnlyDictionary<string, string> media)
    {
        if (cues is not JsonArray array) return;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject cue) SetIfEmpty(cue, "src", media.GetValueOrDefault(Cue(section, i)));
        }
    }

    private static void SetIfEmpty(JsonObject obj, string property, string? value)
    {
        if (value is null) return;
        if (obj[property] is JsonValue existing && existing.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)) return;
        obj[property] = value;
    }
}
