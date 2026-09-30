using System.Text.Json.Nodes;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Ai.Tests;

/// <summary>
/// Hints for the newer puzzles: the game master may see what the group has found, never what it hasn't.
/// The Laboratory (a test room with every kind of puzzle) is played solo, so clue pieces are hidden in the room.
/// </summary>
public class EscapeScenePromptTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ada = Guid.NewGuid();

    private static readonly Lazy<EscapeRoom> Lab = new(() =>
        GameJson.Deserialize<EscapeRoom>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "the-laboratory.json"))));

    [Theory]
    [InlineData(EscapeDifficulty.Easy)]
    [InlineData(EscapeDifficulty.Normal)]
    [InlineData(EscapeDifficulty.Hard)]
    public void Hint_prompts_hold_only_spots_searched_items_looked_at_and_pieces_found(EscapeDifficulty difficulty)
    {
        var template = Lab.Value;
        foreach (var seed in new long[] { 1, 2, 3, 4, 5 })
        {
            var s = EscapeEngine.NewGame(seed, ai: new EscapeAiFeatures { GameMaster = true, Hints = true }, difficulty: difficulty);
            s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, Ada, "Ada", true, false));
            s = EscapeEngine.Apply(s, template, new StartEscape(T0));
            var sawHidden = false;

            for (var step = 1; s.Phase == EscapePhase.Playing; step++)
            {
                Assert.True(step < 300);
                var now = T0.AddSeconds(step * 5);
                var room = EscapeEngine.RoomFor(s, template);
                var stage = room.Stages[s.StageIndex];
                var secrets = new List<string>();
                secrets.AddRange(room.SceneObjects.Where(o => !s.Examined.Contains(o.Id)).SelectMany(o => new[] { o.Look, o.Clue }.OfType<string>()));
                secrets.AddRange(room.Items.Where(i => i.Inspect is not null && !s.Inspected.Contains(i.Id)).Select(i => i.Inspect!));
                secrets.AddRange(s.Pieces.Where(p => p.IsHidden).Select(p => room.FindPuzzle(p.PuzzleId)!.Pieces[p.Index]));
                secrets.AddRange(room.Recipes.Where(r => !s.Notebook.Any(n => n.Text == r.Text)).Select(r => r.Text));
                sawHidden |= s.Pieces.Any(p => p.IsHidden);

                foreach (var puzzle in stage.Puzzles.Select(id => room.FindPuzzle(id)!).Where(p => !s.IsSolved(p.Id) && p.Hints.Count > 0))
                {
                    var prompt = EscapePrompts.Hint(template, s, puzzle.Id, 0, now);
                    foreach (var secret in secrets) Assert.DoesNotContain(secret, prompt);
                    if (puzzle.Kind is PuzzleKind.Code or PuzzleKind.Text && puzzle.Pieces.Count > 0 && s.Pieces.Any(p => p.PuzzleId == puzzle.Id && p.IsHidden))
                        Assert.Contains("\"cluePiecesStillHidden\":", prompt);
                }
                if (s.Cues.Count > 0) Assert.StartsWith("TASK: ", EscapePrompts.Narration(template, s, s.Cues[^1], now));
                s = EscapeEngine.Apply(s, template, Move(s, room, stage, now));
            }
            Assert.Equal(EscapePhase.Escaped, s.Phase);
            Assert.True(sawHidden, "a solo game hides clue pieces");
        }
    }

    [Fact]
    public void The_game_master_is_told_what_was_found_but_not_what_it_says()
    {
        var template = Lab.Value;
        var s = EscapeEngine.NewGame(1, ai: new EscapeAiFeatures { GameMaster = true });
        s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, Ada, "Ada", true, false));
        s = EscapeEngine.Apply(s, template, new StartEscape(T0));
        s = EscapeEngine.Apply(s, template, new ExamineSpot(T0, Ada, "crate"));
        var prompt = EscapePrompts.Narration(template, s, s.Cues[^1], T0);
        Assert.Contains("MOMENT: Found", prompt);
        Assert.Contains("crate", prompt);
    }

    private static EscapeCommand Move(EscapeState s, EscapeRoom room, EscapeStage stage, DateTimeOffset now)
    {
        if (stage.Scene?.Objects.FirstOrDefault(o => !s.Examined.Contains(o.Id) && (o.Requires is null || s.Inventory.Contains(o.Requires))) is { } spot)
            return new ExamineSpot(now, Ada, spot.Id);
        if (s.Inventory.Select(room.FindItem).FirstOrDefault(i => i!.Inspect is not null && !s.Inspected.Contains(i.Id) && (i.InspectRequires is null || s.Inventory.Contains(i.InspectRequires))) is { } item)
            return new InspectItem(now, Ada, item.Id);
        if (room.Recipes.FirstOrDefault(r => r.Items.All(s.Inventory.Contains)) is { } recipe)
            return new CombineItems(now, Ada, recipe.Items[0], recipe.Items[1]);
        var puzzle = stage.Puzzles.Select(id => room.FindPuzzle(id)!).First(p => !s.IsSolved(p.Id) && p.Kind != PuzzleKind.Search && p.Requires.All(s.Inventory.Contains));
        return puzzle.Kind switch
        {
            PuzzleKind.Use => new UseItems(now, Ada, puzzle.Id),
            PuzzleKind.Switches => new PressSwitch(now, Ada, puzzle.Id, FirstPress(puzzle.Grid!.Size, EscapeEngine.LitNow(s, puzzle))),
            _ => new SubmitAnswer(now, Ada, puzzle.Id, puzzle.Answers[0]),
        };
    }

    /// <summary>One press of a set that turns every light on, found by trying every set.</summary>
    private static int FirstPress(int size, IReadOnlyList<int> lit)
    {
        var cells = size * size;
        for (var set = 1; set < 1 << cells; set++)
        {
            IEnumerable<int> on = lit;
            for (var c = 0; c < cells; c++) if ((set >> c & 1) == 1) on = PuzzleGenerators.Press(size, on, c);
            if (on.Count() == cells) return Enumerable.Range(0, cells).First(c => (set >> c & 1) == 1);
        }
        throw new InvalidOperationException("unsolvable");
    }
}

/// <summary>Until the AI is taught the newer puzzles (#86), a room it writes keeps to the kinds it knows.</summary>
public class EscapeGeneratorShapeTests
{
    private static readonly EscapeRoomRequest Request = new("a haunted lighthouse", ContentRating.Family, 30);

    private static EscapeRoom FakeRoom(Action<JsonObject> change)
    {
        var json = new StreamReader(typeof(EscapeRoomGenerator).Assembly.GetManifestResourceStream("ButlerDidIt.Ai.Fake.FakeEscapeRoom.json")!).ReadToEnd();
        var node = JsonNode.Parse(json)!.AsObject();
        change(node);
        return GameJson.Deserialize<EscapeRoom>(node.ToJsonString());
    }

    [Fact]
    public void Scenes_recipes_and_closer_looks_are_stripped_from_an_ai_room()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(n =>
        {
            n["stages"]![0]!["scene"] = JsonNode.Parse("""{"objects":[{"id":"rug","prop":"rug","label":"rug","look":"Dust."}]}""");
            n["recipes"] = JsonNode.Parse("""[{"items":["a","b"],"makes":"c"}]""");
            n["items"]![0]!["inspect"] = "Tiny writing.";
            n["puzzles"]![0]!["minDifficulty"] = "hard";
        }), Request);
        Assert.All(room.Stages, s => Assert.Null(s.Scene));
        Assert.Empty(room.Recipes);
        Assert.All(room.Items, i => Assert.Null(i.Inspect));
        Assert.All(room.Puzzles, p => Assert.Null(p.MinDifficulty));
    }

    [Fact]
    public void Newer_puzzle_kinds_and_generators_are_refused_with_a_reason_the_model_can_act_on()
    {
        var room = FakeRoom(n =>
        {
            n["puzzles"]![0]!["kind"] = "switches";
            n["puzzles"]![1]!["generator"] = JsonNode.Parse("""{"type":"cipher","cipher":"shift","words":["echo"]}""");
        });
        var errors = EscapeRoomGenerator.ShapeErrors(room);
        Assert.Contains(errors, e => e.Contains("\"switches\"") && e.Contains("use code, text or use"));
        Assert.Contains(errors, e => e.Contains("cipher generator"));
    }
}
