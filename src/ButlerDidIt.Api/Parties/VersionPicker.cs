namespace ButlerDidIt.Api.Parties;

/// <summary>
/// "Surprise me": picks which version of a story a party plays, so even the host doesn't
/// know the killer. Prefers a version this host has never played; once they've played them
/// all, the one they've played least. Ties are broken at random.
/// </summary>
public static class VersionPicker
{
    public const string Surprise = "surprise";

    /// <param name="versions">Every version id of the story (the original included).</param>
    /// <param name="timesPlayed">How often this host has played each version (missing = never).</param>
    public static string Pick(IReadOnlyList<string> versions, IReadOnlyDictionary<string, int> timesPlayed, Random random)
    {
        if (versions.Count == 0) throw new ArgumentException("A story needs at least one version.", nameof(versions));
        var fewest = versions.Min(v => timesPlayed.GetValueOrDefault(v));
        var candidates = versions.Where(v => timesPlayed.GetValueOrDefault(v) == fewest).ToList();
        return candidates[random.Next(candidates.Count)];
    }
}
