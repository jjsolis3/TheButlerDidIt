using System.Text.Json;

namespace ButlerDidIt.Escape.Tests;

/// <summary>What a screen would actually show, for the privacy tests.</summary>
public static class ViewText
{
    /// <summary>
    /// Every string in a serialized view, decoded, one per line (with a line break before the first). The JSON
    /// writer escapes some characters (a curly apostrophe becomes \u2019), so searching the raw JSON could miss a
    /// leak. Whole values sit between line breaks, so a short answer can be matched exactly: $"\n{answer}\n".
    /// </summary>
    public static string Decoded(string json)
    {
        var values = new List<string>();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: values.Add(e.GetString()!); break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x); break;
            }
        }
        Walk(JsonDocument.Parse(json).RootElement);
        return "\n" + string.Join("\n", values) + "\n";
    }
}
