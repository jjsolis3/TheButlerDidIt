using System.Security.Cryptography;

namespace ButlerDidIt.Api.Parties;

public static class JoinCodes
{
    /// <summary>
    /// No 0/O, 1/I/L or 5/S: codes are read off a TV across a room and typed on
    /// phones, so look-alike characters cause failed joins.
    /// 28 characters ^ 6 positions is about 480 million codes.
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRTUVWXYZ234679";

    public const int Length = 6;

    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>Accepts "abc-123", " ABC123 " and so on.</summary>
    public static string Normalize(string input) =>
        new(input.ToUpperInvariant().Where(c => Alphabet.Contains(c)).ToArray());
}
