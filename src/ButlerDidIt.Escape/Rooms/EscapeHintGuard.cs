using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Checks a hint written by the AI before anyone sees it. The AI is never told the answer, but it
/// sees the clue pieces and could work it out, so this is the second line of defence: a hint that
/// spells out an accepted answer is refused, and the room's written hint is shown instead.
///
/// It errs on the side of refusing. A false alarm costs nothing but a hand-written hint;
/// a missed leak spoils the puzzle.
/// </summary>
public static partial class EscapeHintGuard
{
    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// <summary>True if the hint must not be shown: empty, far too long, or giving an answer away.</summary>
    public static bool Rejects(string text, EscapePuzzle puzzle) =>
        string.IsNullOrWhiteSpace(text) || text.Trim().Length > Engine.EscapeEngine.MaxAiText || Leaks(text, puzzle);

    public static bool Leaks(string text, EscapePuzzle puzzle) =>
        puzzle.Kind == PuzzleKind.Code ? puzzle.Answers.Any(a => LeaksCode(text, a)) : puzzle.Answers.Any(a => LeaksWords(text, a));

    /// <summary>
    /// A code leaks if its digits appear in order in the hint, whatever sits between them ("4-7-2",
    /// "four, seven, two", "the 4th… then 7… then 2"). Short codes (one or two digits) would match
    /// almost any number, so for those only the number standing on its own counts.
    /// </summary>
    private static bool LeaksCode(string text, string answer)
    {
        var code = Answers.Normalize(answer);
        if (code.Length == 0) return false;
        var spelled = WithDigits(text);
        if (code.Length <= 2) return Regex.IsMatch(spelled, $@"(?<!\d){Regex.Escape(code)}(?!\d)");
        var digits = new string(spelled.Where(char.IsAsciiDigit).ToArray());
        return digits.Contains(code, StringComparison.Ordinal);
    }

    /// <summary>A word answer leaks if its words appear together in the hint (plurals and accents included), or glued into one word.</summary>
    private static bool LeaksWords(string text, string answer)
    {
        var target = Words(answer);
        if (target.Count > 0 && Answers.Normalize(target[0]) is "a" or "an" or "the") target.RemoveAt(0);
        if (target.Count == 0) return false;
        var words = Words(text);
        var glued = string.Concat(target);

        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] == glued || words[i] == glued + "s" || words[i] == glued + "es") return true;
            if (i + target.Count > words.Count) continue;
            var match = true;
            for (var j = 0; j < target.Count && match; j++)
            {
                var w = words[i + j];
                var t = target[j];
                match = w == t || (j == target.Count - 1 && (w == t + "s" || w == t + "es"));
            }
            if (match) return true;
        }
        return false;
    }

    private static string WithDigits(string text)
    {
        var s = Plain(text);
        for (var d = 0; d < DigitWords.Length; d++) s = Regex.Replace(s, $@"\b{DigitWords[d]}\b", d.ToString(CultureInfo.InvariantCulture));
        return s;
    }

    private static List<string> Words(string text) => WordPattern().Matches(Plain(text)).Select(m => m.Value).ToList();

    /// <summary>Lower case, without accents, so "Café" and "cafe" are the same word.</summary>
    private static string Plain(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
