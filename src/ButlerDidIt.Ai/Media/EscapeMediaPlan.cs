using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// The pictures and voice clips an escape room needs: a cover for the lobby and the ending, and one picture per
/// stage; and the game master reading the intro and each stage's description at its reveal (#127).
///
/// Like <see cref="MediaPlan"/> it's a pure function, so the same room always asks for the same
/// files, and MediaService's cache means each is only ever paid for once. The prompts use only
/// what the TV already shows: the room's title, synopsis and look, and each stage's title and
/// description. Never a puzzle, a clue piece or an answer, since a picture or a line could give one away.
/// </summary>
public static class EscapeMediaPlan
{
    public static IReadOnlyList<MediaItem> For(EscapeRoom room, bool voices = true, bool images = true)
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
        return items;
    }

    private static List<MediaItem> Pictures(EscapeRoom room)
    {
        var style = string.IsNullOrWhiteSpace(room.ArtStyle) ? "atmospheric, cinematic illustration" : room.ArtStyle;
        var tone = room.ContentRating == ButlerDidIt.Game.Scenarios.ContentRating.Family
            ? "Family friendly: spooky or silly, never gory."
            : "Tense and eerie, nothing graphic or gory.";
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
