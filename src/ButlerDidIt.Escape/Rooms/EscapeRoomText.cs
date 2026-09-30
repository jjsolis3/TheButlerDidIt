using System.Text.RegularExpressions;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Checks on a room's wording that the validator leaves to the shipped-room content tests and the AI's
/// shape rules: things that are legal, but would put an answer on screen before anyone has worked it out.
/// </summary>
public static class EscapeRoomText
{
    /// <summary>The few words every prompt about the room carries (the clock, the score), wherever it's shown.</summary>
    public const string StandardWords = "minutes seconds time left hints used wrong answers stage puzzles solved";

    /// <summary>
    /// Cipher words already written somewhere in the room: a title, a description, a spot, an item, a hint,
    /// or the standard words. A cipher's word is its answer, so the answer would be on screen (and in the
    /// AI's prompts) before anything is decoded.
    /// </summary>
    public static List<(string PuzzleId, string Word)> CipherWordLeaks(EscapeRoom room)
    {
        var all = string.Join(" ", Texts(room));
        return room.Puzzles
            .Where(p => p.Generator?.Type == GeneratorType.Cipher)
            .SelectMany(p => p.Generator!.Words.Select(w => (p.Id, Word: w)))
            .Where(x => x.Word.Length > 0 && Regex.IsMatch(all, $@"(?<![\p{{L}}]){Regex.Escape(x.Word)}(?![\p{{L}}])", RegexOptions.IgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Riddle answers that are also a name the screens show on their own: a spot's label, prop or id, or an
    /// item's name or id. The privacy checks look for an answer as a whole quoted value in what the browsers
    /// get, and a spot drawn as a "clock" is exactly that.
    /// </summary>
    public static List<(string PuzzleId, string Answer)> AnswersOnScreen(EscapeRoom room)
    {
        var names = room.SceneObjects.SelectMany(o => new[] { o.Id, o.Label, o.Prop })
            .Concat(room.Items.SelectMany(i => new[] { i.Id, i.Name }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return room.Puzzles
            .Where(p => p.Kind == PuzzleKind.Text && p.Generator is null)
            .SelectMany(p => p.Answers.Select(a => (p.Id, Answer: a)))
            .Where(x => names.Contains(x.Answer))
            .ToList();
    }

    private static IEnumerable<string> Texts(EscapeRoom room)
    {
        var texts = new List<string> { room.Title, room.Synopsis, room.Intro, room.EscapedText, room.FailedText, room.Theme, room.ArtStyle, room.Host.Name, room.Host.Persona, StandardWords };
        foreach (var s in room.Stages) texts.AddRange([s.Id, s.Title, s.Description]);
        foreach (var o in room.SceneObjects) texts.AddRange(new[] { o.Id, o.Label, o.Prop, o.Look, o.Clue, o.LockedText }.OfType<string>());
        foreach (var i in room.Items) texts.AddRange(new[] { i.Id, i.Name, i.Description, i.Inspect }.OfType<string>());
        foreach (var r in room.Recipes) texts.Add(r.Text);
        foreach (var p in room.Puzzles)
        {
            texts.AddRange([p.Id, p.Title, p.Prompt, p.SolvedText, .. p.Hints, .. p.Pieces]);
            foreach (var v in p.Variants) texts.AddRange(new[] { v.Prompt, v.SolvedText }.OfType<string>().Concat(v.Hints ?? []).Concat(v.Pieces ?? []));
        }
        return texts;
    }
}
