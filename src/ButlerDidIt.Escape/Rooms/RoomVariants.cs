using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Turns a room, a seed and a difficulty into one concrete play of it: every puzzle's variant is
/// picked and every generator is run. The same inputs always give exactly the same puzzles,
/// so a party can be saved and reloaded mid-game, and two groups on the same seed get a fair race.
///
/// Randomness comes from <see cref="SeededRandom"/>, a tiny fixed algorithm, rather than System.Random,
/// whose sequence isn't guaranteed to stay the same between .NET versions.
///
/// Difficulty: Normal plays the room as written. Easy deals one piece fewer per generated puzzle, picks
/// shorter cipher words and halves the hint penalty. Hard deals one more piece, picks longer words, and
/// drops the last (most revealing) step of every hint ladder that has two or more.
/// </summary>
public static partial class RoomVariants
{
    // Deterministic, so caching is safe: one concrete room per (room, seed, difficulty).
    private static readonly ConditionalWeakTable<EscapeRoom, ConcurrentDictionary<(long, EscapeDifficulty), EscapeRoom>> Cache = [];

    public static bool IsTemplated(EscapeRoom room) => room.Puzzles.Any(p => p.Variants.Count > 0 || p.Generator is not null);

    public static EscapeRoom Build(EscapeRoom room, long seed, EscapeDifficulty difficulty = EscapeDifficulty.Normal)
    {
        if (!IsTemplated(room) && difficulty == EscapeDifficulty.Normal) return room;
        return Cache.GetOrCreateValue(room).GetOrAdd((seed, difficulty), key => Make(room, key.Item1, key.Item2));
    }

    private static EscapeRoom Make(EscapeRoom room, long seed, EscapeDifficulty difficulty)
    {
        var keys = new Dictionary<string, string>();
        var built = room with
        {
            Puzzles = room.Puzzles.Select(p => Concrete(p, new SeededRandom(seed ^ SeededRandom.StableHash(p.Id)), difficulty, keys)).ToList(),
        };
        if (keys.Count > 0) built = WithKeys(built, keys);
        return difficulty switch
        {
            EscapeDifficulty.Easy => built with { HintPenaltySeconds = built.HintPenaltySeconds / 2 },
            // The last hint usually all but gives the answer: on Hard, hints only nudge.
            EscapeDifficulty.Hard => built with { Puzzles = built.Puzzles.Select(p => p.Hints.Count >= 2 ? p with { Hints = p.Hints[..^1] } : p).ToList() },
            _ => built,
        };
    }

    private static EscapePuzzle Concrete(EscapePuzzle p, SeededRandom rng, EscapeDifficulty difficulty, Dictionary<string, string> keys)
    {
        if (p.Variants.Count == 0 && p.Generator is null) return p;

        var v = p.Variants.Count == 0 ? null : p.Variants[rng.Next(p.Variants.Count)];
        string prompt = v?.Prompt ?? p.Prompt, solved = v?.SolvedText ?? p.SolvedText;
        List<string> answers = v?.Answers ?? p.Answers, pieces = v?.Pieces ?? p.Pieces, hints = v?.Hints ?? p.Hints;
        SwitchGrid? grid = null;
        CipherDecoder? decoder = null;
        List<string> lineup = [];

        if (p.Generator is { } g)
        {
            var made = Generate(g, rng, difficulty);
            answers = made.Grid is null ? [made.Answer] : [];
            pieces = made.Pieces;
            grid = made.Grid;
            lineup = made.Lineup ?? [];
            if (made.Key is { } key)
            {
                keys[p.Id] = key;
                decoder = new CipherDecoder(g.Cipher, key);
            }
            prompt = Fill(prompt, made);
            solved = Fill(solved, made);
            hints = hints.Select(h => Fill(h, made)).ToList();
        }

        // The concrete puzzle is fixed: no variants or generator left to pick from.
        return p with { Prompt = prompt, Answers = answers, Pieces = pieces, Hints = hints, SolvedText = solved, Grid = grid, Decoder = decoder, Lineup = lineup, Variants = [], Generator = null };
    }

    /// <param name="Fills">Placeholders for the prompt, hints and solved text, besides {answer}.</param>
    /// <param name="Key">For ciphers: what {key:&lt;puzzle id&gt;} becomes.</param>
    private sealed record Made(string Answer, List<string> Pieces, Dictionary<string, string> Fills, SwitchGrid? Grid = null, string? Key = null, List<string>? Lineup = null);

    private static Made Generate(PuzzleGenerator g, SeededRandom rng, EscapeDifficulty difficulty)
    {
        var count = PieceCount(g, difficulty);
        switch (g.Type)
        {
            case GeneratorType.DigitFacts:
            {
                var facts = rng.Pick(FactBank.Facts, count);
                return new Made(
                    string.Concat(facts.Select(f => f.Value)),
                    facts.Select((f, i) => g.PieceTemplate.Replace("{ordinal}", Ordinal(i)).Replace("{fact}", f.Text)).ToList(),
                    Old("", string.Join(", ", facts.Select(f => f.Short))));
            }
            case GeneratorType.ColorDigits:
            {
                var colors = rng.Pick(g.Colors, count);
                var digits = rng.Pick(Enumerable.Range(1, 9).ToList(), count);
                // The pieces are dealt in a shuffled order, so the order on the sign is the puzzle.
                var dealt = rng.Pick(Enumerable.Range(0, count).ToList(), count);
                return new Made(
                    string.Concat(digits),
                    dealt.Select(i => g.PieceTemplate.Replace("{color}", colors[i].ToUpperInvariant()).Replace("{digit}", digits[i].ToString())).ToList(),
                    Old(string.Join(", then ", colors.Select(c => $"{Capitalise(c)} duck")), ""));
            }
            case GeneratorType.WordSequence:
            {
                var words = rng.Pick(g.Words, count);
                return new Made(
                    string.Join(" ", words),
                    words.Select((w, i) => g.PieceTemplate.Replace("{ordinal}", Ordinal(i)).Replace("{word}", w)).ToList(),
                    Old("", ""));
            }
            case GeneratorType.Cipher:
            {
                var c = PuzzleGenerators.Cipher(g.Cipher, g.Words, difficulty, rng);
                return new Made(c.Word, [], New(("{cipher}", c.Encoded)), Key: c.Key);
            }
            case GeneratorType.Sequence:
            {
                var s = PuzzleGenerators.Sequence(difficulty, rng);
                return new Made(s.Next.ToString(), [], New(("{sequence}", s.Text)));
            }
            case GeneratorType.Deduction:
            {
                var d = PuzzleGenerators.Deduction(g.Words, difficulty, rng);
                var template = g.PieceTemplate.Length == 0 ? "{clue}" : g.PieceTemplate;
                return new Made(d.Answer, d.ClueTexts.Select(t => template.Replace("{clue}", t)).ToList(), New(("{items}", PuzzleGenerators.ListItems(d.Items))), Lineup: d.Items);
            }
            case GeneratorType.Switches:
            {
                var s = PuzzleGenerators.Switches(difficulty, rng);
                // {answer} names the lights to press, for a last hint.
                return new Made(string.Join(", ", s.Presses.Select(c => PuzzleGenerators.CellName(s.Grid.Size, c))), [], New(), s.Grid);
            }
            default:
                throw new InvalidOperationException($"Unknown generator {g.Type}.");
        }
    }

    /// <summary>
    /// How many pieces a DigitFacts, ColorDigits or WordSequence puzzle deals at this difficulty:
    /// one fewer on Easy (never below two), one more on Hard (while there's enough to choose from).
    /// </summary>
    public static int PieceCount(PuzzleGenerator g, EscapeDifficulty difficulty) => difficulty switch
    {
        EscapeDifficulty.Easy => Math.Max(2, g.Count - 1),
        EscapeDifficulty.Hard => Math.Max(g.Count, Math.Min(g.Count + 1, Math.Min(Pool(g), Ordinals.Length))),
        _ => g.Count,
    };

    /// <summary>How many different facts, colours or words the generator can choose from.</summary>
    public static int Pool(PuzzleGenerator g) => g.Type switch
    {
        GeneratorType.DigitFacts => FactBank.Facts.Count,
        GeneratorType.ColorDigits => Math.Min(g.Colors.Distinct().Count(), 9),
        _ => g.Words.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
    };

    private static Dictionary<string, string> Old(string order, string facts) => new() { ["{order}"] = order, ["{facts}"] = facts };
    private static Dictionary<string, string> New(params (string Placeholder, string Text)[] fills) => fills.ToDictionary(f => f.Placeholder, f => f.Text);

    private static string Fill(string text, Made m)
    {
        foreach (var (placeholder, value) in m.Fills) text = text.Replace(placeholder, value);
        return text.Replace("{answer}", m.Answer.ToUpperInvariant());
    }

    /// <summary>
    /// Writes each cipher's key wherever the room says {key:&lt;puzzle id&gt;}, and notes on the cipher
    /// where that is, so the validator can check the group can reach the key before they need it.
    /// </summary>
    private static EscapeRoom WithKeys(EscapeRoom room, Dictionary<string, string> keys)
    {
        var at = new Dictionary<string, List<string>>();
        void Note(string place, params string?[] texts)
        {
            foreach (var text in texts)
            {
                if (text is null) continue;
                foreach (Match m in KeyPlaceholder().Matches(text))
                {
                    var list = at.TryGetValue(m.Groups[1].Value, out var l) ? l : at[m.Groups[1].Value] = [];
                    if (!list.Contains(place)) list.Add(place);
                }
            }
        }
        foreach (var p in room.Puzzles) Note($"puzzle:{p.Id}", [p.Prompt, .. p.Pieces]);
        foreach (var o in room.SceneObjects) Note($"object:{o.Id}", o.Look, o.Clue);
        foreach (var i in room.Items) { Note($"item:{i.Id}", i.Description); Note($"inspect:{i.Id}", i.Inspect); }

        // An unknown puzzle id is left as it is: the validator reports it.
        string K(string text) => KeyPlaceholder().Replace(text, m => keys.TryGetValue(m.Groups[1].Value, out var key) ? key : m.Value);
        string? KN(string? text) => text is null ? null : K(text);

        return room with
        {
            Puzzles = room.Puzzles.Select(p => p with
            {
                Prompt = K(p.Prompt),
                Pieces = p.Pieces.Select(K).ToList(),
                Hints = p.Hints.Select(K).ToList(),
                SolvedText = K(p.SolvedText),
                KeyAt = keys.ContainsKey(p.Id) ? at.GetValueOrDefault(p.Id) ?? [] : p.KeyAt,
            }).ToList(),
            Stages = room.Stages.Select(s => s.Scene is null ? s : s with
            {
                Scene = s.Scene with { Objects = s.Scene.Objects.Select(o => o with { Look = K(o.Look), Clue = KN(o.Clue), LockedText = KN(o.LockedText) }).ToList() },
            }).ToList(),
            Items = room.Items.Select(i => i with { Description = K(i.Description), Inspect = KN(i.Inspect) }).ToList(),
        };
    }

    [GeneratedRegex(@"\{key:([^}\s]+)\}")]
    public static partial Regex KeyPlaceholder();

    private static readonly string[] Ordinals = ["FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH", "EIGHTH", "NINTH", "TENTH"];
    private static string Ordinal(int i) => Ordinals[i];
    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Everyday facts with a single-digit answer, for DigitFacts codes. Each must be unambiguous.</summary>
public static class FactBank
{
    public sealed record Fact(string Text, int Value, string Short);

    public static readonly IReadOnlyList<Fact> Facts =
    [
        new("the number of legs a snake has", 0, "snake legs"),
        new("the number of tails a cat has", 1, "a cat's tails"),
        new("the number of wheels on a bicycle", 2, "bicycle wheels"),
        new("the number of eyes a person has", 2, "eyes"),
        new("the number of sides on a triangle", 3, "triangle sides"),
        new("the number of wheels on a tricycle", 3, "tricycle wheels"),
        new("the number of zeros in one thousand", 3, "zeros in a thousand"),
        new("the number of sides on a square", 4, "square sides"),
        new("the number of seasons in a year", 4, "seasons"),
        new("the number of legs a horse has", 4, "horse legs"),
        new("the number of fingers on one hand", 5, "fingers on a hand"),
        new("the number of sides on a pentagon", 5, "pentagon sides"),
        new("the number of legs an insect has", 6, "insect legs"),
        new("the number of sides on a hexagon", 6, "hexagon sides"),
        new("the number of letters in the word ESCAPE", 6, "letters in ESCAPE"),
        new("the number of days in a week", 7, "days in a week"),
        new("the number of colours in a rainbow", 7, "rainbow colours"),
        new("the number of legs a spider has", 8, "spider legs"),
        new("the number of sides on a stop sign", 8, "stop-sign sides"),
        new("the number of lives a cat is said to have", 9, "a cat's nine lives"),
    ];
}
