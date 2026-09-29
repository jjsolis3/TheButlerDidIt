using ButlerDidIt.Game;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>Loads every room from content/escape/*.json.</summary>
public static class EscapeLibrary
{
    public static List<EscapeRoom> Load(string escapeRoot)
    {
        if (!Directory.Exists(escapeRoot)) return [];
        return Directory.EnumerateFiles(escapeRoot, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(file => GameJson.Deserialize<EscapeRoom>(File.ReadAllText(file)))
            .ToList();
    }
}
