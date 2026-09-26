using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButlerDidIt.Game.Scenarios;

/// <summary>
/// Versions of a mystery: the same place, cast and story, with a different killer,
/// so a group can play the "same" mystery again, like dealing a new game of Clue.
///
/// A version is stored as a patch on the original, holding only what changes:
///
///     {
///       "variantOf": "death-at-blackwood-manor",
///       "variant": "B",
///       "solution": { ...the complete new solution... },
///       "characters": { "hargrove": { "private": { "backstory": "...", "secrets": [ ... ] } } },
///       "clues": { "cook-decanter": { "text": "...", "pointsTo": ["hargrove"] }, "old-clue": null },
///       "addClues": [ { ...a brand-new clue... } ],
///       "acts": { "act3": { "cues": [ ... ] } }
///     }
///
/// Rules:
/// <list type="bullet">
/// <item><c>characters</c>, <c>clues</c> and <c>acts</c> are objects keyed by id: name only the ones you change.</item>
/// <item>Objects merge field by field; a list (cues, secrets, lines, pointsTo…) or a plain value replaces the original's.</item>
/// <item><c>null</c> removes: a clue in <c>clues</c>, or a field anywhere else.</item>
/// <item><c>addClues</c> appends new clues.</item>
/// </list>
///
/// Keeping versions as patches means a fix to the shared story (a typo in the setting, a
/// better costume tip) is made once, in the original, and every version picks it up.
/// </summary>
public static class ScenarioVariants
{
    /// <summary>The id a version is stored and played under, e.g. "death-at-blackwood-manor--b".</summary>
    public static string VariantId(string originalId, string variant) => $"{originalId}--{variant.ToLowerInvariant()}";

    /// <summary>True when a content file is a version patch rather than a complete mystery.</summary>
    public static bool IsPatch(JsonObject document) => document.ContainsKey("variantOf");

    public static Scenario Apply(Scenario original, JsonObject patch)
    {
        var variant = patch["variant"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(variant)) throw new InvalidDataException($"A version of '{original.Id}' needs a \"variant\" name, e.g. \"B\".");

        // Work on a JSON copy of the original; the original object is never touched.
        var root = JsonSerializer.SerializeToNode(original, GameJson.Options)!.AsObject();

        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "variantOf" or "variant" or "id":
                    break; // set below; a version can't rename itself
                case "characters":
                    MergeById(root, "characters", value, allowRemove: false);
                    break;
                case "acts":
                    MergeById(root, "acts", value, allowRemove: false);
                    break;
                case "clues":
                    MergeById(root, "clues", value, allowRemove: true);
                    break;
                case "addClues":
                    foreach (var clue in value?.AsArray() ?? [])
                        root["clues"]!.AsArray().Add(clue?.DeepClone());
                    break;
                default:
                    root[key] = Merge(root[key], value);
                    break;
            }
        }

        root["id"] = VariantId(original.Id, variant);
        root["variantOf"] = original.Id;
        root["variant"] = variant;
        return root.Deserialize<Scenario>(GameJson.Options)!;
    }

    /// <summary>A list of objects with ids (characters, clues, acts), patched by an object keyed by those ids.</summary>
    private static void MergeById(JsonObject root, string listName, JsonNode? patch, bool allowRemove)
    {
        if (patch is not JsonObject byId) throw new InvalidDataException($"\"{listName}\" in a version must be an object keyed by id.");
        var list = root[listName]!.AsArray();
        foreach (var (id, change) in byId)
        {
            var index = list.ToList().FindIndex(node => node?["id"]?.GetValue<string>() == id);
            if (index < 0) throw new InvalidDataException($"A version changes {listName} '{id}', which the original doesn't have.");

            if (change is null)
            {
                if (!allowRemove) throw new InvalidDataException($"A version can't remove {listName} '{id}'; only clues can be removed.");
                list.RemoveAt(index);
            }
            else
            {
                list[index] = Merge(list[index], change);
            }
        }
    }

    /// <summary>Objects merge field by field; anything else (lists, text, numbers) is replaced.</summary>
    private static JsonNode? Merge(JsonNode? original, JsonNode? patch)
    {
        if (original is not JsonObject target || patch is not JsonObject changes) return patch?.DeepClone();
        var merged = target.DeepClone().AsObject();
        foreach (var (key, value) in changes)
        {
            if (value is null) merged.Remove(key);
            else merged[key] = Merge(merged[key], value);
        }
        return merged;
    }
}
