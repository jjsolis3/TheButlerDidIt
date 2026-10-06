using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Checks a room before anyone plays it: every reference points somewhere, every answer
/// can be typed, and, most importantly, the room can actually be escaped. It proves that by
/// playing the room the way a group would: in each stage, keep searching every spot they can,
/// looking closely at every item, putting together every pair that fits and solving every puzzle
/// in sight whose items (and spots, and cipher key) are in hand, until nothing more happens; if the stage
/// isn't finished by then, the room is broken. Puzzles that start out of sight (#134) only count once
/// that play has found them, and a final lock once the rest of its stage is solved and its order has been read.
///
/// That play-through is exact, not just hopeful, because items are either used up (by one puzzle
/// or one recipe) or tools (needed to search a spot or look at an item, never used up), never both:
/// so no order of play can paint the group into a corner.
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
    /// <param name="seeds">
    /// How many puzzle sets to build and play. The full check (<see cref="SeedsChecked"/>) takes a second or two;
    /// the room editor's check as you type uses a handful, and saving always runs the full one.
    /// </param>
    public static List<string> Validate(EscapeRoom room, int seeds = SeedsChecked)
    {
        var template = TemplateErrors(room);
        template.AddRange(LengthErrors(room));
        if (template.Count > 0) return template;

        // Every length and difficulty is its own room to escape: a shorter or easier game must never
        // need a key from a puzzle it leaves out.
        var lengths = room.PlayableLengths;
        foreach (var minutes in lengths)
        {
            foreach (var difficulty in Enum.GetValues<EscapeDifficulty>())
            {
                var at = (lengths.Count > 1 ? $"At {minutes} minutes: " : "") + (difficulty == EscapeDifficulty.Normal ? "" : $"On {difficulty}: ");
                if (!RoomVariants.IsTemplated(room))
                {
                    var errors = ValidateConcrete(RoomLengths.Cut(RoomVariants.Build(room, 0, difficulty), minutes, difficulty));
                    if (errors.Count > 0) return errors.Select(e => at + e).ToList();
                    continue;
                }
                for (var seed = 0; seed < seeds; seed++)
                {
                    var errors = ValidateConcrete(RoomLengths.Cut(RoomVariants.Build(room, seed, difficulty), minutes, difficulty));
                    if (errors.Count > 0) return errors.Select(e => $"{at}With puzzle set {seed}: {e}").ToList();
                }
            }
        }
        return [];
    }

    /// <summary>
    /// Every problem with one puzzle set, checked the way games play it: built and cut at each length and
    /// difficulty together. (A set built for Normal and then cut for Easy is never played, and can hide its
    /// only real cipher key on a spot Easy leaves out.)
    /// </summary>
    public static List<string> ValidateGame(EscapeRoom room, long seed)
    {
        var errors = new List<string>();
        foreach (var minutes in room.PlayableLengths)
            foreach (var difficulty in Enum.GetValues<EscapeDifficulty>())
                errors.AddRange(ValidateConcrete(RoomLengths.Cut(RoomVariants.Build(room, seed, difficulty), minutes, difficulty))
                    .Select(e => $"At {minutes} minutes on {difficulty}: {e}"));
        return errors;
    }

    /// <summary>The lengths a host can pick, and the shortest one still being a real game.</summary>
    public const int MinPuzzlesInShortestGame = 4;
    private static readonly int[] AllowedLengths = [30, 45, 60];

    private static List<string> LengthErrors(EscapeRoom room)
    {
        var errors = new List<string>();
        if (room.Lengths.Count > 0)
        {
            foreach (var bad in room.Lengths.Where(l => !AllowedLengths.Contains(l)).Distinct())
                errors.Add($"A room's lengths are 30, 45 or 60 minutes, not {bad}.");
            if (!room.Lengths.Contains(room.TimeLimitMinutes))
                errors.Add($"The lengths must include the room's time limit ({room.TimeLimitMinutes} minutes).");
        }
        foreach (var p in room.Puzzles.Where(p => p.MinMinutes is { } m && !room.PlayableLengths.Contains(m)))
            errors.Add($"Puzzle '{p.Id}' is kept for {p.MinMinutes}-minute games, which isn't one of the room's lengths.");
        if (errors.Count == 0 && room.PlayableLengths.Count > 1)
        {
            var shortest = room.PlayableLengths[0];
            var kept = room.Puzzles.Count(p => RoomLengths.Plays(p, shortest, EscapeDifficulty.Easy));
            if (kept < MinPuzzlesInShortestGame)
                errors.Add($"A {shortest}-minute game keeps only {kept} puzzles; keep at least {MinPuzzlesInShortestGame}.");
        }
        return errors;
    }

    private static List<string> TemplateErrors(EscapeRoom room)
    {
        var errors = new List<string>();
        foreach (var p in room.Puzzles)
        {
            // A built room has the lights themselves (Grid) instead of the generator that made them.
            if (p.Kind == PuzzleKind.Switches && p.Generator?.Type != GeneratorType.Switches && p.Grid is null)
                errors.Add($"Puzzle '{p.Id}' is a Switches puzzle, so it needs a Switches generator to set its lights.");
            if (p.Generator is not { } g) continue;
            var prompts = p.Variants.Select(v => v.Prompt).OfType<string>().Prepend(p.Prompt).ToList();
            void Needs(string placeholder)
            {
                if (prompts.Any(t => !t.Contains(placeholder))) errors.Add($"Puzzle '{p.Id}': the prompt must include {placeholder}.");
            }
            var kind = g.Type switch
            {
                GeneratorType.DigitFacts or GeneratorType.ColorDigits or GeneratorType.Sequence or GeneratorType.Deduction or GeneratorType.Final => PuzzleKind.Code,
                GeneratorType.Switches => PuzzleKind.Switches,
                _ => PuzzleKind.Text,
            };
            if (p.Kind != kind) errors.Add($"Puzzle '{p.Id}' uses a {g.Type} generator, so it must be a {kind} puzzle.");
            switch (g.Type)
            {
                case GeneratorType.DigitFacts or GeneratorType.ColorDigits or GeneratorType.WordSequence:
                {
                    var (placeholders, what) = g.Type switch
                    {
                        GeneratorType.DigitFacts => (new[] { "{ordinal}", "{fact}" }, "facts"),
                        GeneratorType.ColorDigits => (new[] { "{color}", "{digit}" }, "colours"),
                        _ => (new[] { "{ordinal}", "{word}" }, "words"),
                    };
                    var pool = RoomVariants.Pool(g);
                    if (g.Count is < 2 or > 10) errors.Add($"Puzzle '{p.Id}': a generator makes 2 to 10 pieces.");
                    if (g.Count > pool) errors.Add($"Puzzle '{p.Id}' needs {g.Count} different {what} but has only {pool}.");
                    foreach (var ph in placeholders.Where(ph => !g.PieceTemplate.Contains(ph)))
                        errors.Add($"Puzzle '{p.Id}': the piece template must include {ph}.");
                    break;
                }
                case GeneratorType.Cipher:
                    Needs("{cipher}");
                    if (g.Words.Count == 0 || g.Words.Any(w => w.Length is 0 or > 12 || !w.All(char.IsAsciiLetter)))
                        errors.Add($"Puzzle '{p.Id}': a cipher needs words of 1 to 12 plain letters (A to Z).");
                    if (g.DecoyWords.Any(w => w.Length is 0 or > 12 || !w.All(char.IsAsciiLetter)))
                        errors.Add($"Puzzle '{p.Id}': decoy words are 1 to 12 plain letters (A to Z).");
                    foreach (var w in g.DecoyWords.Where(w => g.Words.Contains(w, StringComparer.OrdinalIgnoreCase)))
                        errors.Add($"Puzzle '{p.Id}': \"{w}\" is one of its words, so it can't be a decoy too.");
                    if (g.Cipher is CipherType.Shift or CipherType.Symbols or CipherType.Morse && !RoomTexts(room).Any(t => t.Contains($"{{key:{p.Id}}}")))
                        errors.Add($"Puzzle '{p.Id}': write its key somewhere the group can find it, with {{key:{p.Id}}}.");
                    break;
                case GeneratorType.Sequence:
                    Needs("{sequence}");
                    break;
                case GeneratorType.Deduction:
                    Needs("{items}");
                    if (g.Words.Distinct(StringComparer.OrdinalIgnoreCase).Count() < 3 || g.Words.Any(string.IsNullOrWhiteSpace))
                        errors.Add($"Puzzle '{p.Id}': a deduction needs at least 3 different things to line up.");
                    if (g.PieceTemplate.Length > 0 && !g.PieceTemplate.Contains("{clue}"))
                        errors.Add($"Puzzle '{p.Id}': the piece template must include {{clue}}.");
                    break;
                case GeneratorType.Final:
                {
                    var others = room.StageOf(p.Id)?.Puzzles.Count(id => id != p.Id) ?? 0;
                    var marks = FinalLocks.MarksOf(g);
                    if (marks.Distinct().Count() != marks.Count || marks.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 16 || m.Any(char.IsDigit)))
                        errors.Add($"Puzzle '{p.Id}': a final lock's marks must all be different and short, with no digits in them.");
                    if (marks.Count < others)
                        errors.Add($"Puzzle '{p.Id}': each of the {others} other puzzles in its stage leaves a mark, but it has only {marks.Count} marks.");
                    if (!RoomVariants.OrderPlaces(room).ContainsKey(p.Id))
                        errors.Add($"Puzzle '{p.Id}': write the order of its marks somewhere the group can find it, with {{order:{p.Id}}}.");
                    if (p.Answers.Count > 0 || p.Pieces.Count > 0 || p.Variants.Any(v => v.Answers is not null || v.Pieces is not null))
                        errors.Add($"Puzzle '{p.Id}': a final lock's code is built from its stage, so it has no answers or clue pieces of its own.");
                    if (p.RevealedBy is not null)
                        errors.Add($"Puzzle '{p.Id}': a final lock comes into sight by itself once the rest of its stage is solved, so it can't have revealedBy.");
                    break;
                }
            }
        }
        foreach (var stage in room.Stages.Where(st => st.Puzzles.Count(id => room.FindPuzzle(id)?.Generator?.Type == GeneratorType.Final) > 1))
            errors.Add($"Stage '{stage.Id}' has more than one final lock; give it one.");
        errors.AddRange(RevealErrors(room));
        var finals = room.Puzzles.Where(p => p.Generator?.Type == GeneratorType.Final).Select(p => p.Id).ToHashSet();
        foreach (var id in RoomTexts(room).SelectMany(t => FinalLocks.OrderPlaceholder().Matches(t)).Select(m => m.Groups[1].Value).Distinct())
        {
            if (!finals.Contains(id)) errors.Add($"The room writes {{order:{id}}}, but '{id}' isn't a final lock.");
        }
        var ciphers = room.Puzzles.Where(p => p.Generator?.Type == GeneratorType.Cipher).Select(p => p.Id).ToHashSet();
        foreach (var id in RoomTexts(room).SelectMany(t => RoomVariants.KeyPlaceholder().Matches(t)).Select(m => m.Groups[1].Value).Distinct())
        {
            if (!ciphers.Contains(id)) errors.Add($"The room writes {{key:{id}}}, but '{id}' isn't a cipher puzzle.");
        }
        return errors;
    }

    /// <summary>
    /// What brings a puzzle into sight (#134) must be in its own stage: a spot in its scene or another puzzle there
    /// (not the final lock, which is always last), or an item that exists. Whether it can actually happen in time is
    /// for the play-through.
    /// </summary>
    private static IEnumerable<string> RevealErrors(EscapeRoom room)
    {
        foreach (var p in room.Puzzles.Where(p => p.RevealedBy is not null))
        {
            var stage = room.StageOf(p.Id);
            var (kind, id) = Reveals.Parse(p.RevealedBy!);
            var error = kind switch
            {
                Reveals.Spot when stage?.Scene?.Objects.Any(o => o.Id == id) != true => $"is found by searching '{id}', which isn't a spot in its stage's scene",
                Reveals.Puzzle when id == p.Id => "can't be found by solving itself",
                Reveals.Puzzle when stage?.Puzzles.Contains(id) != true => $"is found by solving '{id}', which isn't a puzzle in its stage",
                Reveals.Puzzle when room.FindPuzzle(id)?.Generator?.Type == GeneratorType.Final => $"is found by solving '{id}', the final lock, but nothing comes after that",
                Reveals.Item when room.FindItem(id) is null => $"is found by holding item '{id}', which doesn't exist",
                "" => $"has revealedBy \"{p.RevealedBy}\"; use \"spot:<id>\", \"puzzle:<id>\" or \"item:<id>\"",
                _ => null,
            };
            if (error is not null) yield return $"Puzzle '{p.Id}' {error}.";
        }
    }

    /// <summary>Every piece of text a room can show, for the checks that look at wording.</summary>
    private static IEnumerable<string> RoomTexts(EscapeRoom room)
    {
        yield return room.Title;
        yield return room.Synopsis;
        yield return room.Intro;
        yield return room.EscapedText;
        yield return room.FailedText;
        foreach (var s in room.Stages)
        {
            yield return s.Title;
            yield return s.Description;
        }
        foreach (var o in room.SceneObjects)
        {
            foreach (var t in new[] { o.Label, o.Look, o.Clue, o.LockedText }.OfType<string>()) yield return t;
        }
        foreach (var p in room.Puzzles)
        {
            foreach (var t in new[] { p.Title, p.Prompt, p.SolvedText }.Concat(p.Pieces).Concat(p.Hints)) yield return t;
            // A final lock's marks show on the screens too.
            foreach (var mark in (p.Generator?.Marks ?? []).Concat(p.Final?.Parts.Select(x => x.Mark) ?? [])) yield return mark;
            foreach (var v in p.Variants)
            {
                foreach (var t in new[] { v.Prompt, v.SolvedText }.OfType<string>().Concat(v.Pieces ?? []).Concat(v.Hints ?? [])) yield return t;
            }
        }
        foreach (var i in room.Items)
        {
            foreach (var t in new[] { i.Name, i.Description, i.Inspect }.OfType<string>()) yield return t;
        }
        foreach (var r in room.Recipes) yield return r.Text;
        if (room.GameMaster is { } gm)
        {
            yield return gm.Name;
            yield return gm.Persona;
        }
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
        Duplicates(room.SceneObjects.Select(o => o.Id), "scene object", errors);

        var itemIds = room.Items.Select(i => i.Id).ToHashSet();
        SceneErrors(room, itemIds, errors);
        DecoyKeyErrors(room, errors);
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
                case PuzzleKind.Search:
                {
                    var here = room.StageOf(p.Id)?.Scene?.Objects.Select(o => o.Id).ToHashSet() ?? [];
                    if (p.Finds.Count == 0) errors.Add($"Puzzle '{p.Id}' is a Search puzzle, so it must list the spots to find.");
                    foreach (var id in p.Finds.Where(id => !here.Contains(id)))
                        errors.Add($"Puzzle '{p.Id}' needs spot '{id}' searched, which isn't in the scene of its stage.");
                    if (p.Answers.Count > 0 || p.Requires.Count > 0)
                        errors.Add($"Puzzle '{p.Id}' is a Search puzzle: it can't have answers or need items (put the tool on the spot instead).");
                    break;
                }
                case PuzzleKind.Switches:
                    if (p.Grid is null) errors.Add($"Puzzle '{p.Id}' is a Switches puzzle with no lights.");
                    if (p.Answers.Count > 0) errors.Add($"Puzzle '{p.Id}' is a Switches puzzle and can't have answers.");
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

        // Where each item comes from, and what uses it up or needs it as a tool.
        var given = room.Puzzles.SelectMany(p => p.Rewards)
            .Concat(room.SceneObjects.Select(o => o.Gives).OfType<string>())
            .Concat(room.Items.Select(i => i.InspectGives).OfType<string>())
            .Concat(room.Recipes.Select(r => r.Makes));
        foreach (var item in given.GroupBy(i => i).Where(g => g.Count() > 1))
            errors.Add($"Item '{item.Key}' is given out in more than one place.");
        var usedUp = room.Puzzles.SelectMany(p => p.Requires).Concat(room.Recipes.SelectMany(r => r.Items.Distinct())).ToList();
        foreach (var item in usedUp.GroupBy(i => i).Where(g => g.Count() > 1))
            errors.Add($"Item '{item.Key}' is used up in more than one place, so using it one way would make the other impossible.");
        var tools = room.SceneObjects.Select(o => o.Requires).Concat(room.Items.Select(i => i.InspectRequires)).OfType<string>().ToHashSet();
        foreach (var item in usedUp.Distinct().Where(tools.Contains))
            errors.Add($"Item '{item}' is needed as a tool but also used up somewhere; if it's used up first, the group is stuck.");

        foreach (var hidden in HidingErrors(room)) errors.Add(hidden);
        foreach (var text in RoomTexts(room).Where(t => RoomVariants.KeyPlaceholder().IsMatch(t)).Take(1))
            errors.Add($"The room writes a key for a puzzle that isn't a cipher: “{text}”.");
        foreach (var text in RoomTexts(room).Where(t => FinalLocks.OrderPlaceholder().IsMatch(t)).Take(1))
            errors.Add($"The room writes a final lock's order where the group can't read it (a spot, an item or a puzzle's prompt can hold it): “{text}”.");
        foreach (var f in room.Puzzles.Where(p => p.Final is { } lk && lk.Parts.Count < FinalLocks.MinParts))
            errors.Add($"Puzzle '{f.Id}' is a final lock built from {f.Final!.Parts.Count} other puzzles in this game; its stage needs at least {FinalLocks.MinParts}, so the code can't just be guessed.");

        if (errors.Count == 0) errors.AddRange(PlayThrough(room));
        if (room.ContentRating == ContentRating.Family) errors.AddRange(FamilyCheck(room));
        return errors;
    }

    private static void SceneErrors(EscapeRoom room, HashSet<string> itemIds, List<string> errors)
    {
        foreach (var stage in room.Stages)
        {
            if (stage.Scene is not { } scene) continue;
            if (scene.Width <= 0 || scene.Height <= 0) errors.Add($"Stage '{stage.Id}': the scene needs a width and a height.");
            foreach (var o in scene.Objects)
            {
                if (!SceneProps.Known.Contains(o.Prop)) errors.Add($"Spot '{o.Id}' is a '{o.Prop}', which the screens can't draw. Use one of: {string.Join(", ", SceneProps.Known.Order())}.");
                if (string.IsNullOrWhiteSpace(o.Label) || string.IsNullOrWhiteSpace(o.Look)) errors.Add($"Spot '{o.Id}' needs a label and a look.");
                if (o.W <= 0 || o.H <= 0 || o.X < 0 || o.Y < 0 || o.X + o.W > scene.Width || o.Y + o.H > scene.Height)
                    errors.Add($"Spot '{o.Id}' must fit inside the {scene.Width}×{scene.Height} scene.");
                foreach (var item in new[] { o.Gives, o.Requires }.OfType<string>().Where(i => !itemIds.Contains(i)))
                    errors.Add($"Spot '{o.Id}' mentions item '{item}', which doesn't exist.");
                if (o.Requires is not null && o.Requires == o.Gives) errors.Add($"Spot '{o.Id}' can't need the item it gives.");
            }
        }
        foreach (var i in room.Items)
        {
            foreach (var item in new[] { i.InspectRequires, i.InspectGives }.OfType<string>().Where(x => !itemIds.Contains(x)))
                errors.Add($"Item '{i.Id}' mentions item '{item}', which doesn't exist.");
            if (i.Inspect is null && (i.InspectRequires ?? i.InspectGives) is not null)
                errors.Add($"Item '{i.Id}' needs inspect text to go with what a close look needs or gives.");
            if (i.InspectGives == i.Id || i.InspectRequires == i.Id) errors.Add($"Item '{i.Id}' can't give or need itself.");
        }
        foreach (var r in room.Recipes)
        {
            if (r.Items.Distinct().Count() != 2) errors.Add($"The recipe for '{r.Makes}' must combine exactly two different items.");
            foreach (var item in r.Items.Append(r.Makes).Where(i => !itemIds.Contains(i)))
                errors.Add($"The recipe for '{r.Makes}' mentions item '{item}', which doesn't exist.");
            if (r.Items.Contains(r.Makes)) errors.Add($"The recipe for '{r.Makes}' can't use what it makes.");
        }
        foreach (var pair in room.Recipes.GroupBy(r => string.Join("+", r.Items.Order())).Where(g => g.Count() > 1))
            errors.Add($"Two recipes combine the same items ({pair.Key}).");
    }

    /// <summary>
    /// A solo player gets one piece of each puzzle on their phone and the rest hidden. A stage with
    /// hiding spots must have enough of them, or pieces would pile up on the phone after all.
    /// </summary>
    private static IEnumerable<string> HidingErrors(EscapeRoom room)
    {
        foreach (var stage in room.Stages)
        {
            var spots = stage.Scene?.Objects.Count(o => o.HidesPieces) ?? 0;
            if (spots == 0) continue;
            var needed = stage.Puzzles.Select(id => room.FindPuzzle(id)).OfType<EscapePuzzle>().Sum(p => Math.Max(0, p.Pieces.Count - 1));
            if (needed > spots)
                yield return $"Stage '{stage.Id}' has {spots} hiding spots, but a solo player needs {needed} for the clue pieces nobody else holds.";
        }
    }

    /// <summary>
    /// Plays the room stage by stage. In each stage it repeats, until nothing changes: search every spot
    /// it can, look closely at every item it holds, put together every pair that fits, notice every puzzle
    /// that has come into sight (#134), and solve every puzzle in sight it's able to. A stage still unfinished
    /// after that is stuck.
    /// </summary>
    private static IEnumerable<string> PlayThrough(EscapeRoom room)
    {
        var inventory = new HashSet<string>();
        var held = new HashSet<string>(); // everything ever held, for keys written on an item that's since been used
        var examined = new HashSet<string>();
        var inspected = new HashSet<string>();
        var seenPuzzles = new HashSet<string>(); // every puzzle the group has had in sight, for keys written in a puzzle's own text
        var revealed = new HashSet<string>();
        foreach (var stage in room.Stages)
        {
            var all = stage.Puzzles.Select(id => room.FindPuzzle(id)!).ToList();
            var open = all.ToList();
            var spots = stage.Scene?.Objects ?? [];
            var hidingSpots = spots.Where(o => o.HidesPieces).Select(o => o.Id).ToList();
            // Every item held while this stage is open, as the engine looks each time the group gains one: an "item:" reveal.
            var heldHere = new HashSet<string>(inventory);

            bool InSight(EscapePuzzle p) => (p.RevealedBy is null && p.Final is null) || revealed.Contains(p.Id);
            bool Due(EscapePuzzle p) => p.Final is not null
                ? open.All(o => o == p)
                : Reveals.Parse(p.RevealedBy!) switch
                {
                    (Reveals.Spot, var id) => examined.Contains(id),
                    (Reveals.Puzzle, var id) => !open.Any(o => o.Id == id),
                    (Reveals.Item, var id) => heldHere.Contains(id),
                    _ => false,
                };
            bool RevealDue()
            {
                var found = open.Where(p => !InSight(p) && Due(p)).ToList();
                foreach (var p in found)
                {
                    revealed.Add(p.Id);
                    seenPuzzles.Add(p.Id);
                }
                return found.Count > 0;
            }
            bool KeySeen(string place)
            {
                var (kind, id) = (place[..place.IndexOf(':')], place[(place.IndexOf(':') + 1)..]);
                return kind switch
                {
                    "object" => examined.Contains(id),
                    "item" => held.Contains(id),
                    "inspect" => inspected.Contains(id),
                    _ => seenPuzzles.Contains(id),
                };
            }
            bool Solvable(EscapePuzzle p) =>
                InSight(p)
                && p.Requires.All(inventory.Contains)
                && p.Finds.All(examined.Contains)
                // With a real key and decoys, the group needs every key it can find, to compare them.
                && VisibleKeyPlaces(room, p).All(KeySeen)
                // Its pieces might be hidden in any hiding spot of the stage: all of them may need searching.
                && (p.Pieces.Count < 2 || hidingSpots.All(examined.Contains))
                // A final lock's code reads in the order the room writes somewhere: the group has to have read it.
                && (p.Final is null || p.Final.OrderAt.Any(KeySeen));
            void Gain(string? item)
            {
                if (item is null) return;
                inventory.Add(item);
                held.Add(item);
                heldHere.Add(item);
            }

            // What's in sight as the stage opens. With nothing at all, the group wouldn't know where to start.
            RevealDue();
            seenPuzzles.UnionWith(all.Where(InSight).Select(p => p.Id));
            if (!all.Any(InSight))
            {
                yield return $"Stage '{stage.Id}' opens with nothing in sight: leave at least one of its puzzles without revealedBy, so the group has somewhere to start.";
                yield break;
            }

            for (var progress = true; progress;)
            {
                progress = false;
                foreach (var o in spots.Where(o => !examined.Contains(o.Id) && (o.Requires is null || inventory.Contains(o.Requires))))
                {
                    examined.Add(o.Id);
                    Gain(o.Gives);
                    progress = true;
                }
                foreach (var item in inventory.Select(room.FindItem).OfType<EscapeItem>().ToList())
                {
                    if (item.Inspect is null || inspected.Contains(item.Id) || (item.InspectRequires is { } tool && !inventory.Contains(tool))) continue;
                    inspected.Add(item.Id);
                    Gain(item.InspectGives);
                    progress = true;
                }
                foreach (var r in room.Recipes.Where(r => r.Items.All(inventory.Contains)))
                {
                    inventory.ExceptWith(r.Items);
                    Gain(r.Makes);
                    progress = true;
                }
                if (RevealDue()) progress = true;
                foreach (var p in open.Where(Solvable).ToList())
                {
                    open.Remove(p);
                    inventory.ExceptWith(p.Requires);
                    foreach (var reward in p.Rewards) Gain(reward);
                    progress = true;
                }
            }
            if (open.Count == 0) continue;

            // A puzzle in sight that can't be solved says more than one that was never found.
            var stuck = open.FirstOrDefault(InSight) ?? open[0];
            var why = new List<string>();
            if (!InSight(stuck))
            {
                why.Add(stuck.Final is not null ? "is a final lock that never comes into sight" : $"is never found (\"{stuck.RevealedBy}\" can't happen in time)");
            }
            else
            {
                var missing = stuck.Requires.Where(i => !inventory.Contains(i)).Select(i => $"'{i}'").ToList();
                if (missing.Count > 0) why.Add($"needs {string.Join(", ", missing)}, which nothing earlier gives out");
                var unsearched = stuck.Finds.Concat(stuck.Pieces.Count >= 2 ? hidingSpots : []).Where(id => !examined.Contains(id)).Distinct().Select(id => $"'{id}'").ToList();
                if (unsearched.Count > 0) why.Add($"needs {string.Join(", ", unsearched)} searched, which can't be reached");
                if (!VisibleKeyPlaces(room, stuck).All(KeySeen)) why.Add("has a key somewhere the group can't reach in time");
                if (stuck.Final is { } final && !final.OrderAt.Any(KeySeen)) why.Add("is a final lock whose order isn't written anywhere the group can reach");
            }
            yield return $"Stage '{stage.Id}' can't be finished: puzzle '{stuck.Id}' {string.Join(" and ", why)}.";
            yield break;
        }
    }

    /// <summary>The key places a group can find in this game: a spot left out at this difficulty holds no key for it.</summary>
    public static List<string> VisibleKeyPlaces(EscapeRoom room, EscapePuzzle p)
    {
        var spots = room.SceneObjects.Select(o => o.Id).ToHashSet();
        return p.KeyAt.Where(place => !place.StartsWith("object:") || spots.Contains(place["object:".Length..])).ToList();
    }

    /// <summary>
    /// A cipher whose key is written in several places (a real key and decoys): at most three in one game, the real
    /// key reads the answer, no decoy does, and a symbols or Morse decoy reads as one of the room's decoy words, a real
    /// word, so the group has to reason about which key is right instead of spotting gibberish.
    /// </summary>
    private static void DecoyKeyErrors(EscapeRoom room, List<string> errors)
    {
        // A key written only on spots left out at this difficulty (a Hard-only spot, say) can't be found in this game at all.
        foreach (var p in room.Puzzles.Where(p => p.Decoder is not null && p.KeyAt.Count > 0 && VisibleKeyPlaces(room, p).Count == 0))
            errors.Add($"Puzzle '{p.Id}': its key is written only on spots this game leaves out; write {{key:{p.Id}}} on a spot every difficulty has.");
        foreach (var p in room.Puzzles.Where(p => p.Decoder?.Candidates is { Count: > 1 }))
        {
            var d = p.Decoder!;
            var visible = VisibleKeyPlaces(room, p).ToHashSet();
            var shown = d.Candidates!.Where(c => visible.Contains(c.Place)).ToList();
            if (shown.Count > 3) errors.Add($"Puzzle '{p.Id}': {shown.Count} keys in one game is too many to compare; use at most 3.");
            if (shown.Count(c => c.Real) != 1) { errors.Add($"Puzzle '{p.Id}': exactly one of its keys must be the real one."); continue; }
            foreach (var c in shown)
            {
                var reads = PuzzleGenerators.Decode(d.Type, d.Encoded, c.Key);
                if (c.Real && !p.Answers.Any(a => string.Equals(a, reads, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Puzzle '{p.Id}': the real key reads \"{reads}\", not the answer.");
                if (!c.Real && p.Answers.Any(a => string.Equals(a, reads, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Puzzle '{p.Id}': a decoy key also reads the answer.");
                if (!c.Real && d.Type is CipherType.Symbols or CipherType.Morse && !c.FromDecoyWords)
                    errors.Add($"Puzzle '{p.Id}': add more \"decoyWords\" shaped like {p.Answers[0].ToUpperInvariant()} (same length and repeated letters), so every wrong key reads a real word.");
            }
        }
    }

    private static IEnumerable<string> FamilyCheck(EscapeRoom room)
    {
        var texts = RoomTexts(room).ToList();
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
