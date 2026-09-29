using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Checks a room before anyone plays it: every reference points somewhere, every answer
/// can be typed, and, most importantly, the room can actually be escaped. It proves that by
/// playing the room the way a group would: in each stage, keep solving any puzzle whose items
/// are already in hand; if a stage can't be finished that way, the room is broken.
/// </summary>
public static class EscapeRoomValidator
{
    // Family rooms can be spooky, never gruesome.
    private static readonly string[] FamilyUnsafeWords = ["blood", "kill", "murder", "corpse", "gore", "stab", "dead", "death", "torture"];

    /// <summary>How many seeds a templated room is built and checked with. Enough to cover every variant many times over.</summary>
    public const int SeedsChecked = 200;

    /// <summary>
    /// Every problem with the room. A room with variants or generators is checked the way it will
    /// be played: built from many seeds, each one validated (and played through) on its own.
    /// </summary>
    public static List<string> Validate(EscapeRoom room)
    {
        var template = TemplateErrors(room);
        if (template.Count > 0) return template;
        if (!RoomVariants.IsTemplated(room)) return ValidateConcrete(room);
        for (var seed = 0; seed < SeedsChecked; seed++)
        {
            var errors = ValidateConcrete(RoomVariants.Build(room, seed));
            if (errors.Count > 0) return errors.Select(e => $"With puzzle set {seed}: {e}").ToList();
        }
        return [];
    }

    private static List<string> TemplateErrors(EscapeRoom room)
    {
        var errors = new List<string>();
        foreach (var p in room.Puzzles)
        {
            if (p.Generator is not { } g) continue;
            var (kind, placeholders, pool) = g.Type switch
            {
                GeneratorType.DigitFacts => (PuzzleKind.Code, new[] { "{ordinal}", "{fact}" }, FactBank.Facts.Count),
                GeneratorType.ColorDigits => (PuzzleKind.Code, new[] { "{color}", "{digit}" }, Math.Min(g.Colors.Distinct().Count(), 9)),
                _ => (PuzzleKind.Text, new[] { "{ordinal}", "{word}" }, g.Words.Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            };
            if (p.Kind != kind) errors.Add($"Puzzle '{p.Id}' uses a {g.Type} generator, so it must be a {kind} puzzle.");
            if (g.Count is < 2 or > 10) errors.Add($"Puzzle '{p.Id}': a generator makes 2 to 10 pieces.");
            if (g.Count > pool) errors.Add($"Puzzle '{p.Id}' needs {g.Count} different {(g.Type == GeneratorType.WordSequence ? "words" : g.Type == GeneratorType.ColorDigits ? "colours" : "facts")} but has only {pool}.");
            foreach (var ph in placeholders.Where(ph => !g.PieceTemplate.Contains(ph)))
                errors.Add($"Puzzle '{p.Id}': the piece template must include {ph}.");
        }
        return errors;
    }

    /// <summary>A room with every puzzle fixed (no variants or generators left).</summary>
    private static List<string> ValidateConcrete(EscapeRoom room)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(room.Id)) errors.Add("The room needs an id.");
        if (room.TimeLimitMinutes <= 0) errors.Add("The time limit must be at least a minute.");
        if (room.HintPenaltySeconds < 0) errors.Add("The hint penalty can't be negative.");
        if (room.MinPlayers < 1 || room.MinPlayers > room.MaxPlayers) errors.Add("Player counts must satisfy 1 ≤ min ≤ max.");
        if (room.Stages.Count == 0) errors.Add("The room needs at least one stage.");
        if (room.GameMaster is { } gm)
        {
            if (gm.Name.Trim().Length is 0 or > 40) errors.Add("The game master's name must be 1–40 characters.");
            if (gm.Persona.Length > 400) errors.Add("Keep the game master's persona under 400 characters.");
        }

        Duplicates(room.Stages.Select(s => s.Id), "stage", errors);
        Duplicates(room.Puzzles.Select(p => p.Id), "puzzle", errors);
        Duplicates(room.Items.Select(i => i.Id), "item", errors);

        var itemIds = room.Items.Select(i => i.Id).ToHashSet();
        var staged = room.Stages.SelectMany(s => s.Puzzles).ToList();
        foreach (var id in staged.Where(id => room.FindPuzzle(id) is null)) errors.Add($"A stage lists puzzle '{id}', which doesn't exist.");
        foreach (var p in room.Puzzles)
        {
            var count = staged.Count(id => id == p.Id);
            if (count != 1) errors.Add($"Puzzle '{p.Id}' must be in exactly one stage (it's in {count}).");

            switch (p.Kind)
            {
                case PuzzleKind.Use:
                    if (p.Requires.Count == 0) errors.Add($"Puzzle '{p.Id}' is a Use puzzle, so it must require at least one item.");
                    if (p.Answers.Count > 0) errors.Add($"Puzzle '{p.Id}' is a Use puzzle and can't have answers.");
                    break;
                default:
                    if (p.Answers.Count == 0 || p.Answers.Any(a => Answers.Normalize(a).Length == 0))
                        errors.Add($"Puzzle '{p.Id}' needs at least one answer with letters or digits.");
                    if (p.Kind == PuzzleKind.Code && p.Answers.Any(a => !a.All(char.IsDigit)))
                        errors.Add($"Puzzle '{p.Id}' is a Code puzzle, so its answers must be digits only.");
                    break;
            }
            if (p.Hints.Count == 0) errors.Add($"Puzzle '{p.Id}' needs at least one hint.");
            foreach (var item in p.Requires.Concat(p.Rewards).Where(i => !itemIds.Contains(i)))
                errors.Add($"Puzzle '{p.Id}' mentions item '{item}', which doesn't exist.");
        }
        foreach (var item in room.Puzzles.SelectMany(p => p.Rewards).GroupBy(i => i).Where(g => g.Count() > 1))
            errors.Add($"Item '{item.Key}' is given out by more than one puzzle.");

        if (errors.Count == 0) errors.AddRange(PlayThrough(room));
        if (room.ContentRating == ContentRating.Family) errors.AddRange(FamilyCheck(room));
        return errors;
    }

    /// <summary>Solves the room greedily, stage by stage, and reports any stage that gets stuck.</summary>
    private static IEnumerable<string> PlayThrough(EscapeRoom room)
    {
        var inventory = new HashSet<string>();
        foreach (var stage in room.Stages)
        {
            var open = stage.Puzzles.Select(id => room.FindPuzzle(id)!).ToList();
            while (open.Count > 0)
            {
                var next = open.FirstOrDefault(p => p.Requires.All(inventory.Contains));
                if (next is null)
                {
                    var stuck = open[0];
                    yield return $"Stage '{stage.Id}' can't be finished: puzzle '{stuck.Id}' needs {string.Join(", ", stuck.Requires.Where(i => !inventory.Contains(i)).Select(i => $"'{i}'"))}, which no earlier puzzle gives out.";
                    yield break;
                }
                open.Remove(next);
                inventory.UnionWith(next.Rewards);
            }
        }
    }

    private static IEnumerable<string> FamilyCheck(EscapeRoom room)
    {
        var texts = new List<string> { room.Title, room.Synopsis, room.Intro, room.EscapedText, room.FailedText };
        texts.AddRange(room.Stages.SelectMany(s => new[] { s.Title, s.Description }));
        texts.AddRange(room.Puzzles.SelectMany(p => new[] { p.Title, p.Prompt, p.SolvedText }.Concat(p.Pieces).Concat(p.Hints)));
        texts.AddRange(room.Items.SelectMany(i => new[] { i.Name, i.Description }));
        if (room.GameMaster is { } gm) texts.AddRange([gm.Name, gm.Persona]);
        foreach (var word in FamilyUnsafeWords)
        {
            if (texts.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{word}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                yield return $"Family rooms can't mention '{word}'.";
        }
    }

    private static void Duplicates(IEnumerable<string> ids, string what, List<string> errors)
    {
        foreach (var dup in ids.GroupBy(i => i).Where(g => g.Count() > 1)) errors.Add($"Two {what}s share the id '{dup.Key}'.");
    }
}
