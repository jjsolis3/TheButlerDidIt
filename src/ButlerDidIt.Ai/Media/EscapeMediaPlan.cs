using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// The pictures and voice clips an escape room needs: a cover for the lobby and the ending, and one picture per
/// stage; the game master reading the intro and each stage's description at its reveal (#127); and, with the
/// Filmmaker role, each stage's picture brought to life as a short clip for its reveal (#110).
///
/// Like <see cref="MediaPlan"/> it's a pure function, so the same room always asks for the same
/// files, and MediaService's cache means each is only ever paid for once. The prompts use only
/// what the TV already shows: the room's title, synopsis and look, and each stage's title and
/// description. Never a puzzle, a clue piece or an answer, since a picture or a line could give one away.
/// </summary>
public static class EscapeMediaPlan
{
    public static IReadOnlyList<MediaItem> For(EscapeRoom room, bool voices = true, bool images = true, bool films = false)
    {
        var items = new List<MediaItem>();
        if (images) items.AddRange(Pictures(room));
        if (voices)
        {
            // The same voice the game master speaks its live lines in (EscapeGameMaster).
            var voice = VoiceCasting.For($"game-master:{room.Id}", room.Host.Voice);
            if (!string.IsNullOrWhiteSpace(room.Intro)) items.Add(new MediaItem(EscapeArt.IntroVoice, AiRole.Voice, room.Intro, voice, default));
            foreach (var stage in room.Stages.Where(s => !string.IsNullOrWhiteSpace(s.Description)))
                items.Add(new MediaItem(EscapeArt.StageVoice(stage.Id), AiRole.Voice, stage.Description, voice, default));
        }
        // Last, so the pictures they start from are made first.
        if (films) items.AddRange(Clips(room));
        return items;
    }

    /// <summary>
    /// One clip per stage, from its picture (painted, or the host's own). The picture already shows the room, so the
    /// prompt only asks for slow, quiet movement, and keeps the room empty: no people, no text.
    /// </summary>
    private static IEnumerable<MediaItem> Clips(EscapeRoom room) => room.Stages.Select(stage =>
        new MediaItem(EscapeArt.StageFilm(stage.Id), AiRole.Filmmaker,
            $"Bring this still picture of an escape room to life: {stage.Title}. {stage.Description} " +
            $"A slow, steady camera push-in with subtle movement: flickering light and drifting dust. {Tone(room)} " +
            "The room stays empty: no people appear. No text, no letters, no numbers.",
            "", ImageShape.Landscape, From: EscapeArt.Stage(stage.Id)));

    private static string Tone(EscapeRoom room) => room.ContentRating == ButlerDidIt.Game.Scenarios.ContentRating.Family
        ? "Family friendly: spooky or silly, never gory."
        : "Tense and eerie, nothing graphic or gory.";

    private static List<MediaItem> Pictures(EscapeRoom room)
    {
        var style = string.IsNullOrWhiteSpace(room.ArtStyle) ? "atmospheric, cinematic illustration" : room.ArtStyle;
        var tone = Tone(room);
        const string rules = "Wide shot of an empty room, no people. No text, no letters, no numbers, no watermark.";

        var items = new List<MediaItem>
        {
            new(EscapeArt.Cover, AiRole.Illustrator,
                $"Key art for an escape room game called \"{room.Title}\". {room.Synopsis} Style: {style}. {tone} {rules}", "", ImageShape.Landscape),
        };
        foreach (var stage in room.Stages)
        {
            items.Add(new MediaItem(EscapeArt.Stage(stage.Id), AiRole.Illustrator,
                $"Inside an escape room game called \"{room.Title}\": {stage.Title}. {stage.Description} Style: {style}. {tone} {rules}", "", ImageShape.Landscape));
        }
        return items;
    }
}
