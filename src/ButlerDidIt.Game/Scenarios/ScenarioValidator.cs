using System.Text.RegularExpressions;

namespace ButlerDidIt.Game.Scenarios;

/// <summary>
/// Checks that a mystery is internally consistent and fair to solve.
///
/// A broken reference (a clue addressed to a character who doesn't exist) would
/// crash or confuse a live party, so every scenario is validated when it is
/// loaded. In milestone 2 the AI generator runs the same checks and retries
/// until they pass, which is why this lives in the Game project and not the API.
/// </summary>
public static class ScenarioValidator
{
    /// <summary>How many genuine (non-red-herring) clues must point at the murderer.</summary>
    public const int MinCluesAgainstMurderer = 3;

    public static IReadOnlyList<string> Validate(Scenario s)
    {
        var errors = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) errors.Add(message);
        }

        var characterIds = s.Characters.Select(c => c.Id).ToHashSet();
        var actIds = s.Acts.Select(a => a.Id).ToHashSet();

        Check(s.Characters.Count >= 2, "A mystery needs at least two characters.");
        Check(s.Acts.Count >= 1, "A mystery needs at least one act.");
        Check(s.MinPlayers >= 1 && s.MinPlayers <= s.MaxPlayers, "MinPlayers must be between 1 and MaxPlayers.");
        Check(s.MaxPlayers <= s.Characters.Count, "MaxPlayers cannot exceed the number of characters.");

        CheckUnique(s.Characters.Select(c => c.Id), "character", errors);
        CheckUnique(s.Clues.Select(c => c.Id), "clue", errors);
        CheckUnique(s.Acts.Select(a => a.Id), "act", errors);
        CheckUnique(s.Accusation.Motives.Select(o => o.Id), "motive", errors);
        CheckUnique(s.Accusation.Methods.Select(o => o.Id), "method", errors);
        CheckUnique(s.Characters.SelectMany(c => c.Private.Secrets).Select(x => x.Id), "secret", errors);

        // The solution must reference real things, and the murderer must always be
        // present at the party (as a player or an NPC), otherwise it is unsolvable.
        var murderer = s.FindCharacter(s.Solution.MurdererId);
        Check(murderer is not null, $"Solution murderer '{s.Solution.MurdererId}' is not a character.");
        Check(murderer?.Required ?? true, "The murderer must be a required character so they are always at the party.");
        Check(murderer?.KillerEligible ?? true, $"'{s.Solution.MurdererId}' is marked as never the killer (killerEligible: false).");
        Check(s.Accusation.Motives.Any(o => o.Id == s.Solution.MotiveId), $"Solution motive '{s.Solution.MotiveId}' is not an accusation option.");
        Check(s.Accusation.Methods.Any(o => o.Id == s.Solution.MethodId), $"Solution method '{s.Solution.MethodId}' is not an accusation option.");
        Check(s.Accusation.Motives.Count >= 2, "Offer at least two motives to choose from.");
        Check(s.Accusation.Methods.Count >= 2, "Offer at least two methods to choose from.");
        Check(s.Solution.Explanation.Count > 0, "The solution needs an explanation for the reveal.");

        foreach (var c in s.Characters)
        {
            Check(!string.IsNullOrWhiteSpace(c.PublicBio), $"Character '{c.Id}' needs a public bio.");
            Check(!string.IsNullOrWhiteSpace(c.Private.Alibi), $"Character '{c.Id}' needs an alibi.");
            foreach (var secret in c.Private.Secrets)
            {
                Check(secret.UnlockAct >= 0 && secret.UnlockAct <= s.Acts.Count,
                    $"Secret '{secret.Id}' unlocks in act {secret.UnlockAct}, which does not exist.");
            }
            foreach (var actId in c.Private.Lines.Keys)
            {
                Check(actIds.Contains(actId), $"Character '{c.Id}' has lines for unknown act '{actId}'.");
            }
        }

        foreach (var clue in s.Clues)
        {
            Check(clue.Act >= 1 && clue.Act <= s.Acts.Count, $"Clue '{clue.Id}' is in act {clue.Act}, which does not exist.");
            foreach (var target in clue.PointsTo)
            {
                Check(characterIds.Contains(target), $"Clue '{clue.Id}' points to unknown character '{target}'.");
            }
            if (clue.Visibility == ClueVisibility.Private)
            {
                Check(clue.Recipient is not null && characterIds.Contains(clue.Recipient),
                    $"Private clue '{clue.Id}' needs a valid recipient.");
            }
            if (clue.Puzzle is not null)
            {
                Check(clue.Puzzle.Answers.Count > 0, $"Puzzle on clue '{clue.Id}' needs at least one answer.");
            }
        }

        var allCues = s.Prologue.Concat(s.Finale).Concat(s.Acts.SelectMany(a => a.Cues));
        foreach (var cue in allCues.Where(c => c.Type == CueType.Line))
        {
            Check(cue.Speaker is not null && characterIds.Contains(cue.Speaker),
                $"A line cue has unknown speaker '{cue.Speaker}'.");
        }

        // Fairness: players must be able to deduce the answer from the clues.
        var cluesAgainstMurderer = s.Clues.Count(c => !c.RedHerring && c.PointsTo.Contains(s.Solution.MurdererId));
        Check(cluesAgainstMurderer >= MinCluesAgainstMurderer,
            $"Only {cluesAgainstMurderer} genuine clue(s) point to the murderer; at least {MinCluesAgainstMurderer} are needed.");

        // Solvable by the room, not just on paper: a private clue's holder may keep it to themselves
        // (the murderer certainly will), so enough of the evidence must be seen by everyone.
        var publicAgainstMurderer = s.Clues.Count(c => !c.RedHerring && c.Visibility == ClueVisibility.Public && c.PointsTo.Contains(s.Solution.MurdererId));
        Check(publicAgainstMurderer >= MinCluesAgainstMurderer,
            $"Only {publicAgainstMurderer} genuine public clue(s) point to the murderer; at least {MinCluesAgainstMurderer} must be public, " +
            "because a private clue's holder may never share it.");

        // Suspense: the murderer must not be the only suspect clues point at.
        var otherSuspects = s.Clues.SelectMany(c => c.PointsTo).Where(id => id != s.Solution.MurdererId).Distinct().Count();
        Check(otherSuspects >= 2, "Clues should cast suspicion on at least two innocent characters.");

        // Family mysteries promise no alcohol. Checked here, not just in tests, so text
        // written by the AI or in the editor is held to the same promise as ours.
        if (s.ContentRating == ContentRating.Family)
        {
            foreach (var word in AlcoholWordsIn(s))
                errors.Add($"Family mysteries can't mention alcohol, but this one says \"{word}\". Use cocoa, lemonade or juice instead.");
        }

        return errors;
    }

    private static readonly string[] AlcoholWords = ["wine", "rum", "beer", "gin", "whisky", "whiskey", "vodka", "brandy", "grog", "cocktail", "drunk", "booze"];

    /// <summary>Alcohol words anywhere in the mystery's text. Whole words only, so "ginger" or "Virginia" are fine.</summary>
    public static IReadOnlyList<string> AlcoholWordsIn(Scenario s)
    {
        var text = GameJson.Serialize(s).ToLowerInvariant();
        return AlcoholWords.Where(w => Regex.IsMatch(text, $@"\b{w}s?\b")).ToList();
    }

    private static void CheckUnique(IEnumerable<string> ids, string kind, List<string> errors)
    {
        foreach (var dup in ids.GroupBy(i => i).Where(g => g.Count() > 1))
        {
            errors.Add($"Duplicate {kind} id '{dup.Key}'.");
        }
    }
}
