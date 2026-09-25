using System.Text.Json;
using System.Text.Json.Serialization;

namespace ButlerDidIt.Game;

/// <summary>
/// One shared set of JSON settings for scenarios, saved game state and the views
/// sent to browsers. camelCase matches JavaScript conventions, and enums are
/// written as strings ("Mingle" instead of 1) so the JSON is readable in the
/// database and in the browser's dev tools.
/// </summary>
public static class GameJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}.");
}
