using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Tests;

/// <summary>
/// The bar every rebuilt shipped room clears (#85), on top of the validator (which AI-written rooms face
/// too): a scene to search in every stage, every kind of newer puzzle, enough puzzles for its length,
/// few one-tap "use" steps, more for Hard, and locks to find with a final lock to end on.
/// </summary>
public class ContentBarTests
{
    /// <summary>The rebuilt rooms: edition 2 or later. Every shipped room has been rebuilt (#85).</summary>
    public static TheoryData<string> RebuiltRooms() => new(Rooms.Library.Where(r => r.Edition >= 2).Select(r => r.Id));

    private static readonly Dictionary<int, int> MinPuzzles = new() { [30] = 7, [45] = 10, [60] = 13 };

    [Fact]
    public void Every_shipped_room_is_rebuilt() =>
        // Edition 4: locks to find and a final lock (#134, #143). The times before them don't compare.
        Assert.All(Rooms.Library, r => Assert.True(r.Edition >= 4, $"{r.Id} is on edition 4"));

    /// <summary>
    /// Locks to find, and a final lock (#143): every game, at every length and difficulty, has at least two locks the group
    /// has to find (one whose spot or puzzle the game leaves out doesn't count: it's in sight), and a final lock. That's
    /// usually the exit, though a story can put it earlier (the Black Notebook's true name stays its climax). The validator
    /// already proves each one can be found, and that a final lock has three parts or more.
    /// </summary>
    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Every_game_has_locks_to_find_and_a_final_lock(string id)
    {
        var room = Rooms.Get(id);
        foreach (var minutes in room.PlayableLengths)
            foreach (var difficulty in Enum.GetValues<EscapeDifficulty>())
            {
                var played = RoomLengths.Cut(RoomVariants.Build(room, 1, difficulty, cache: false), minutes, difficulty);
                var hidden = played.Puzzles.Count(p => p.RevealedBy is not null);
                Assert.True(hidden >= 2, $"{minutes} minutes on {difficulty}: {hidden} locks to find; aim for at least 2");
                Assert.True(played.Puzzles.Any(p => p.Final is not null), $"{minutes} minutes on {difficulty}: a final lock");
            }
    }

    /// <summary>
    /// A cipher that needs a key has one place to find it on Easy, two on Normal and three on Hard: one real, the rest
    /// decoys, so the group has to find them all and work out which reads right.
    /// </summary>
    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Keyed_ciphers_have_one_key_on_easy_two_on_normal_and_three_on_hard(string id)
    {
        var room = Rooms.Get(id);
        var longest = room.PlayableLengths.Max();
        foreach (var (difficulty, expected) in new[] { (EscapeDifficulty.Easy, 1), (EscapeDifficulty.Normal, 2), (EscapeDifficulty.Hard, 3) })
        {
            var played = RoomLengths.Cut(RoomVariants.Build(room, 1, difficulty), longest, difficulty);
            foreach (var p in played.Puzzles.Where(p => p.Decoder is { Type: CipherType.Shift or CipherType.Symbols or CipherType.Morse }))
                Assert.True(EscapeRoomValidator.VisibleKeyPlaces(played, p).Count == expected,
                    $"{p.Id} on {difficulty}: {EscapeRoomValidator.VisibleKeyPlaces(played, p).Count} keys to find; aim for {expected}");
        }
    }

    /// <summary>Real-word decoys need a key card: every room plays a symbols or Morse cipher on Normal.</summary>
    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Every_room_has_a_key_card_cipher_on_normal(string id)
    {
        var room = Rooms.Get(id);
        Assert.Contains(RoomLengths.Cut(room, room.PlayableLengths.Max(), EscapeDifficulty.Normal).Puzzles,
            p => p.Generator is { Type: GeneratorType.Cipher, Cipher: CipherType.Symbols or CipherType.Morse });
    }

    /// <summary>Every prop a room can name has a drawing on the screens (src/web/src/escape/props.tsx), and every drawing a name.</summary>
    [Fact]
    public void Every_prop_the_validator_allows_has_a_drawing()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "web"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "web", "src", "escape", "props.tsx"));
        var drawn = System.Text.RegularExpressions.Regex.Matches(source, @"^\s+(\w+): \{ emoji:", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(SceneProps.Known.Order(), drawn.Order());
    }

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

    /// <summary>
    /// Nor can it be a field name in what the screens are sent ("name", "notebook"): the privacy checks look for an answer
    /// as a whole word in the views, and they play one puzzle set, so a riddle variant only some sets pick slips past them.
    /// </summary>
    [Theory]
    [MemberData(nameof(RebuiltRooms))]
    public void Riddle_answers_are_not_field_names_in_the_views(string id)
    {
        var views = typeof(Engine.EscapeProjector).Assembly.GetTypes().Where(t => t.Namespace == typeof(Engine.EscapeProjector).Namespace && t.Name.EndsWith("View"));
        var fields = views.SelectMany(t => t.GetProperties()).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var answers = Rooms.Get(id).Puzzles.Where(p => p.Kind == PuzzleKind.Text && p.Generator is null)
            .SelectMany(p => p.Variants.SelectMany(v => v.Answers ?? []).Concat(p.Answers).Select(a => $"{p.Id}: {a}"));
        Assert.DoesNotContain(answers, a => fields.Contains(a[(a.IndexOf(": ") + 2)..]));
    }
}
