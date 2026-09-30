using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Escape.Tests;

/// <summary>
/// The Laboratory: a test-only room (Fixtures/the-laboratory.json) using every newer kind of puzzle.
/// Spots to search (with decoys, a tool-locked poster and hiding places), items to look at and put
/// together, a shift cipher and a Hard-only Morse cipher whose keys must be found, a number pattern,
/// a logic puzzle and a light panel.
/// </summary>
public static class Lab
{
    private static readonly Lazy<EscapeRoom> Loaded = new(() =>
        GameJson.Deserialize<EscapeRoom>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "the-laboratory.json"))));

    public static EscapeRoom Room => Loaded.Value;

    public static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    public static readonly Guid Ada = Guid.NewGuid(), Ben = Guid.NewGuid(), Cy = Guid.NewGuid();

    public static EscapeState Started(EscapeDifficulty? difficulty = null, long seed = 7, int? minutes = null, params Guid[] seats)
    {
        if (seats.Length == 0) seats = [Ada];
        var s = EscapeEngine.NewGame(seed, minutes: minutes, difficulty: difficulty);
        for (var i = 0; i < seats.Length; i++) s = EscapeEngine.Apply(s, Room, new AddEscapePlayer(T0, seats[i], $"P{i}", i == 0, false));
        return EscapeEngine.Apply(s, Room, new StartEscape(T0));
    }

    public static EscapeState Do(this EscapeState s, EscapeCommand c) => EscapeEngine.Apply(s, Room, c);

    /// <summary>Plays the game to the end with the shared <see cref="EscapeBot"/>. <paramref name="check"/> runs before every move.</summary>
    public static EscapeState PlayToEnd(EscapeState s, EscapeRoom template, Guid[] seats, Action<EscapeState>? check = null) =>
        EscapeBot.PlayToEnd(s, template, seats, T0, check);

    /// <summary>A solo game played (as <see cref="PlayToEnd"/> would) until stage <paramref name="stage"/> (0-based) opens.</summary>
    public static EscapeState PlayToStage(int stage, long seed = 7, EscapeDifficulty? difficulty = null)
    {
        var s = Started(difficulty, seed);
        var t = T0;
        while (s.StageIndex < stage)
        {
            t = t.AddSeconds(5);
            s = s.Do(EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, Room), Ada, t));
        }
        return s;
    }

    public static List<int> SolveLights(int size, IEnumerable<int> lit) => EscapeBot.SolveLights(size, lit);
}

public class LaboratoryTests
{
    private static EscapeRoom Room => Lab.Room;
    private static readonly DateTimeOffset T0 = Lab.T0;
    private static readonly Guid Ada = Lab.Ada, Ben = Lab.Ben, Cy = Lab.Cy;

    [Fact]
    public void The_laboratory_is_valid_at_every_length_and_difficulty() => Assert.Empty(EscapeRoomValidator.Validate(Room));

    public static TheoryData<EscapeDifficulty, int, int> Games()
    {
        var data = new TheoryData<EscapeDifficulty, int, int>();
        foreach (var d in Enum.GetValues<EscapeDifficulty>())
            foreach (var minutes in new[] { 30, 45 })
                foreach (var players in new[] { 1, 3 })
                    data.Add(d, minutes, players);
        return data;
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void A_thorough_group_escapes_at_every_difficulty_length_and_size(EscapeDifficulty difficulty, int minutes, int players)
    {
        var seats = new[] { Ada, Ben, Cy }.Take(players).ToArray();
        foreach (var seed in new long[] { 1, 2, 3, 99, 20261031 })
        {
            var s = Lab.PlayToEnd(Lab.Started(difficulty, seed, minutes, seats), Room, seats);
            Assert.Equal(EscapePhase.Escaped, s.Phase);
            Assert.Equal(EscapeEngine.RoomFor(s, Room).Puzzles.Count, s.Solved.Count);
            Assert.DoesNotContain(s.Pieces, p => p.IsHidden); // a thorough search finds every hidden piece
        }
    }

    [Fact]
    public void Searching_finds_items_a_tool_reveals_more_and_a_search_puzzle_solves_itself()
    {
        var s = Lab.Started();
        s = s.Do(new ExamineSpot(T0, Ada, "crate"));
        Assert.Contains("bulb", s.Inventory);
        Assert.Contains("found a glass bulb", s.Feed[^1].Text);

        var ex = Assert.Throws<GameRuleException>(() => s.Do(new ExamineSpot(T0, Ada, "poster")));
        Assert.Contains("different light", ex.Message); // the room's own words, not "you need the UV lamp"
        Assert.Throws<GameRuleException>(() => s.Do(new ExamineSpot(T0, Ada, "crate"))); // once is enough
        Assert.Throws<GameRuleException>(() => s.Do(new ExamineSpot(T0, Ada, "window"))); // the next stage's

        s = s.Do(new ExamineSpot(T0, Ada, "drawer"));
        s = s.Do(new CombineItems(T0, Ada, "lamp-body", "bulb"));
        Assert.Equal(["uv-lamp"], s.Inventory);
        Assert.Contains(s.Notebook, n => n.Text.Contains("glows violet"));

        Assert.False(s.IsSolved("gather"));
        s = s.Do(new ExamineSpot(T0, Ada, "poster"));
        Assert.True(s.IsSolved("gather"));
        Assert.Contains("cabinet-key", s.Inventory);
        Assert.Contains("uv-lamp", s.Inventory); // a tool isn't used up
        Assert.Contains(s.Notebook, n => n.Source == "Poster" && n.Text.Contains("LOOKS TWICE"));
    }

    [Fact]
    public void Items_that_dont_fit_together_are_just_logged()
    {
        var s = Lab.Started().Do(new ExamineSpot(T0, Ada, "crate"));
        Assert.Throws<GameRuleException>(() => s.Do(new CombineItems(T0, Ada, "bulb", "lamp-body"))); // not holding both
        Assert.Throws<GameRuleException>(() => s.Do(new CombineItems(T0, Ada, "bulb", "bulb")));

        var t = Lab.Started().Do(new ExamineSpot(T0, Ada, "crate")).Do(new ExamineSpot(T0, Ada, "drawer"));
        t = t.Do(new CombineItems(T0, Ada, "bulb", "lamp-body"));
        t = t.Do(new ExamineSpot(T0, Ada, "poster")); // gives the small key
        var before = t.Inventory.ToList();
        t = t.Do(new CombineItems(T0, Ada, "uv-lamp", "cabinet-key"));
        Assert.Equal(before, t.Inventory);
        Assert.Contains("don't fit together", t.Feed[^1].Text);
    }

    [Fact]
    public void A_closer_look_can_find_another_item_and_can_need_a_tool()
    {
        var s = Lab.Started(seed: 5);
        var room = EscapeEngine.RoomFor(s, Room);
        foreach (var spot in new[] { "crate", "drawer" }) s = s.Do(new ExamineSpot(T0, Ada, spot));
        s = s.Do(new CombineItems(T0, Ada, "bulb", "lamp-body")).Do(new ExamineSpot(T0, Ada, "poster"));
        s = s.Do(new SubmitAnswer(T0, Ada, "sequence", room.FindPuzzle("sequence")!.Answers[0]));
        Assert.Contains("locket", s.Inventory);

        var view = EscapeProjector.Stage(s, Room, T0).Inventory.Single(i => i.Id == "locket");
        Assert.True(view.Inspectable);
        Assert.Null(view.InspectText);

        s = s.Do(new InspectItem(T0, Ada, "locket"));
        Assert.Contains("photo", s.Inventory);
        view = EscapeProjector.Stage(s, Room, T0).Inventory.Single(i => i.Id == "locket");
        Assert.False(view.Inspectable);
        Assert.Contains("tucked behind", view.InspectText);
        Assert.Throws<GameRuleException>(() => s.Do(new InspectItem(T0, Ada, "locket")));
        Assert.Throws<GameRuleException>(() => s.Do(new InspectItem(T0, Ada, "manual"))); // not held
    }

    [Fact]
    public void Decoys_cost_time_only_on_hard_and_a_spot_holding_a_key_is_no_decoy()
    {
        var normal = Lab.Started(EscapeDifficulty.Normal);
        Assert.Equal(normal.Deadline, normal.Do(new ExamineSpot(T0, Ada, "plant")).Deadline);

        var hard = Lab.Started(EscapeDifficulty.Hard);
        var searched = hard.Do(new ExamineSpot(T0, Ada, "plant"));
        Assert.Equal(hard.Deadline!.Value.AddSeconds(-EscapeEngine.DecoyPenaltySeconds), searched.Deadline);
        Assert.Contains("Nothing there", searched.Feed[^1].Text);
        Assert.Equal(hard.Deadline, hard.Do(new ExamineSpot(T0, Ada, "painting")).Deadline); // the cipher's key is written on it
    }

    [Fact]
    public void Lights_flip_with_their_neighbours_and_all_on_solves_the_panel()
    {
        var s = Lab.PlayToStage(1);
        var room = EscapeEngine.RoomFor(s, Room);
        var panel = room.FindPuzzle("lights")!;
        Assert.Throws<GameRuleException>(() => s.Do(new SubmitAnswer(T0, Ada, "lights", "1234")));
        Assert.Throws<GameRuleException>(() => s.Do(new PressSwitch(T0, Ada, "lights", 99)));

        var presses = Lab.SolveLights(panel.Grid!.Size, panel.Grid.Lit);
        foreach (var cell in presses.SkipLast(1)) s = s.Do(new PressSwitch(T0, Ada, "lights", cell));
        Assert.False(s.IsSolved("lights"));
        var view = EscapeProjector.Stage(s, Room, T0).Puzzles.Single(p => p.Id == "lights").Switches!;
        Assert.Equal(EscapeEngine.LitNow(s, panel), view.Lit);
        s = s.Do(new PressSwitch(T0, Ada, "lights", presses[^1]));
        Assert.True(s.IsSolved("lights"));
    }

    [Fact]
    public void A_solo_players_extra_clue_pieces_are_hidden_in_the_room_and_found_by_searching()
    {
        var s = Lab.PlayToStage(1, seed: 11);
        var room = EscapeEngine.RoomFor(s, Room);
        var jars = room.FindPuzzle("jars")!;
        var held = s.Pieces.Where(p => p.PuzzleId == "jars").ToList();
        Assert.Single(held, p => p.SeatId == Ada); // one on the phone…
        Assert.Equal(jars.Pieces.Count - 1, held.Count(p => p.IsHidden)); // …the rest hidden, one per spot
        Assert.Equal(held.Count(p => p.IsHidden), held.Where(p => p.IsHidden).Select(p => p.SpotId).Distinct().Count());

        var phone = EscapeProjector.Player(s, Room, Ada, T0);
        Assert.Single(phone.Pieces, p => p.PuzzleId == "jars");
        Assert.Equal(jars.Pieces.Count - 1, phone.Stage.Puzzles.Single(p => p.Id == "jars").PiecesHidden);

        var hidden = held.First(p => p.IsHidden);
        s = s.Do(new ExamineSpot(T0, Ada, hidden.SpotId!));
        phone = EscapeProjector.Player(s, Room, Ada, T0);
        var found = phone.Pieces.Single(p => p.Text == jars.Pieces[hidden.Index]);
        Assert.Equal(room.FindObject(hidden.SpotId!)!.Label, found.FoundIn);
        Assert.Contains("found a clue piece", s.Feed[^1].Text);
    }

    [Fact]
    public void With_three_phones_only_pieces_beyond_the_third_are_hidden_and_a_leaver_leaves_them_hidden()
    {
        var s = Lab.Started(EscapeDifficulty.Hard, 11, null, Ada, Ben, Cy);
        var room = EscapeEngine.RoomFor(s, Room);
        foreach (var puzzle in room.Puzzles.Where(p => p.Pieces.Count > 0))
        {
            var pieces = s.Pieces.Where(p => p.PuzzleId == puzzle.Id).ToList();
            Assert.Equal(Math.Min(3, puzzle.Pieces.Count), pieces.Where(p => !p.IsHidden).Select(p => p.SeatId).Distinct().Count());
            Assert.Equal(Math.Max(0, puzzle.Pieces.Count - 3), pieces.Count(p => p.IsHidden));
        }
        var hidden = s.Pieces.Count(p => p.IsHidden);
        s = s.Do(new RemoveEscapePlayer(T0, Cy));
        Assert.Equal(hidden, s.Pieces.Count(p => p.IsHidden));
        Assert.DoesNotContain(s.Pieces, p => p.SeatId == Cy);
    }

    [Fact]
    public void Rooms_without_hiding_spots_deal_every_piece_to_a_phone_as_before()
    {
        var workshop = Rooms.Get("the-workshop");
        var s = EscapeEngine.NewGame(3);
        s = EscapeEngine.Apply(s, workshop, new AddEscapePlayer(T0, Ada, "Ada", true, false));
        s = EscapeEngine.Apply(s, workshop, new StartEscape(T0));
        Assert.All(s.Pieces, p => Assert.Equal(Ada, p.SeatId));
        Assert.Null(EscapeProjector.Stage(s, workshop, T0).Scene);
    }

    [Fact]
    public void Difficulty_changes_sizes_hints_penalties_and_extra_puzzles()
    {
        EscapeRoom At(EscapeDifficulty d, int minutes = 45) => EscapeEngine.RoomFor(EscapeEngine.NewGame(4, minutes: minutes, difficulty: d), Room);
        var (easy, normal, hard) = (At(EscapeDifficulty.Easy), At(EscapeDifficulty.Normal), At(EscapeDifficulty.Hard));

        Assert.Equal([2, 3, 4], new[] { easy, normal, hard }.Select(r => r.FindPuzzle("digits")!.Pieces.Count));
        Assert.Equal([3, 4, 5], new[] { easy, normal, hard }.Select(r => r.FindPuzzle("jars")!.Answers[0].Length));
        Assert.Equal([3, 3, 4], new[] { easy, normal, hard }.Select(r => r.FindPuzzle("lights")!.Grid!.Size));
        Assert.Equal([60, 120, 120], new[] { easy, normal, hard }.Select(r => r.HintPenaltySeconds));
        Assert.Equal(3, normal.FindPuzzle("formula")!.Hints.Count);
        Assert.Equal(2, hard.FindPuzzle("formula")!.Hints.Count); // the give-away last hint is gone
        Assert.Single(hard.FindPuzzle("door")!.Hints); // a ladder of one keeps its only step
        Assert.Null(normal.FindPuzzle("signal"));
        Assert.NotNull(hard.FindPuzzle("signal")); // Hard's extra puzzle
        Assert.Null(At(EscapeDifficulty.Hard, 30).FindPuzzle("digits")); // lengths still cut

        var words = new[] { "atom", "flask", "spark", "magnet", "crystal", "science" };
        var easyWords = Enumerable.Range(0, 50).Select(seed => RoomVariants.Build(Room, seed, EscapeDifficulty.Easy).FindPuzzle("formula")!.Answers[0]).Distinct().ToList();
        var hardWords = Enumerable.Range(0, 50).Select(seed => RoomVariants.Build(Room, seed, EscapeDifficulty.Hard).FindPuzzle("formula")!.Answers[0]).Distinct().ToList();
        Assert.True(easyWords.Max(w => w.Length) <= hardWords.Min(w => w.Length), "Easy ciphers hide shorter words than Hard ones");
        Assert.All(easyWords.Concat(hardWords), w => Assert.Contains(w.ToLowerInvariant(), words));
    }

    [Fact]
    public void Normal_is_the_room_as_written()
    {
        foreach (var room in Rooms.Library)
        {
            Assert.Same(RoomVariants.Build(room, 42), RoomVariants.Build(room, 42, EscapeDifficulty.Normal));
            var s = EscapeEngine.NewGame(42);
            Assert.Equal(EscapeDifficulty.Normal, EscapeProjector.Stage(s, room, T0).Difficulty);
            Assert.Equal(room.HintPenaltySeconds, EscapeEngine.RoomFor(s, room).HintPenaltySeconds);
        }
    }

    [Fact]
    public void A_party_saved_before_difficulties_loads_as_normal()
    {
        var json = GameJson.Serialize(EscapeEngine.NewGame(5)).Replace("\"difficulty\":null,", "");
        var s = GameJson.Deserialize<EscapeState>(json);
        Assert.Null(s.Difficulty);
        Assert.Equal(EscapeDifficulty.Normal, s.Level);
        // …and a piece saved with a seat (all of them, before hiding spots) still loads.
        var piece = GameJson.Deserialize<PieceHolder>($$"""{"puzzleId":"tape","index":0,"seatId":"{{Ada}}"}""");
        Assert.Equal(Ada, piece.SeatId);
        Assert.False(piece.IsHidden);
    }

    [Fact]
    public void Cipher_tools_unlock_once_the_key_is_found_and_never_give_the_shift_away()
    {
        var s = Lab.Started(EscapeDifficulty.Hard, 21);
        EscapeCipherView Tool(EscapeState x, string id) => EscapeProjector.Stage(x, Room, T0).Puzzles.Single(p => p.Id == id).Cipher!;

        Assert.Equal(new EscapeCipherView(CipherType.Shift, false, null), Tool(s, "formula"));
        s = s.Do(new ExamineSpot(T0, Ada, "painting")); // the dial's number is on the portrait
        Assert.Equal(new EscapeCipherView(CipherType.Shift, true, null), Tool(s, "formula")); // the number itself is only in the look

        s = Lab.PlayToStage(1, seed: 21, difficulty: EscapeDifficulty.Hard);
        var room = EscapeEngine.RoomFor(s, Room);
        Assert.False(Tool(s, "signal").Unlocked);
        Assert.Null(Tool(s, "signal").Table);
        s = s.Do(new ExamineSpot(T0, Ada, "bookshelf")); // the manual…
        Assert.False(Tool(s, "signal").Unlocked);
        s = s.Do(new InspectItem(T0, Ada, "manual")); // …under the UV lamp
        var tool = Tool(s, "signal");
        Assert.True(tool.Unlocked);
        // The key card decodes the telegraph's message, letter by letter.
        var encoded = room.FindPuzzle("signal")!.Prompt["The telegraph taps: ".Length..];
        var decoded = string.Concat(encoded.Split(' ').Select(code => tool.Table!.Single(e => e.Code == code).Letter));
        Assert.Equal(room.FindPuzzle("signal")!.Answers[0], decoded);
    }

    [Fact]
    public void A_logic_puzzle_lists_its_row_for_the_phones_grid()
    {
        var s = Lab.PlayToStage(1, seed: 8);
        var room = EscapeEngine.RoomFor(s, Room);
        var view = EscapeProjector.Stage(s, Room, T0).Puzzles.Single(p => p.Id == "jars").Deduction!;
        Assert.Equal(4, view.Spots);
        Assert.Equal(room.FindPuzzle("jars")!.Lineup, view.Items);
        Assert.All(view.Items, i => Assert.Contains(i.ToUpperInvariant(), room.FindPuzzle("jars")!.Prompt)); // nothing the prompt doesn't say
    }

    [Fact]
    public void The_game_master_hears_about_finds()
    {
        var s = EscapeEngine.NewGame(7, ai: new EscapeAiFeatures { GameMaster = true });
        s = s.Do(new AddEscapePlayer(T0, Ada, "Ada", true, false)).Do(new StartEscape(T0));
        s = s.Do(new ExamineSpot(T0, Ada, "crate"));
        Assert.Equal(CueKind.Found, s.Cues[^1].Kind);
        Assert.Equal("crate", s.Cues[^1].Thing);
        var cues = s.Cues.Count;
        s = s.Do(new ExamineSpot(T0, Ada, "plant")); // nothing there: nothing to say
        Assert.Equal(cues, s.Cues.Count);
    }
}

/// <summary>What the screens must never see in the newer puzzles, at every length and difficulty.</summary>
public class HarderPrivacyTests
{
    private static EscapeRoom RoomById(string id) => id == Lab.Room.Id ? Lab.Room : Rooms.Get(id);

    /// <summary>The Laboratory and every shipped room, at every length and difficulty.</summary>
    public static TheoryData<string, EscapeDifficulty, int> Games()
    {
        var data = new TheoryData<string, EscapeDifficulty, int>();
        foreach (var room in Rooms.Library.Prepend(Lab.Room))
            foreach (var d in Enum.GetValues<EscapeDifficulty>())
                foreach (var minutes in room.PlayableLengths) data.Add(room.Id, d, minutes);
        return data;
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void Unsearched_spots_unread_items_hidden_pieces_and_answers_stay_on_the_server(string roomId, EscapeDifficulty difficulty, int minutes)
    {
        var template = RoomById(roomId);
        var seats = new[] { Lab.Ada, Lab.Ben };
        foreach (var seed in new long[] { 3, 987654321 })
        {
            var s0 = EscapeEngine.NewGame(seed, minutes: minutes, difficulty: difficulty);
            for (var i = 0; i < seats.Length; i++) s0 = EscapeEngine.Apply(s0, template, new AddEscapePlayer(Lab.T0, seats[i], $"P{i}", i == 0, false));
            var start = EscapeEngine.Apply(s0, template, new StartEscape(Lab.T0));
            var room = RoomVariants.Build(template, seed, difficulty); // every puzzle, including ones this game leaves out
            Lab.PlayToEnd(start, template, seats, s =>
            {
                var views = new List<string> { GameJson.Serialize(EscapeProjector.Stage(s, template, Lab.T0)) };
                views.AddRange(seats.Select(seat => GameJson.Serialize(EscapeProjector.Player(s, template, seat, Lab.T0))));
                var stageJson = views[0];

                foreach (var o in room.SceneObjects.Where(o => !s.Examined.Contains(o.Id)))
                {
                    Assert.All(views, v => Assert.DoesNotContain(o.Look, v));
                    if (o.Clue is { } clue) Assert.All(views, v => Assert.DoesNotContain(clue, v));
                }
                foreach (var i in room.Items.Where(i => i.Inspect is not null && !s.Inspected.Contains(i.Id)))
                    Assert.All(views, v => Assert.DoesNotContain(i.Inspect!, v));
                foreach (var r in room.Recipes.Where(r => !s.Notebook.Any(n => n.Text == r.Text)))
                    Assert.All(views, v => Assert.DoesNotContain(r.Text, v));
                foreach (var p in EscapeProjector.Stage(s, template, Lab.T0).Puzzles.Where(p => p.Cipher is not null))
                {
                    var puzzle = room.FindPuzzle(p.Id)!;
                    Assert.Equal(EscapeEngine.KeyFound(s, puzzle), p.Cipher!.Unlocked);
                    if (!p.Cipher.Unlocked || p.Cipher.Type == CipherType.Shift) Assert.Null(p.Cipher.Table);
                }
                foreach (var hidden in s.Pieces.Where(p => p.IsHidden))
                    Assert.All(views, v => Assert.DoesNotContain(room.FindPuzzle(hidden.PuzzleId)!.Pieces[hidden.Index], v));
                if (s.Phase == EscapePhase.Playing)
                {
                    foreach (var p in room.Puzzles)
                        foreach (var answer in p.Answers.Where(a => a.Length >= 3))
                            Assert.False(stageJson.Contains($"\"{answer}\"", StringComparison.OrdinalIgnoreCase), $"answer of {p.Id} leaked");
                    if (seed > 1000) Assert.DoesNotContain(seed.ToString(), stageJson); // a short one could be any number on screen
                }
            });
        }
    }
}

/// <summary>Proofs over a thousand seeds that every generated puzzle is fair: one answer, reachable from what players see.</summary>
public class PuzzleProofTests
{
    private const int Seeds = 1000;

    [Fact]
    public void Every_cipher_decodes_to_its_word_with_the_key_as_written()
    {
        var words = new[] { "atom", "flask", "spark", "magnet", "crystal", "science", "quartz", "zebra" };
        foreach (var type in Enum.GetValues<CipherType>())
            foreach (var d in Enum.GetValues<EscapeDifficulty>())
                for (var seed = 0; seed < Seeds; seed++)
                {
                    var c = PuzzleGenerators.Cipher(type, words, d, new SeededRandom(seed));
                    Assert.Equal(c.Word, PuzzleGenerators.Decode(type, c.Encoded, c.Key));
                    Assert.NotEqual(c.Word, c.Encoded);
                    if (type is CipherType.Symbols or CipherType.Morse)
                        Assert.Equal(c.Word.Distinct().Count() + 2, c.Key.Split("   ").Length); // the letters needed, plus two spare
                }
    }

    [Fact]
    public void Every_number_pattern_follows_its_rule_to_the_answer()
    {
        foreach (var d in Enum.GetValues<EscapeDifficulty>())
            for (var seed = 0; seed < Seeds; seed++)
            {
                var m = PuzzleGenerators.Sequence(d, new SeededRandom(seed));
                var t = m.Shown.Append(m.Next).ToList();
                Assert.True(t.All(x => x > 0));
                Assert.True(m.Next < 100_000, "a code short enough to type");
                switch (m.Rule)
                {
                    case PuzzleGenerators.SequenceRule.Arithmetic:
                        Assert.Single(Diffs(t).Distinct());
                        break;
                    case PuzzleGenerators.SequenceRule.Geometric:
                        Assert.Single(t.Skip(1).Select((x, i) => x / t[i]).Distinct());
                        Assert.All(t.Skip(1).Select((x, i) => x % t[i]), r => Assert.Equal(0, r));
                        break;
                    case PuzzleGenerators.SequenceRule.Alternating:
                        var steps = Diffs(t);
                        Assert.Single(steps.Where((_, i) => i % 2 == 0).Distinct());
                        Assert.Single(steps.Where((_, i) => i % 2 == 1).Distinct());
                        Assert.NotEqual(steps[0], steps[1]);
                        break;
                    case PuzzleGenerators.SequenceRule.Quadratic:
                        Assert.Single(Diffs(Diffs(t)).Distinct());
                        Assert.NotEqual(0, Diffs(Diffs(t))[0]);
                        break;
                    case PuzzleGenerators.SequenceRule.Interleaved:
                        Assert.Single(Diffs(t.Where((_, i) => i % 2 == 0).ToList()).Distinct());
                        Assert.Single(Diffs(t.Where((_, i) => i % 2 == 1).ToList()).Distinct());
                        break;
                    default:
                        for (var i = 2; i < t.Count; i++) Assert.Equal(t[i - 1] + t[i - 2], t[i]);
                        break;
                }
                PuzzleGenerators.SequenceRule[] rules = d switch
                {
                    EscapeDifficulty.Easy => [PuzzleGenerators.SequenceRule.Arithmetic],
                    EscapeDifficulty.Normal => [PuzzleGenerators.SequenceRule.Geometric, PuzzleGenerators.SequenceRule.Alternating],
                    _ => [PuzzleGenerators.SequenceRule.Quadratic, PuzzleGenerators.SequenceRule.Interleaved, PuzzleGenerators.SequenceRule.Fibonacci],
                };
                Assert.Contains(m.Rule, rules); // harder patterns at harder levels
            }

        static List<long> Diffs(List<long> t) => t.Skip(1).Select((x, i) => x - t[i]).ToList();
    }

    [Fact]
    public void Every_logic_puzzle_has_exactly_one_answer_and_needs_every_clue()
    {
        var words = new[] { "red jar", "blue jar", "green jar", "gold jar", "silver jar" };
        foreach (var d in Enum.GetValues<EscapeDifficulty>())
            for (var seed = 0; seed < Seeds; seed++)
            {
                var m = PuzzleGenerators.Deduction(words, d, new SeededRandom(seed));
                var n = PuzzleGenerators.DeductionSize(d);
                Assert.Equal(n, m.Items.Count);
                Assert.Equal(1, PuzzleGenerators.CountSolutions(n, m.Clues));
                Assert.All(m.Clues, c => Assert.True(PuzzleGenerators.Holds(c, m.PositionOf))); // every clue is true of the answer
                for (var i = 0; i < m.Clues.Count; i++)
                    Assert.True(PuzzleGenerators.CountSolutions(n, m.Clues.Where((_, j) => j != i).ToList()) > 1, "no clue is spare");
                Assert.Equal(n, m.Answer.Distinct().Count());
            }
    }

    [Fact]
    public void Every_light_panel_starts_unsolved_and_its_presses_turn_every_light_on()
    {
        foreach (var d in Enum.GetValues<EscapeDifficulty>())
            for (var seed = 0; seed < Seeds; seed++)
            {
                var m = PuzzleGenerators.Switches(d, new SeededRandom(seed));
                var cells = m.Grid.Size * m.Grid.Size;
                Assert.True(m.Grid.Lit.Count < cells);
                IEnumerable<int> lit = m.Grid.Lit;
                foreach (var p in m.Presses) lit = PuzzleGenerators.Press(m.Grid.Size, lit, p);
                Assert.Equal(cells, lit.Count());
                Assert.Equal(PuzzleGenerators.SwitchesSize(d).Presses, m.Presses.Count);
            }
    }
}

public class HarderValidatorTests
{
    private static EscapeRoom Lab2(Func<EscapeRoom, EscapeRoom> change) => change(GameJson.Deserialize<EscapeRoom>(GameJson.Serialize(Lab.Room)));

    private static EscapeRoom WithObject(EscapeRoom r, string id, Func<SceneObject, SceneObject> change) => r with
    {
        Stages = r.Stages.Select(s => s.Scene is null ? s : s with { Scene = s.Scene with { Objects = s.Scene.Objects.Select(o => o.Id == id ? change(o) : o).ToList() } }).ToList(),
    };

    private static EscapeRoom WithPuzzle(EscapeRoom r, string id, Func<EscapePuzzle, EscapePuzzle> change) =>
        r with { Puzzles = r.Puzzles.Select(p => p.Id == id ? change(p) : p).ToList() };

    [Fact]
    public void A_key_for_something_that_isnt_a_cipher_is_caught() =>
        Assert.Contains(EscapeRoomValidator.Validate(Lab2(r => WithObject(r, "plant", o => o with { Look = "A note: {key:jars}" }))),
            e => e.Contains("'jars' isn't a cipher"));

    [Fact]
    public void A_cipher_whose_key_is_nowhere_is_caught() =>
        Assert.Contains(EscapeRoomValidator.Validate(Lab2(r => WithObject(r, "painting", o => o with { Look = "The professor." }))),
            e => e.Contains("{key:formula}"));

    [Fact]
    public void A_key_the_group_can_only_find_later_is_caught()
    {
        // The formula's key moves to the next stage's window: the blackboard can't be read in time.
        var errors = EscapeRoomValidator.Validate(Lab2(r => WithObject(WithObject(r, "painting", o => o with { Look = "The professor." }), "window", o => o with { Look = "Scratched: {key:formula}" })));
        Assert.Contains(errors, e => e.Contains("'formula'") && e.Contains("key"));
    }

    [Fact]
    public void A_spot_needing_a_tool_nobody_finds_is_caught()
    {
        var errors = EscapeRoomValidator.Validate(Lab2(r => WithObject(r, "poster", o => o with { Requires = "magnifier" }) with
        {
            Items = [.. r.Items, new EscapeItem { Id = "magnifier", Name = "a magnifying glass" }],
        }));
        Assert.Contains(errors, e => e.Contains("'gather'") && e.Contains("'poster'"));
    }

    [Fact]
    public void Too_few_hiding_spots_for_a_solo_player_is_caught()
    {
        var errors = EscapeRoomValidator.Validate(Lab2(r => r with
        {
            Stages = r.Stages.Select(s => s.Id != "cabinet" ? s : s with { Scene = s.Scene! with { Objects = s.Scene.Objects.Where(o => o.Id is not ("shelf" or "barrel" or "lamp" or "box" or "chest")).ToList() } }).ToList(),
        }));
        Assert.Contains(errors, e => e.Contains("hiding spots") && e.Contains("solo player"));
    }

    [Fact]
    public void Search_puzzles_must_name_spots_in_their_own_stage() =>
        Assert.Contains(EscapeRoomValidator.Validate(Lab2(r => WithPuzzle(r, "gather", p => p with { Finds = ["crate", "window"] }))),
            e => e.Contains("'window'") && e.Contains("its stage"));

    [Fact]
    public void Broken_recipes_are_caught()
    {
        var errors = EscapeRoomValidator.Validate(Lab2(r => r with { Recipes = [new EscapeRecipe { Items = ["bulb", "bulb"], Makes = "uv-lamp" }, new EscapeRecipe { Items = ["bulb", "ghost"], Makes = "nothing" }] }));
        Assert.Contains(errors, e => e.Contains("exactly two different items"));
        Assert.Contains(errors, e => e.Contains("'ghost'"));
    }

    [Fact]
    public void An_item_that_is_both_a_tool_and_used_up_is_caught() =>
        Assert.Contains(EscapeRoomValidator.Validate(Lab2(r => WithPuzzle(r, "sequence", p => p with { Requires = ["cabinet-key", "uv-lamp"] }))),
            e => e.Contains("'uv-lamp'") && e.Contains("tool"));

    [Fact]
    public void Spots_must_be_drawable_and_inside_the_scene()
    {
        var errors = EscapeRoomValidator.Validate(Lab2(r => WithObject(WithObject(r, "plant", o => o with { Prop = "spaceship" }), "rug", o => o with { X = 900 })));
        Assert.Contains(errors, e => e.Contains("'spaceship'"));
        Assert.Contains(errors, e => e.Contains("'rug'") && e.Contains("fit inside"));
    }

    [Fact]
    public void Family_rooms_check_what_spots_and_items_say_too() =>
        Assert.Contains(EscapeRoomValidator.Validate(Lab2(r => WithObject(r, "plant", o => o with { Look = "A dead plant." }))), e => e.Contains("'dead'"));

    [Fact]
    public void Generators_need_their_placeholders_and_kinds()
    {
        var errors = EscapeRoomValidator.Validate(Lab2(r => WithPuzzle(WithPuzzle(r, "sequence", p => p with { Prompt = "No numbers here." }), "lights", p => p with { Kind = PuzzleKind.Code })));
        Assert.Contains(errors, e => e.Contains("{sequence}"));
        Assert.Contains(errors, e => e.Contains("'lights'") && e.Contains("Switches puzzle"));
    }
}
