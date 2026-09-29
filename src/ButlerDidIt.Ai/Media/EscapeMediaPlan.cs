using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// The pictures an escape room needs: a cover for the lobby and the ending, and one per stage.
///
/// Like <see cref="MediaPlan"/> it's a pure function, so the same room always asks for the same
/// pictures, and MediaService's cache means each is only ever paid for once. The prompts use only
/// what the TV already shows: the room's title, synopsis and look, and each stage's title and
/// description. Never a puzzle, a clue piece or an answer, since a picture could give one away.
/// </summary>
public static class EscapeMediaPlan
{
    public static IReadOnlyList<MediaItem> For(EscapeRoom room)
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
