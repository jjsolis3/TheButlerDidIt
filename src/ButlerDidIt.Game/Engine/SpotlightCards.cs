using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Engine;

/// <summary>
/// The question shown on the big screen when someone takes the spotlight, so quieter guests
/// always know what to say and the room knows what to ask.
///
/// Built only from what the whole room already knows: the victim, the phase, and clues that
/// have been found publicly (never a private clue, a secret or the solution). A clue is only
/// used to ask about it, never to say whom it implicates.
/// </summary>
public static class SpotlightCards
{
    private static readonly string[] General =
    [
        "Where were you when {victim} died, and who can vouch for you?",
        "What did you really think of {victim}?",
        "Who here do you trust least, and why?",
        "What's one thing you saw tonight that nobody has mentioned yet?",
        "If you had to accuse someone right now, who would it be?",
    ];

    public static string Question(GameState s, Scenario scenario, Character c)
    {
        var victim = scenario.Victim.Name;
        if (s.Phase == Phase.CastReveal)
            return $"Introduce yourself: who are you, what brings you here tonight, and how did you know {victim}?";

        if (s.Confrontation is { } confrontation && scenario.FindClue(confrontation.ClueId) is { } evidence)
            return $"{c.Name}, explain “{evidence.Title}”. The room is listening.";

        // Clues everyone has seen that mention this character come first: they're what the room wants answered.
        var aboutThem = s.DroppedClues
            .Where(d => d.RecipientSeatId is null || d.SharedPublicly)
            .Select(d => scenario.FindClue(d.ClueId))
            .Where(clue => clue is not null && clue.PointsTo.Contains(c.Id))
            .Select(clue => clue!)
            .ToList();
        var turn = Math.Max(0, s.SpotlightTurns - 1);
        if (aboutThem.Count > 0 && turn % 2 == 0)
            return $"{c.Name}, what can you tell us about “{aboutThem[turn / 2 % aboutThem.Count].Title}”?";

        return $"{c.Name}: " + General[turn % General.Length].Replace("{victim}", victim);
    }
}
