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

    [Fact]
    public void On_hard_a_decoy_is_teased_and_hints_only_nudge()
    {
        var template = Lab.Value;
        var s = EscapeEngine.NewGame(1, ai: new EscapeAiFeatures { GameMaster = true, Hints = true }, difficulty: EscapeDifficulty.Hard);
        s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, Ada, "Ada", true, false));
        s = EscapeEngine.Apply(s, template, new StartEscape(T0));
        s = EscapeEngine.Apply(s, template, new ExamineSpot(T0, Ada, "plant"));
        var cue = s.Cues[^1];
        Assert.Equal(CueKind.Decoy, cue.Kind);
        var line = EscapePrompts.Narration(template, s, cue, T0);
        Assert.Contains("MOMENT: Decoy", line);
        Assert.Contains("plant", line);

        var hint = EscapePrompts.Hint(template, s, "formula", 0, T0);
        Assert.Contains("a coded word", hint);
        Assert.Contains("\"cipherKeyFound\":false", hint);
        Assert.Contains("They chose Hard: only nudge.", hint);
    }

    [Fact]
    public void On_easy_an_empty_search_is_just_a_search_and_on_normal_it_costs_time_but_hints_still_say_plenty()
    {
        var template = Lab.Value;
        EscapeState Searched(EscapeDifficulty level)
        {
            var s = EscapeEngine.NewGame(1, ai: new EscapeAiFeatures { GameMaster = true, Hints = true }, difficulty: level);
            s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, Ada, "Ada", true, false));
            s = EscapeEngine.Apply(s, template, new StartEscape(T0));
            return EscapeEngine.Apply(s, template, new ExamineSpot(T0, Ada, "plant"));
        }
        Assert.DoesNotContain(Searched(EscapeDifficulty.Easy).Cues, c => c.Kind == CueKind.Decoy);

        // #132: on Normal too, a wasted search costs time, and the game master may tease the group for it.
        var normal = Searched(EscapeDifficulty.Normal);
        Assert.Contains(normal.Cues, c => c.Kind == CueKind.Decoy);
        Assert.DoesNotContain("only nudge", EscapePrompts.Hint(template, normal, "formula", 0, T0));
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

/// <summary>The shape an AI-written room must have (#86): every newer kind of puzzle, in scenes the server lays out.</summary>
public class EscapeGeneratorShapeTests
{
    private static readonly EscapeRoomRequest Request = new("a haunted lighthouse", ContentRating.Family, 30);

    private static EscapeRoom FakeRoom(Action<JsonObject>? change = null)
    {
        var json = new StreamReader(typeof(EscapeRoomGenerator).Assembly.GetManifestResourceStream("ButlerDidIt.Ai.Fake.FakeEscapeRoom.json")!).ReadToEnd();
        var node = JsonNode.Parse(json)!.AsObject();
        change?.Invoke(node);
        return GameJson.Deserialize<EscapeRoom>(node.ToJsonString());
    }

    private static JsonObject Puzzle(JsonObject room, string id) => room["puzzles"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == id)!.AsObject();

    [Fact]
    public void The_fake_room_has_the_whole_shape()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(), Request);
        Assert.Empty(EscapeRoomGenerator.ShapeErrors(room));
        Assert.Empty(EscapeRoomValidator.Validate(room));
    }

    [Fact]
    public void Scenes_recipes_closer_looks_and_hard_parts_are_kept_but_positions_and_built_parts_are_the_servers()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(n =>
        {
            // Positions the model made up (overlapping), and parts only a build or a hand-written room has.
            foreach (var spot in n["stages"]![0]!["scene"]!["objects"]!.AsArray()) { spot!["x"] = 5; spot["y"] = 5; spot["w"] = 900; spot["h"] = 500; }
            n["lengths"] = new JsonArray(30, 45);
            Puzzle(n, "lamp-lights")["grid"] = JsonNode.Parse("""{"size":3,"lit":[0]}""");
            Puzzle(n, "tide-note")["keyAt"] = new JsonArray("object:nowhere");
            Puzzle(n, "tide-note")["minMinutes"] = 45;
        }), Request);

        Assert.All(room.Stages, s => Assert.NotNull(s.Scene));
        Assert.NotEmpty(room.Recipes);
        Assert.Contains(room.Items, i => i.Inspect is not null);
        Assert.Contains(room.Puzzles, p => p.MinDifficulty == EscapeDifficulty.Hard);
        Assert.Contains(room.SceneObjects, o => o.MinDifficulty == EscapeDifficulty.Hard);
        Assert.Empty(room.Lengths);
        Assert.All(room.Puzzles, p => { Assert.Null(p.Grid); Assert.Empty(p.KeyAt); Assert.Null(p.MinMinutes); });
        // Laid out on a grid: inside the canvas, and no two spots overlap.
        foreach (var spots in room.Stages.Select(s => s.Scene!.Objects))
        {
            Assert.All(spots, o => Assert.True(o.X >= 0 && o.Y >= 0 && o.X + o.W <= 1000 && o.Y + o.H <= 600, $"{o.Id} is on the canvas"));
            for (var i = 0; i < spots.Count; i++)
                for (var j = i + 1; j < spots.Count; j++)
                    Assert.True(spots[i].X + spots[i].W <= spots[j].X || spots[j].X + spots[j].W <= spots[i].X || spots[i].Y + spots[i].H <= spots[j].Y || spots[j].Y + spots[j].H <= spots[i].Y);
        }
    }

    [Fact]
    public void Missing_variety_is_refused_with_a_reason_the_model_can_act_on()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(n =>
        {
            n["stages"]![0]!.AsObject().Remove("scene");
            foreach (var id in new[] { "tide-note", "gull-signal" }) Puzzle(n, id).Remove("generator");
            Puzzle(n, "bottle-shelf")["generator"] = JsonNode.Parse("""{"type":"digitFacts","count":3}""");
            Puzzle(n, "lamp-lights")["kind"] = "code";
            Puzzle(n, "lamp-lights")["generator"] = JsonNode.Parse("""{"type":"sequence"}""");
        }), Request);
        var errors = EscapeRoomGenerator.ShapeErrors(room);
        Assert.Contains(errors, e => e.Contains("Stage 'stairs' needs a \"scene\""));
        Assert.Contains(errors, e => e.Contains("\"cipher\" generator"));
        Assert.Contains(errors, e => e.Contains("logic puzzle"));
    }

    [Fact]
    public void Too_few_puzzles_for_the_clock_are_refused()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(), Request with { Minutes = 60 });
        Assert.Empty(EscapeRoomGenerator.ShapeErrors(room)); // 12 on Normal: enough for 60 minutes
        var shorter = EscapeRoomGenerator.Normalize(FakeRoom(n =>
        {
            // Two puzzles fewer: 10 on Normal.
            n["stages"]![2]!["puzzles"] = new JsonArray("buoys", "gull-signal", "lamp-panel", "lamp-door");
            foreach (var id in new[] { "tide-marks", "lamp-lights" })
                n["puzzles"]!.AsArray().Remove(n["puzzles"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == id));
        }), Request with { Minutes = 60 });
        Assert.Contains(EscapeRoomGenerator.ShapeErrors(shorter), e => e.Contains("60-minute room needs 11"));
    }

    [Fact]
    public void One_tap_steps_must_be_few_and_need_something_found()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(n =>
        {
            // The lamp panel becomes a one-tap step opened with a puzzle's reward: not something found.
            var panel = Puzzle(n, "lamp-panel");
            panel.Remove("generator");
            panel["kind"] = "use";
        }), Request);
        Assert.Contains(EscapeRoomGenerator.ShapeErrors(room), e => e.Contains("'lamp-panel' is a \"use\" step"));
    }

    [Fact]
    public void Family_rooms_keep_symbols_and_morse_for_hard()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(n => Puzzle(n, "gull-signal").Remove("minDifficulty")), Request);
        Assert.Contains(EscapeRoomGenerator.ShapeErrors(room), e => e.Contains("'gull-signal'") && e.Contains("Hard only"));
        var mature = EscapeRoomGenerator.Normalize(FakeRoom(n => Puzzle(n, "gull-signal").Remove("minDifficulty")), Request with { ContentRating = ContentRating.Mature });
        Assert.DoesNotContain(EscapeRoomGenerator.ShapeErrors(mature), e => e.Contains("'gull-signal'"));
    }

    [Fact]
    public void Answers_already_on_screen_are_refused()
    {
        var room = EscapeRoomGenerator.Normalize(FakeRoom(n =>
        {
            n["intro"] = n["intro"]!.GetValue<string>() + " Beware the walrus!";
            n["stages"]![1]!["scene"]!["objects"]![0]!["label"] = "map";
        }), Request);
        var errors = EscapeRoomGenerator.ShapeErrors(room);
        Assert.Contains(errors, e => e.Contains("'tide-note'") && e.Contains("\"walrus\""));
        Assert.Contains(errors, e => e.Contains("'map-riddle'") && e.Contains("\"map\""));
    }
}
