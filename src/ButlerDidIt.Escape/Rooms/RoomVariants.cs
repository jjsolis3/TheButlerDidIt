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

    /// <param name="cache">
    /// Keep the built room for the next command of the same game (the default). The validator passes false: it builds
    /// hundreds of puzzle sets nobody will play, and keeping them would hold hundreds of megabytes for as long as the room lives.
    /// </param>
    public static EscapeRoom Build(EscapeRoom room, long seed, EscapeDifficulty difficulty = EscapeDifficulty.Normal, bool cache = true)
    {
        if (!IsTemplated(room) && difficulty == EscapeDifficulty.Normal) return room;
        if (!cache) return Cache.TryGetValue(room, out var built) && built.TryGetValue((seed, difficulty), out var hit) ? hit : Make(room, seed, difficulty);
        return Cache.GetOrCreateValue(room).GetOrAdd((seed, difficulty), key => Make(room, key.Item1, key.Item2));
    }

    private static EscapeRoom Make(EscapeRoom room, long seed, EscapeDifficulty difficulty)
    {
        // Where each cipher's key is written: one place is real, the rest get decoys (see WithKeys).
        var places = KeyPlaces(room);
        var made = new Dictionary<string, PuzzleGenerators.CipherMade>();
        var built = room with
        {
            Puzzles = room.Puzzles.Select(p => Concrete(p, new SeededRandom(seed ^ SeededRandom.StableHash(p.Id)), difficulty,
                Math.Max(0, places.GetValueOrDefault(p.Id)?.Count - 1 ?? 0), made)).ToList(),
        };
        if (made.Count > 0) built = WithKeys(built, places, made, seed, difficulty);
        built = FinalLocks.Make(built, seed, OrderPlaces(room));
        return difficulty switch
        {
            EscapeDifficulty.Easy => built with { HintPenaltySeconds = built.HintPenaltySeconds / 2 },
            // The last hint usually all but gives the answer: on Hard, hints only nudge.
            EscapeDifficulty.Hard => built with { Puzzles = built.Puzzles.Select(p => p.Hints.Count >= 2 ? p with { Hints = p.Hints[..^1] } : p).ToList() },
            _ => built,
        };
    }

    private static EscapePuzzle Concrete(EscapePuzzle p, SeededRandom rng, EscapeDifficulty difficulty, int decoys, Dictionary<string, PuzzleGenerators.CipherMade> ciphers)
    {
        if (p.Variants.Count == 0 && p.Generator is null) return p;

        var v = p.Variants.Count == 0 ? null : p.Variants[rng.Next(p.Variants.Count)];
        string prompt = v?.Prompt ?? p.Prompt, solved = v?.SolvedText ?? p.SolvedText;
        List<string> answers = v?.Answers ?? p.Answers, pieces = v?.Pieces ?? p.Pieces, hints = v?.Hints ?? p.Hints;
        // A final lock is built from the rest of its stage (FinalLocks): its text keeps its placeholders until the game's puzzles are known.
        if (p.Generator is { Type: GeneratorType.Final })
            return p with { Prompt = prompt, Answers = [], Pieces = pieces, Hints = hints, SolvedText = solved, Variants = [] };
        SwitchGrid? grid = null;
        CipherDecoder? decoder = null;
        List<string> lineup = [];

        if (p.Generator is { } g)
        {
            var made = Generate(g, rng, difficulty, decoys);
            answers = made.Grid is null ? [made.Answer] : [];
            pieces = made.Pieces;
            grid = made.Grid;
            lineup = made.Lineup ?? [];
            if (made.Cipher is { } cipher)
            {
                // Mirror and numbers need no key, but still get a decoder: the phone shows their alphabet strip.
                if (g.Cipher is CipherType.Shift or CipherType.Symbols or CipherType.Morse) ciphers[p.Id] = cipher;
                decoder = new CipherDecoder(g.Cipher, cipher.Key, cipher.Encoded);
            }
            prompt = Fill(prompt, made);
            solved = Fill(solved, made);
            hints = hints.Select(h => Fill(h, made)).ToList();
        }

        // The concrete puzzle is fixed: no variants or generator left to pick from.
        return p with { Prompt = prompt, Answers = answers, Pieces = pieces, Hints = hints, SolvedText = solved, Grid = grid, Decoder = decoder, Lineup = lineup, Variants = [], Generator = null };
    }

    /// <param name="Fills">Placeholders for the prompt, hints and solved text, besides {answer}.</param>
    /// <param name="Cipher">For ciphers: the real key and the decoys, written where the room says {key:&lt;puzzle id&gt;}.</param>
    private sealed record Made(string Answer, List<string> Pieces, Dictionary<string, string> Fills, SwitchGrid? Grid = null, PuzzleGenerators.CipherMade? Cipher = null, List<string>? Lineup = null);

    private static Made Generate(PuzzleGenerator g, SeededRandom rng, EscapeDifficulty difficulty, int decoys = 0)
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
                    Dealt(count, facts: string.Join(", ", facts.Select(f => f.Short))));
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
                    Dealt(count, order: string.Join(", then ", colors.Select(c => g.Thing.Length == 0 ? Capitalise(c) : $"{Capitalise(c)} {g.Thing}"))));
            }
            case GeneratorType.WordSequence:
            {
                var words = rng.Pick(g.Words, count);
                return new Made(
                    string.Join(" ", words),
                    words.Select((w, i) => g.PieceTemplate.Replace("{ordinal}", Ordinal(i)).Replace("{word}", w)).ToList(),
                    Dealt(count));
            }
            case GeneratorType.Cipher:
            {
                var c = PuzzleGenerators.Cipher(g.Cipher, g.Words, difficulty, rng, g.DecoyWords, decoys);
                // Only the ciphers that need a key have one to write in the room; numbers and mirror read without.
                return new Made(c.Word, [], New(("{cipher}", c.Encoded)), Cipher: c);
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

    /// <summary>
    /// The fills for a code or password dealt across the phones. {count} is how many digits or words this game deals, as a
    /// word ("a {count}-digit lock" reads "a four-digit lock" on Hard): Easy deals one fewer and Hard one more, so a room's
    /// text never says the number itself.
    /// </summary>
    private static Dictionary<string, string> Dealt(int count, string order = "", string facts = "") =>
        new() { ["{order}"] = order, ["{facts}"] = facts, ["{count}"] = CountWord(count) };

    private static readonly string[] CountWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    /// <summary>A count as a word ("four"), for {count}: a room's text never says the number itself.</summary>
    public static string CountWord(int count) => count >= 0 && count < CountWords.Length ? CountWords[count] : count.ToString();
    private static Dictionary<string, string> New(params (string Placeholder, string Text)[] fills) => fills.ToDictionary(f => f.Placeholder, f => f.Text);

    private static string Fill(string text, Made m)
    {
        foreach (var (placeholder, value) in m.Fills) text = text.Replace(placeholder, value);
        return text.Replace("{answer}", m.Answer.ToUpperInvariant());
    }

    /// <summary>
    /// Every place a room writes {key:&lt;puzzle id&gt;}, per cipher, in room order: "puzzle:&lt;id&gt;" (its prompt or
    /// pieces), "object:&lt;id&gt;" (a spot's look or clue), "item:&lt;id&gt;" (an item's description), "inspect:&lt;id&gt;"
    /// (what a close look at it shows).
    /// </summary>
    public static Dictionary<string, List<string>> KeyPlaces(EscapeRoom room) => Places(room, KeyPlaceholder());

    /// <summary>Every place a room writes a final lock's order ({order:&lt;puzzle id&gt;}), per lock, as <see cref="KeyPlaces"/> does for keys.</summary>
    public static Dictionary<string, List<string>> OrderPlaces(EscapeRoom room) => Places(room, FinalLocks.OrderPlaceholder());

    private static Dictionary<string, List<string>> Places(EscapeRoom room, Regex placeholder)
    {
        var at = new Dictionary<string, List<string>>();
        void Note(string place, params string?[] texts)
        {
            foreach (var text in texts)
            {
                if (text is null) continue;
                foreach (Match m in placeholder.Matches(text))
                {
                    var list = at.TryGetValue(m.Groups[1].Value, out var l) ? l : at[m.Groups[1].Value] = [];
                    if (!list.Contains(place)) list.Add(place);
                }
            }
        }
        foreach (var p in room.Puzzles) Note($"puzzle:{p.Id}", [p.Prompt, .. p.Pieces]);
        foreach (var o in room.SceneObjects) Note($"object:{o.Id}", o.Look, o.Clue);
        foreach (var i in room.Items) { Note($"item:{i.Id}", i.Description); Note($"inspect:{i.Id}", i.Inspect); }
        return at;
    }

    /// <summary>
    /// Writes each cipher's keys into the room. When the room writes a cipher's key in several places, the seed picks
    /// which of the places shown at this difficulty holds the real key; every other place gets a decoy, so the group
    /// has to find them all and work out which one reads right. The cipher notes every place (<see cref="EscapePuzzle.KeyAt"/>),
    /// so the validator can check the group can reach them all before they need them. Hints show the real key.
    /// </summary>
    private static EscapeRoom WithKeys(EscapeRoom room, Dictionary<string, List<string>> places, Dictionary<string, PuzzleGenerators.CipherMade> ciphers,
        long seed, EscapeDifficulty difficulty)
    {
        var shown = room.SceneObjects.ToDictionary(o => o.Id, o => RoomLengths.Shows(o, difficulty));
        bool Visible(string place) => !place.StartsWith("object:") || shown.GetValueOrDefault(place["object:".Length..], true);

        // (cipher, place) → the key written there.
        var written = new Dictionary<(string, string), string>();
        var candidates = new Dictionary<string, List<KeyCandidate>>();
        foreach (var (id, c) in ciphers)
        {
            var at = places.GetValueOrDefault(id) ?? [];
            if (at.Count == 0) continue;
            var visible = at.Where(Visible).ToList();
            var real = visible.Count == 0 ? at[0] : visible[new SeededRandom(seed ^ SeededRandom.StableHash(id + "#key")).Next(visible.Count)];
            var decoys = new Queue<PuzzleGenerators.DecoyKey>(c.Decoys);
            var list = new List<KeyCandidate>();
            foreach (var place in at)
            {
                if (place == real)
                {
                    written[(id, place)] = c.Key;
                    list.Add(new KeyCandidate(place, c.Key, true, c.Word, false));
                }
                else
                {
                    var d = decoys.Dequeue();
                    written[(id, place)] = d.Key;
                    list.Add(new KeyCandidate(place, d.Key, false, d.Decodes, d.FromDecoyWords));
                }
            }
            candidates[id] = list;
        }

        // An unknown puzzle id is left as it is: the validator reports it. Text outside any place (hints) gets the real key.
        string K(string text, string? place) => KeyPlaceholder().Replace(text, m =>
        {
            var id = m.Groups[1].Value;
            if (place is not null && written.TryGetValue((id, place), out var there)) return there;
            return ciphers.TryGetValue(id, out var c) ? c.Key : m.Value;
        });
        string? KN(string? text, string place) => text is null ? null : K(text, place);

        return room with
        {
            Puzzles = room.Puzzles.Select(p => p with
            {
                Prompt = K(p.Prompt, $"puzzle:{p.Id}"),
                Pieces = p.Pieces.Select(x => K(x, $"puzzle:{p.Id}")).ToList(),
                Hints = p.Hints.Select(x => K(x, null)).ToList(),
                SolvedText = K(p.SolvedText, null),
                KeyAt = ciphers.ContainsKey(p.Id) ? places.GetValueOrDefault(p.Id) ?? [] : p.KeyAt,
                Decoder = p.Decoder is { } d && candidates.TryGetValue(p.Id, out var list) ? d with { Candidates = list } : p.Decoder,
            }).ToList(),
            Stages = room.Stages.Select(s => s.Scene is null ? s : s with
            {
                Scene = s.Scene with
                {
                    Objects = s.Scene.Objects.Select(o => o with
                    {
                        Look = K(o.Look, $"object:{o.Id}"),
                        Clue = KN(o.Clue, $"object:{o.Id}"),
                        LockedText = KN(o.LockedText, $"object:{o.Id}"),
                    }).ToList(),
                },
            }).ToList(),
            Items = room.Items.Select(i => i with { Description = K(i.Description, $"item:{i.Id}"), Inspect = KN(i.Inspect, $"inspect:{i.Id}") }).ToList(),
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
