using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Turns a room and a seed into one concrete play of it: every puzzle's variant is picked
/// and every generator is run. The same room and seed always give exactly the same puzzles,
/// so a party can be saved and reloaded mid-game, and two groups on the same seed get a fair race.
///
/// Randomness comes from a tiny fixed algorithm (SplitMix64) rather than System.Random, whose
/// sequence isn't guaranteed to stay the same between .NET versions.
/// </summary>
public static class RoomVariants
{
    // Deterministic, so caching is safe: one concrete room per (room, seed).
    private static readonly ConditionalWeakTable<EscapeRoom, ConcurrentDictionary<long, EscapeRoom>> Cache = [];

    public static bool IsTemplated(EscapeRoom room) => room.Puzzles.Any(p => p.Variants.Count > 0 || p.Generator is not null);

    public static EscapeRoom Build(EscapeRoom room, long seed)
    {
        if (!IsTemplated(room)) return room;
        return Cache.GetOrCreateValue(room).GetOrAdd(seed, s => room with
        {
            Puzzles = room.Puzzles.Select(p => Concrete(p, new Rng(s ^ StableHash(p.Id)))).ToList(),
        });
    }

    private static EscapePuzzle Concrete(EscapePuzzle p, Rng rng)
    {
        if (p.Variants.Count == 0 && p.Generator is null) return p;

        var v = p.Variants.Count == 0 ? null : p.Variants[rng.Next(p.Variants.Count)];
        string prompt = v?.Prompt ?? p.Prompt, solved = v?.SolvedText ?? p.SolvedText;
        List<string> answers = v?.Answers ?? p.Answers, pieces = v?.Pieces ?? p.Pieces, hints = v?.Hints ?? p.Hints;

        if (p.Generator is { } g)
        {
            var made = Generate(g, rng);
            answers = [made.Answer];
            pieces = made.Pieces;
            prompt = Fill(prompt, made);
            solved = Fill(solved, made);
            hints = hints.Select(h => Fill(h, made)).ToList();
        }

        // The concrete puzzle is fixed: no variants or generator left to pick from.
        return p with { Prompt = prompt, Answers = answers, Pieces = pieces, Hints = hints, SolvedText = solved, Variants = [], Generator = null };
    }

    private sealed record Made(string Answer, List<string> Pieces, string Order, string Facts);

    private static Made Generate(PuzzleGenerator g, Rng rng)
    {
        switch (g.Type)
        {
            case GeneratorType.DigitFacts:
            {
                var facts = rng.Pick(FactBank.Facts, g.Count);
                return new Made(
                    string.Concat(facts.Select(f => f.Value)),
                    facts.Select((f, i) => g.PieceTemplate.Replace("{ordinal}", Ordinal(i)).Replace("{fact}", f.Text)).ToList(),
                    "",
                    string.Join(", ", facts.Select(f => f.Short)));
            }
            case GeneratorType.ColorDigits:
            {
                var colors = rng.Pick(g.Colors, g.Count);
                var digits = rng.Pick(Enumerable.Range(1, 9).ToList(), g.Count);
                // The pieces are dealt in a shuffled order, so the order on the sign is the puzzle.
                var dealt = rng.Pick(Enumerable.Range(0, g.Count).ToList(), g.Count);
                return new Made(
                    string.Concat(digits),
                    dealt.Select(i => g.PieceTemplate.Replace("{color}", colors[i].ToUpperInvariant()).Replace("{digit}", digits[i].ToString())).ToList(),
                    string.Join(", then ", colors.Select(c => $"{Capitalise(c)} duck")),
                    "");
            }
            case GeneratorType.WordSequence:
            {
                var words = rng.Pick(g.Words, g.Count);
                return new Made(
                    string.Join(" ", words),
                    words.Select((w, i) => g.PieceTemplate.Replace("{ordinal}", Ordinal(i)).Replace("{word}", w)).ToList(),
                    "",
                    "");
            }
            default:
                throw new InvalidOperationException($"Unknown generator {g.Type}.");
        }
    }

    private static string Fill(string text, Made m) =>
        text.Replace("{order}", m.Order).Replace("{facts}", m.Facts).Replace("{answer}", m.Answer.ToUpperInvariant());

    private static readonly string[] Ordinals = ["FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH", "EIGHTH", "NINTH", "TENTH"];
    private static string Ordinal(int i) => Ordinals[i];
    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>FNV-1a: the same number for the same id on every machine and every run (string.GetHashCode isn't).</summary>
    private static long StableHash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in text) { hash ^= ch; hash *= 1099511628211UL; }
        return (long)hash;
    }

    /// <summary>SplitMix64: small, fast, and the same sequence forever.</summary>
    private sealed class Rng(long seed)
    {
        private ulong _state = (ulong)seed;

        private ulong NextULong()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int Next(int maxExclusive) => (int)(NextULong() % (ulong)maxExclusive);

        /// <summary><paramref name="count"/> different items, in random order.</summary>
        public List<T> Pick<T>(IReadOnlyList<T> items, int count)
        {
            var pool = items.ToList();
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            return pool.Take(count).ToList();
        }
    }
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
