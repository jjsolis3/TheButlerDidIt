using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Tests;

/// <summary>
/// The bar every rebuilt shipped room clears (#85), on top of the validator (which AI-written rooms face
/// too): a scene to search in every stage, every kind of newer puzzle, enough puzzles for its length,
/// few one-tap "use" steps, and more for Hard.
/// </summary>
public class ContentBarTests
{
    /// <summary>The rebuilt rooms: edition 2 or later. Every shipped room has been rebuilt (#85).</summary>
    public static TheoryData<string> RebuiltRooms() => new(Rooms.Library.Where(r => r.Edition >= 2).Select(r => r.Id));

    private static readonly Dictionary<int, int> MinPuzzles = new() { [30] = 7, [45] = 10, [60] = 13 };

    [Fact]
    public void Every_shipped_room_is_rebuilt() =>
        Assert.All(Rooms.Library, r => Assert.True(r.Edition >= 2, $"{r.Id} is rebuilt"));

    [Fact]
    public void Every_shipped_room_offers_every_length() =>
        Assert.All(Rooms.Library, r => Assert.Equal([30, 45, 60], r.PlayableLengths));

    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Every_stage_has_a_scene_with_decoys_and_hiding_places(string id)
    {
        foreach (var stage in Rooms.Get(id).Stages)
        {
            var spots = stage.Scene?.Objects ?? [];
            Assert.True(spots.Count >= 5, $"{stage.Id} has at least 5 spots");
            Assert.True(spots.Count(o => o.HidesPieces) >= 2, $"{stage.Id} has hiding places");
            Assert.Contains(spots, o => o is { Gives: null, Clue: null, HidesPieces: false }); // something to rule out
        }
    }

    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Every_length_is_a_full_game(string id)
    {
        var room = Rooms.Get(id);
        foreach (var minutes in room.PlayableLengths)
        {
            var count = RoomLengths.Cut(room, minutes).Puzzles.Count;
            Assert.True(count >= MinPuzzles[minutes], $"{minutes} minutes plays {count} puzzles; aim for at least {MinPuzzles[minutes]}");
        }
    }

    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Every_room_uses_each_newer_kind_of_puzzle(string id)
    {
        var room = Rooms.Get(id);
        Assert.Contains(room.Puzzles, p => p.Kind == PuzzleKind.Search);
        Assert.True(room.Items.Any(i => i.Inspect is not null) || room.Recipes.Count > 0, "something to look at closely or put together");
        Assert.Contains(room.Puzzles, p => p.Generator?.Type == GeneratorType.Cipher);
        Assert.Contains(room.Puzzles, p => p.Generator?.Type is GeneratorType.Deduction or GeneratorType.Switches);
    }

    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void One_tap_use_steps_are_few_and_each_needs_something_found(string id)
    {
        var room = Rooms.Get(id);
        var found = room.SceneObjects.Select(o => o.Gives).Concat(room.Items.Select(i => i.InspectGives)).Concat(room.Recipes.Select(r => r.Makes)).OfType<string>().ToHashSet();
        var uses = room.Puzzles.Where(p => p.Kind == PuzzleKind.Use).ToList();
        Assert.True(uses.Count <= 2, $"{uses.Count} use puzzles; keep it to 2");
        Assert.All(uses, p => Assert.True(p.Requires.Any(found.Contains), $"{p.Id} needs something found by searching, looking or combining"));
    }

    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Hard_adds_a_puzzle_and_spots(string id)
    {
        var room = Rooms.Get(id);
        Assert.Contains(room.Puzzles, p => p.MinDifficulty == EscapeDifficulty.Hard);
        Assert.Contains(room.SceneObjects, o => o.MinDifficulty == EscapeDifficulty.Hard);
    }

    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Spots_dont_sit_on_top_of_each_other(string id)
    {
        foreach (var stage in Rooms.Get(id).Stages)
        {
            var spots = stage.Scene!.Objects;
            for (var i = 0; i < spots.Count; i++)
                for (var j = i + 1; j < spots.Count; j++)
                {
                    var (a, b) = (spots[i], spots[j]);
                    var overlap = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
                    Assert.True(overlap * 4 <= Math.Min(a.W * a.H, b.W * b.H), $"{stage.Id}: '{a.Id}' and '{b.Id}' overlap too much");
                }
        }
    }

    /// <summary>
    /// A cipher's word is its answer, so it mustn't already be written in the room: in a title, a description, a spot,
    /// an item, a hint, or the few words every prompt carries ("minutes"). Otherwise the answer is on screen, or in
    /// the AI's prompt, before anyone decodes anything. AI-written rooms face the same check (EscapeRoomGenerator.ShapeErrors).
    /// </summary>
    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Cipher_words_are_nowhere_else_in_the_room(string id) =>
        Assert.Empty(EscapeRoomText.CipherWordLeaks(Rooms.Get(id)));

    /// <summary>A riddle's answer can't also be a spot's or item's name: the screens show those on their own.</summary>
    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Riddle_answers_are_not_names_on_screen(string id) =>
        Assert.Empty(EscapeRoomText.AnswersOnScreen(Rooms.Get(id)));
}
