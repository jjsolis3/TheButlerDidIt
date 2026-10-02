using System.Security.Cryptography;
using System.Text;
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
///   clue/{clueId}/{titleHash}          a clue card's picture, drawn from its title only
///
/// And keys only a host's uploads use (#110 step 2):
///   video/prologue, video/{actId}, video/finale   a scene's video, which replaces its narration and pictures
///   music, music/{actId}                          a background track for the evening, or for one act
///
/// A host's upload is the host's choice, so it wins over anything: an uploaded portrait replaces a hand-placed one.
/// </summary>
public static class MediaOverlay
{
    public static string Portrait(string characterId) => $"portrait/{characterId}";
    public const string Victim = "victim";
    public const string Setting = "setting";
    public static string Cue(string section, int index) => $"cue/{section}/{index}";
    public static string Line(string characterId, string actId, int index) => $"line/{characterId}/{actId}/{index}";

    public const string Prologue = "prologue";
    public const string Finale = "finale";

    /// <summary>A scene's video: <see cref="Prologue"/>, an act's id, or <see cref="Finale"/>.</summary>
    public static string Video(string section) => $"video/{section}";

    public const string Music = "music";
    public static string ActMusic(string actId) => $"music/{actId}";

    /// <summary>
    /// A clue's picture is drawn from its title, so the key includes a hash of the title:
    /// if the clue is renamed (in the editor, or by a version), the old picture no longer
    /// matches and a new one is made instead of showing the wrong object.
    /// </summary>
    public static string ClueImage(string clueId, string title) =>
        $"clue/{clueId}/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title.Trim())))[..8]}";

    /// <param name="media">Files by key, generated and uploaded.</param>
    /// <param name="uploaded">The keys a host uploaded: those replace hand-placed media too, where generated media only fills gaps.</param>
    public static Scenario Apply(Scenario scenario, IReadOnlyDictionary<string, string> media, IReadOnlySet<string>? uploaded = null)
    {
        if (media.Count == 0) return scenario;
        var root = JsonSerializer.SerializeToNode(scenario, GameJson.Options)!.AsObject();
        uploaded ??= new HashSet<string>();
        void Fill(JsonObject obj, string property, string key)
        {
            if (!media.TryGetValue(key, out var url)) return;
            if (uploaded.Contains(key)) obj[property] = url;
            else SetIfEmpty(obj, property, url);
        }

        foreach (var character in root["characters"]!.AsArray().OfType<JsonObject>())
        {
            var id = character["id"]!.GetValue<string>();
            Fill(character, "portrait", Portrait(id));

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

        foreach (var clue in root["clues"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var id = clue["id"]?.GetValue<string>();
            var title = clue["title"]?.GetValue<string>();
            if (id is not null && title is not null) Fill(clue, "image", ClueImage(id, title));
        }

        if (root["victim"] is JsonObject victim) Fill(victim, "portrait", Victim);
        if (root["setting"] is JsonObject setting) Fill(setting, "image", Setting);
        Fill(root, "music", Music);

        ApplyCues(root, "prologue", Prologue, media);
        ApplyCues(root, "finale", Finale, media);
        foreach (var act in root["acts"]!.AsArray().OfType<JsonObject>())
        {
            var actId = act["id"]!.GetValue<string>();
            ApplyCues(act, "cues", actId, media);
            Fill(act, "music", ActMusic(actId));
        }

        return root.Deserialize<Scenario>(GameJson.Options)!;
    }

    /// <summary>
    /// A scene's cues. With a video for the scene, the video is the scene: it replaces the narration, pictures, music
    /// and sounds. The characters' lines (an NPC's, voiced on stage) and the drinking toasts still follow, in order.
    /// Otherwise each cue's empty file is filled in.
    /// </summary>
    private static void ApplyCues(JsonObject owner, string property, string section, IReadOnlyDictionary<string, string> media)
    {
        var array = owner[property] as JsonArray ?? [];
        if (media.TryGetValue(Video(section), out var video))
        {
            var kept = array.OfType<JsonObject>()
                .Where(c => c["type"]?.GetValue<string>() is "line" or "toast")
                .Select(c => c.DeepClone());
            owner[property] = new JsonArray([new JsonObject { ["type"] = "video", ["src"] = video }, .. kept]);
            return;
        }
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
