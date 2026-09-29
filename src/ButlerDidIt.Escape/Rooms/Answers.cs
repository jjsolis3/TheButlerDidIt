using System.Text;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>How a typed answer is compared with the accepted ones: forgiving about case, spacing, punctuation and a leading article.</summary>
public static class Answers
{
    private static readonly string[] Articles = ["a ", "an ", "the "];

    public static string Normalize(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        foreach (var article in Articles)
        {
            if (s.StartsWith(article, StringComparison.Ordinal)) { s = s[article.Length..]; break; }
        }
        var kept = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) kept.Append(ch);
        return kept.ToString();
    }

    public static bool Matches(EscapePuzzle puzzle, string attempt)
    {
        var given = Normalize(attempt);
        return given.Length > 0 && puzzle.Answers.Any(a => Normalize(a) == given);
    }
}
