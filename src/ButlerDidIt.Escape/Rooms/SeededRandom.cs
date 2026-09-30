namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// SplitMix64: small, fast, and the same sequence forever. Used instead of System.Random, whose
/// sequence isn't guaranteed to stay the same between .NET versions, because a saved game is
/// rebuilt from its seed every time it loads and must come out exactly the same.
/// </summary>
public sealed class SeededRandom(long seed)
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

    /// <summary>FNV-1a: the same number for the same text on every machine and every run (string.GetHashCode isn't).</summary>
    public static long StableHash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in text) { hash ^= ch; hash *= 1099511628211UL; }
        return (long)hash;
    }
}
