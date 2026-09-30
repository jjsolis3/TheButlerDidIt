namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// The puzzle makers behind the newer generator types: ciphers, number patterns, logic puzzles and
/// light grids. Each one is built so it always has exactly one answer, and returns enough detail for
/// the tests to prove it (see PuzzleProofTests). <see cref="RoomVariants"/> turns the results into puzzle text.
/// </summary>
public static class PuzzleGenerators
{
    // ─── Ciphers ────────────────────────────────────────────────────────────────

    public sealed record CipherMade(string Word, string Encoded, string Key);

    private static readonly string[] Symbols =
        ["◆", "★", "●", "▲", "■", "♥", "♣", "♠", "☀", "☾", "✿", "☂", "♪", "⚓", "✦", "☘", "✚", "✖", "⚑", "✈", "☎", "♞", "☕", "⌛", "☯", "✂"];

    private static readonly string[] MorseCode =
        [".-", "-...", "-.-.", "-..", ".", "..-.", "--.", "....", "..", ".---", "-.-", ".-..", "--", "-.", "---", ".--.", "--.-", ".-.", "...", "-", "..-", "...-", ".--", "-..-", "-.--", "--.."];

    /// <summary>Written between the entries of a key table, so the table can be read back (by people and by <see cref="Decode"/>).</summary>
    private const string KeySeparator = "   ";

    public static CipherMade Cipher(CipherType type, IReadOnlyList<string> words, EscapeDifficulty difficulty, SeededRandom rng)
    {
        var pool = WordsFor(words, difficulty);
        var word = pool[rng.Next(pool.Count)].ToUpperInvariant();
        switch (type)
        {
            case CipherType.Shift:
            {
                var shift = 1 + rng.Next(25);
                return new(word, new string(word.Select(c => (char)('A' + (c - 'A' + shift) % 26)).ToArray()), shift.ToString());
            }
            case CipherType.Symbols:
            {
                var symbols = rng.Pick(Symbols, 26);
                var table = KeyLetters(word, rng).Select(l => $"{symbols[l - 'A']} = {l}");
                return new(word, string.Join(" ", word.Select(c => symbols[c - 'A'])), string.Join(KeySeparator, table));
            }
            case CipherType.Morse:
            {
                var table = KeyLetters(word, rng).Order().Select(l => $"{l} = {MorseCode[l - 'A']}");
                return new(word, string.Join(" ", word.Select(c => MorseCode[c - 'A'])), string.Join(KeySeparator, table));
            }
            case CipherType.Numbers:
                return new(word, string.Join("-", word.Select(c => c - 'A' + 1)), "A=1, B=2, C=3 … Z=26");
            case CipherType.Mirror:
                return new(word, new string(word.Select(c => (char)('Z' - (c - 'A'))).ToArray()), "A↔Z, B↔Y, C↔X … M↔N");
            default:
                throw new InvalidOperationException($"Unknown cipher {type}.");
        }
    }

    /// <summary>Reads a cipher back using only what players see: the coded text and the key as written. For the proofs.</summary>
    public static string Decode(CipherType type, string encoded, string key)
    {
        switch (type)
        {
            case CipherType.Shift:
                var shift = int.Parse(key);
                return new string(encoded.Select(c => (char)('A' + (c - 'A' - shift + 26) % 26)).ToArray());
            case CipherType.Symbols:
            {
                var table = key.Split(KeySeparator).Select(e => e.Split(" = ")).ToDictionary(e => e[0], e => e[1]);
                return string.Concat(encoded.Split(' ').Select(s => table[s]));
            }
            case CipherType.Morse:
            {
                var table = key.Split(KeySeparator).Select(e => e.Split(" = ")).ToDictionary(e => e[1], e => e[0]);
                return string.Concat(encoded.Split(' ').Select(s => table[s]));
            }
            case CipherType.Numbers:
                return new string(encoded.Split('-').Select(n => (char)('A' + int.Parse(n) - 1)).ToArray());
            case CipherType.Mirror:
                return new string(encoded.Select(c => (char)('Z' - (c - 'A'))).ToArray());
            default:
                throw new InvalidOperationException($"Unknown cipher {type}.");
        }
    }

    /// <summary>
    /// A symbols or Morse key as (code, letter) pairs, read back from the key as written, for the phone's key card.
    /// Other ciphers have no table: the shift amount is the puzzle, and numbers and mirror need none.
    /// </summary>
    public static List<(string Code, string Letter)> KeyTable(CipherType type, string key) => type switch
    {
        CipherType.Symbols => key.Split(KeySeparator).Select(e => e.Split(" = ")).Select(e => (e[0], e[1])).ToList(),
        CipherType.Morse => key.Split(KeySeparator).Select(e => e.Split(" = ")).Select(e => (e[1], e[0])).ToList(),
        _ => [],
    };

    /// <summary>The letters of the word plus two it doesn't use, shuffled, so the table doesn't simply spell the answer.</summary>
    private static List<char> KeyLetters(string word, SeededRandom rng)
    {
        var used = word.Distinct().ToList();
        var spare = rng.Pick(Enumerable.Range('A', 26).Select(c => (char)c).Where(c => !used.Contains(c)).ToList(), 2);
        return rng.Pick(used.Concat(spare).ToList(), used.Count + spare.Count);
    }

    /// <summary>Easy picks from the shorter half of the words, Hard from the longer half, Normal from all of them.</summary>
    public static List<string> WordsFor(IReadOnlyList<string> words, EscapeDifficulty difficulty)
    {
        var sorted = words.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).ToList();
        var half = (sorted.Count + 1) / 2;
        return difficulty switch
        {
            EscapeDifficulty.Easy => sorted.Take(half).ToList(),
            EscapeDifficulty.Hard => sorted.Skip(sorted.Count - half).ToList(),
            _ => sorted,
        };
    }

    // ─── Number patterns ────────────────────────────────────────────────────────

    public enum SequenceRule
    {
        /// <summary>The same step every time: 3, 7, 11, 15…</summary>
        Arithmetic,
        /// <summary>Multiplied by the same number: 2, 6, 18, 54…</summary>
        Geometric,
        /// <summary>Two steps taken in turn: 5, 8, 15, 18, 25…</summary>
        Alternating,
        /// <summary>The step grows by the same amount: 2, 5, 11, 20…</summary>
        Quadratic,
        /// <summary>Two patterns woven together: 1, 20, 4, 25, 7, 30…</summary>
        Interleaved,
        /// <summary>Each term is the two before it added: 2, 5, 7, 12…</summary>
        Fibonacci,
    }

    public sealed record SequenceMade(SequenceRule Rule, List<long> Shown, long Next)
    {
        public string Text => string.Join(", ", Shown) + ", ?";
    }

    /// <summary>
    /// A pattern whose next term is always a three-digit (or longer) code: short codes are too easy to guess
    /// by trying them all, and they turn up by chance in other numbers on screen ("38 minutes left").
    /// </summary>
    public static SequenceMade Sequence(EscapeDifficulty difficulty, SeededRandom rng)
    {
        var rule = difficulty switch
        {
            EscapeDifficulty.Easy => SequenceRule.Arithmetic,
            EscapeDifficulty.Normal => rng.Next(2) == 0 ? SequenceRule.Geometric : SequenceRule.Alternating,
            _ => (SequenceRule)((int)SequenceRule.Quadratic + rng.Next(3)),
        };
        var terms = new List<long>();
        int shown;
        switch (rule)
        {
            case SequenceRule.Arithmetic:
            {
                long a = 100 + rng.Next(100), d = 2 + rng.Next(8);
                for (var i = 0; i < 6; i++) terms.Add(a + d * i);
                shown = 5;
                break;
            }
            case SequenceRule.Geometric:
            {
                long a = 4 + rng.Next(6), r = 2 + rng.Next(2);
                for (var i = 0; i < 6; i++) terms.Add(i == 0 ? a : terms[^1] * r);
                shown = 5;
                break;
            }
            case SequenceRule.Alternating:
            {
                long a = 100 + rng.Next(100), p = 2 + rng.Next(8), q = 2 + rng.Next(8);
                if (q == p) q = p + 1;
                terms.Add(a);
                for (var i = 1; i < 7; i++) terms.Add(terms[^1] + (i % 2 == 1 ? p : q));
                shown = 6;
                break;
            }
            case SequenceRule.Quadratic:
            {
                long a = 100 + rng.Next(100), b = 1 + rng.Next(5), c = 2 + rng.Next(3);
                for (long i = 0; i < 6; i++) terms.Add(a + b * i + c * i * (i - 1) / 2);
                shown = 5;
                break;
            }
            case SequenceRule.Interleaved:
            {
                long a1 = 1 + rng.Next(9), d1 = 2 + rng.Next(6), a2 = 100 + rng.Next(100), d2 = 2 + rng.Next(6);
                if (d2 == d1) d2 = d1 + 1;
                for (var i = 0; i < 8; i++) terms.Add(i % 2 == 0 ? a1 + d1 * (i / 2) : a2 + d2 * (i / 2));
                shown = 7;
                break;
            }
            default:
            {
                long x = 10 + rng.Next(10), y = x + 1 + rng.Next(10);
                terms.AddRange([x, y]);
                for (var i = 2; i < 7; i++) terms.Add(terms[^1] + terms[^2]);
                shown = 6;
                break;
            }
        }
        return new(rule, terms.Take(shown).ToList(), terms[shown]);
    }

    // ─── Logic puzzles ──────────────────────────────────────────────────────────

    public enum ClueForm { LeftOf, NextTo, NotNextTo, NotAt, AtEnd, At }

    /// <summary>A fact about the row. <see cref="A"/> and <see cref="B"/> are item indexes; <see cref="Spot"/> is 0-based.</summary>
    public sealed record DeductionClue(ClueForm Form, int A, int B, int Spot);

    /// <param name="Items">The things, in the order the prompt lists them (and the code reads them).</param>
    /// <param name="PositionOf">Each item's spot, 0-based from the left.</param>
    public sealed record DeductionMade(List<string> Items, int[] PositionOf, List<DeductionClue> Clues, List<string> ClueTexts)
    {
        /// <summary>Each item's spot (1 = far left), in the listed order.</summary>
        public string Answer => string.Concat(PositionOf.Select(p => p + 1));
    }

    public static int DeductionSize(EscapeDifficulty difficulty) => difficulty switch
    {
        EscapeDifficulty.Easy => 3,
        EscapeDifficulty.Normal => 4,
        _ => 5,
    };

    public static DeductionMade Deduction(IReadOnlyList<string> words, EscapeDifficulty difficulty, SeededRandom rng)
    {
        var n = Math.Min(DeductionSize(difficulty), words.Count);
        var items = rng.Pick(words, n);
        var pos = rng.Pick(Enumerable.Range(0, n).ToList(), n).ToArray();

        // Every true fact, the gentle ones shuffled first; a plain "X is in spot 2" only if nothing else will do.
        var indirect = new List<DeductionClue>();
        var direct = new List<DeductionClue>();
        for (var a = 0; a < n; a++)
        {
            for (var b = 0; b < n; b++)
            {
                if (a == b) continue;
                if (pos[a] < pos[b]) indirect.Add(new(ClueForm.LeftOf, a, b, 0));
                if (a < b && Math.Abs(pos[a] - pos[b]) == 1) indirect.Add(new(ClueForm.NextTo, a, b, 0));
                if (a < b && Math.Abs(pos[a] - pos[b]) > 1) indirect.Add(new(ClueForm.NotNextTo, a, b, 0));
            }
            for (var k = 0; k < n; k++)
                if (pos[a] != k) indirect.Add(new(ClueForm.NotAt, a, 0, k));
            if (pos[a] == 0 || pos[a] == n - 1) indirect.Add(new(ClueForm.AtEnd, a, 0, 0));
            direct.Add(new(ClueForm.At, a, 0, pos[a]));
        }
        // A few tries, keeping the shortest set of clues: fewer, sharper pieces to share out.
        List<DeductionClue>? best = null;
        for (var attempt = 0; attempt < DeductionAttempts; attempt++)
        {
            var clues = Clues(n, rng.Pick(indirect, indirect.Count).Concat(rng.Pick(direct, direct.Count)));
            if (best is null || clues.Count < best.Count) best = clues;
        }
        return new(items, pos, best!, best!.Select(c => ClueText(c, items)).ToList());
    }

    private const int DeductionAttempts = 6;

    /// <summary>Adds clues in the given order until only one arrangement fits, then drops any the others make unnecessary.</summary>
    private static List<DeductionClue> Clues(int n, IEnumerable<DeductionClue> candidates)
    {
        var clues = new List<DeductionClue>();
        foreach (var clue in candidates)
        {
            clues.Add(clue);
            if (CountSolutions(n, clues) == 1) break;
        }
        // So every piece matters.
        for (var i = 0; i < clues.Count;)
        {
            var without = clues.Where((_, j) => j != i).ToList();
            if (CountSolutions(n, without) == 1) clues = without;
            else i++;
        }
        return clues;
    }

    public static bool Holds(DeductionClue c, IReadOnlyList<int> pos) => c.Form switch
    {
        ClueForm.LeftOf => pos[c.A] < pos[c.B],
        ClueForm.NextTo => Math.Abs(pos[c.A] - pos[c.B]) == 1,
        ClueForm.NotNextTo => Math.Abs(pos[c.A] - pos[c.B]) > 1,
        ClueForm.NotAt => pos[c.A] != c.Spot,
        ClueForm.AtEnd => pos[c.A] == 0 || pos[c.A] == pos.Count - 1,
        _ => pos[c.A] == c.Spot,
    };

    /// <summary>How many arrangements of <paramref name="n"/> things fit every clue, by trying them all (at most 5! = 120).</summary>
    public static int CountSolutions(int n, IReadOnlyList<DeductionClue> clues) =>
        Permutations(n).Count(pos => clues.All(c => Holds(c, pos)));

    private static IEnumerable<int[]> Permutations(int n)
    {
        var pos = new int[n];
        var used = new bool[n];
        return Fill(0);

        IEnumerable<int[]> Fill(int i)
        {
            if (i == n) { yield return (int[])pos.Clone(); yield break; }
            for (var k = 0; k < n; k++)
            {
                if (used[k]) continue;
                used[k] = true;
                pos[i] = k;
                foreach (var p in Fill(i + 1)) yield return p;
                used[k] = false;
            }
        }
    }

    private static string ClueText(DeductionClue c, IReadOnlyList<string> items)
    {
        string a = items[c.A], b = items[c.B];
        return c.Form switch
        {
            ClueForm.LeftOf => $"The {a} is somewhere to the left of the {b}.",
            ClueForm.NextTo => $"The {a} is right next to the {b}.",
            ClueForm.NotNextTo => $"The {a} is not next to the {b}.",
            ClueForm.NotAt => $"The {a} is not in spot {c.Spot + 1}.",
            ClueForm.AtEnd => $"The {a} is at one of the two ends.",
            _ => $"The {a} is in spot {c.Spot + 1}.",
        };
    }

    /// <summary>"RED, BLUE and GREEN": how {items} lists the things, in the order the code reads them.</summary>
    public static string ListItems(IReadOnlyList<string> items)
    {
        var upper = items.Select(i => i.ToUpperInvariant()).ToList();
        return upper.Count == 1 ? upper[0] : string.Join(", ", upper.Take(upper.Count - 1)) + " and " + upper[^1];
    }

    // ─── Light grids ────────────────────────────────────────────────────────────

    /// <param name="Presses">Pressing these (in any order) turns every light on: the proof it can be solved.</param>
    public sealed record SwitchesMade(SwitchGrid Grid, List<int> Presses);

    public static (int Size, int Presses) SwitchesSize(EscapeDifficulty difficulty) => difficulty switch
    {
        EscapeDifficulty.Easy => (3, 2),
        EscapeDifficulty.Normal => (3, 4),
        _ => (4, 6),
    };

    /// <summary>
    /// Starts with every light on and presses a few at random. Each press undoes itself, so pressing
    /// the same ones again turns every light back on: the grid can always be solved.
    /// </summary>
    public static SwitchesMade Switches(EscapeDifficulty difficulty, SeededRandom rng)
    {
        var (size, count) = SwitchesSize(difficulty);
        var cells = Enumerable.Range(0, size * size).ToList();
        while (true)
        {
            var presses = rng.Pick(cells, count).Order().ToList();
            IReadOnlyCollection<int> lit = cells;
            foreach (var p in presses) lit = Press(size, lit, p);
            // Some sets of presses on a 4×4 grid cancel out: pick again rather than start solved.
            if (lit.Count < cells.Count) return new(new SwitchGrid(size, lit.Order().ToList()), presses);
        }
    }

    /// <summary>The lights after pressing <paramref name="cell"/>: it and the ones above, below, left and right flip.</summary>
    public static List<int> Press(int size, IEnumerable<int> lit, int cell)
    {
        var on = lit.ToHashSet();
        int row = cell / size, col = cell % size;
        foreach (var (r, c) in new[] { (row, col), (row - 1, col), (row + 1, col), (row, col - 1), (row, col + 1) })
        {
            if (r < 0 || r >= size || c < 0 || c >= size) continue;
            var i = r * size + c;
            if (!on.Remove(i)) on.Add(i);
        }
        return on.Order().ToList();
    }

    /// <summary>A1-style name for a cell, for hints: row letters, column numbers.</summary>
    public static string CellName(int size, int cell) => $"{(char)('A' + cell / size)}{cell % size + 1}";
}
