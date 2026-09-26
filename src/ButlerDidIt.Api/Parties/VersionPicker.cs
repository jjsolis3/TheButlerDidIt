namespace ButlerDidIt.Api.Parties;

/// <summary>
/// "Surprise me": picks which version of a story a party plays, so even the host doesn't
/// know the killer. The pick happens when the host presses "Begin the evening", once every
/// guest has a character, and ranks the versions by:
/// <list type="number">
/// <item>How often this host has played it: never first, so nobody replays a killer they know.</item>
/// <item>Whether its killer is played by a guest tonight rather than by the narrator.</item>
/// <item>Chance, to break ties.</item>
/// </list>
/// </summary>
public static class VersionPicker
{
    public const string Surprise = "surprise";

    /// <summary>One version of a story and who the killer is in it.</summary>
    public sealed record Candidate(string Id, string KillerId);

    /// <param name="Fits">True when the killer is played by a guest tonight.</param>
    public sealed record Deal(string Id, bool Fits);

    /// <param name="versions">Every version of the story (the original included).</param>
    /// <param name="timesPlayed">How often this host has played each version (missing = never).</param>
    /// <param name="cast">The characters guests are playing tonight.</param>
    public static Deal Pick(IReadOnlyList<Candidate> versions, IReadOnlyDictionary<string, int> timesPlayed, IReadOnlySet<string> cast, Random random)
    {
        if (versions.Count == 0) throw new ArgumentException("A story needs at least one version.", nameof(versions));
        // A tuple sorts field by field, which is exactly the ranking above. Shuffling first makes the
        // final order among equals random (OrderBy is a stable sort, so it keeps the shuffled order).
        var best = versions
            .OrderBy(_ => random.Next())
            .OrderBy(v => (timesPlayed.GetValueOrDefault(v.Id), cast.Contains(v.KillerId) ? 0 : 1))
            .First();
        return new Deal(best.Id, cast.Contains(best.KillerId));
    }
}
