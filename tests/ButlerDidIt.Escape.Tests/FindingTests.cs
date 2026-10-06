using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;

namespace ButlerDidIt.Escape.Tests;

/// <summary>
/// Puzzles the group has to find, and a final lock built from the others (#134), played in the Lookout: a hatch to
/// climb through, then a deck whose locks turn up by searching the mast (the flag locker), solving the knot board
/// (the bottle), holding the red flag (the signal) and the spyglass (the far shore, in longer games), and a gangplank
/// whose code is the marks the others leave, in the order painted on the lifeboat.
/// </summary>
public class FindingTests
{
    private static readonly Lazy<EscapeRoom> Loaded = new(() =>
        GameJson.Deserialize<EscapeRoom>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "the-lookout.json"))));

    private static EscapeRoom Room => Loaded.Value;
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ada = Guid.NewGuid(), Ben = Guid.NewGuid();

    private static EscapeState Started(EscapeRoom room, long seed = 7, int? minutes = null, EscapeDifficulty? difficulty = null,
        AnswerRule answering = AnswerRule.Anyone, bool gameMaster = false, params Guid[] seats)
    {
        if (seats.Length == 0) seats = [Ada];
        var s = EscapeEngine.NewGame(seed, minutes: minutes, difficulty: difficulty, answering: answering, ai: new EscapeAiFeatures { GameMaster = gameMaster });
        for (var i = 0; i < seats.Length; i++) s = EscapeEngine.Apply(s, room, new AddEscapePlayer(T0, seats[i], i == 0 ? "Ada" : "Ben", i == 0, false));
        return EscapeEngine.Apply(s, room, new StartEscape(T0));
    }

    /// <summary>A game on the deck: the hatch is open, and the group holds the red flag.</summary>
    private static EscapeState OnDeck(EscapeRoom? room = null, long seed = 7, int? minutes = null, EscapeDifficulty? difficulty = null,
        AnswerRule answering = AnswerRule.Anyone, bool gameMaster = false, params Guid[] seats)
    {
        room ??= Room;
        var s = Started(room, seed, minutes, difficulty, answering, gameMaster, seats);
        // When puzzles are dealt, the hatch is someone's: they open it.
        var holder = s.Holds.TryGetValue("hatch", out var h) ? h.SeatId : Ada;
        return EscapeEngine.Apply(s, room, new SubmitAnswer(T0, holder, "hatch", "please"));
    }

    private static EscapeState Do(EscapeState s, EscapeCommand c, EscapeRoom? room = null) => EscapeEngine.Apply(s, room ?? Room, c);
    private static EscapeState Search(EscapeState s, string spot, Guid? seat = null) => Do(s, new ExamineSpot(T0, seat ?? Ada, spot));
    private static EscapeState Solve(EscapeState s, string puzzle, Guid? seat = null) =>
        Do(s, new SubmitAnswer(T0, seat ?? Ada, puzzle, EscapeEngine.RoomFor(s, Room).FindPuzzle(puzzle)!.Answers[0]));
    private static List<string> InSight(EscapeState s, EscapeRoom? room = null) => EscapeProjector.Stage(s, room ?? Room, T0).Puzzles.Select(p => p.Id).ToList();

    [Fact]
    public void The_lookout_is_valid_at_every_length_and_difficulty() => Assert.Empty(EscapeRoomValidator.Validate(Room));

    // ------------------------------------------------------------------ finding locks

    [Fact]
    public void A_stage_shows_only_the_locks_in_sight_and_the_rest_cannot_be_tried_or_even_named()
    {
        var s = OnDeck(seats: [Ada, Ben]);
        // The knot board from the start, and the signal because the group already holds the flag that brings it.
        Assert.Equal(["knots", "signal"], InSight(s));
        Assert.DoesNotContain(s.Feed, f => f.Text.Contains("new lock")); // it was simply there when the stage opened
        Assert.Equal(3, EscapeProjector.Stage(s, Room, T0).PuzzleCount); // the hatch, and the two in sight: not the hidden ones

        // A lock out of sight gets the same answer as one that doesn't exist.
        var answer = Assert.Throws<GameRuleException>(() => Do(s, new SubmitAnswer(T0, Ada, "bottle", "coral")));
        Assert.Equal("That isn't in this part of the room.", answer.Message);
        Assert.Throws<GameRuleException>(() => Do(s, new RequestEscapeHint(T0, Ada, "flags")));
        Assert.Throws<GameRuleException>(() => Do(s, new TakePuzzle(T0, Ada, "flags")));

        // Nothing about them reaches a screen: not their titles or prompts, and not their clue pieces.
        var played = EscapeEngine.RoomFor(s, Room);
        var screens = new[] { Ada, Ben }.Select(seat => ViewText.Decoded(GameJson.Serialize(EscapeProjector.Player(s, Room, seat, T0))))
            .Prepend(ViewText.Decoded(GameJson.Serialize(EscapeProjector.Stage(s, Room, T0)))).ToList();
        foreach (var hidden in new[] { "flags", "bottle", "gangplank" }.Select(played.FindPuzzle))
            foreach (var screen in screens)
            {
                Assert.DoesNotContain(hidden!.Title, screen);
                Assert.DoesNotContain(hidden.Prompt, screen);
                foreach (var piece in hidden.Pieces) Assert.DoesNotContain(piece, screen);
            }
    }

    [Fact]
    public void Searching_its_spot_finds_a_lock_and_costs_no_time()
    {
        var s = OnDeck();
        var deadline = s.Deadline;
        s = Search(s, "mast");
        Assert.Contains("flags", InSight(s));
        Assert.Equal("🔓 Ada found a new lock: The Flag Locker.", s.Feed[^1].Text);
        Assert.Equal(deadline, s.Deadline); // a find, not a wasted search

        // A spot with nothing to find still costs time on Normal: the mast wasn't free because it's a spot.
        s = Search(s, "rail");
        Assert.True(s.Deadline < deadline);
    }

    [Fact]
    public void Solving_one_lock_or_getting_an_item_brings_the_next_into_sight()
    {
        var s = OnDeck(minutes: 45);
        Assert.DoesNotContain("bottle", InSight(s));
        s = Solve(s, "knots");
        Assert.Contains("bottle", InSight(s));
        Assert.Contains(s.Feed, f => f.Text == "🔓 Ada found a new lock: The Message in a Bottle.");

        Assert.DoesNotContain("shore", InSight(s));
        s = Search(s, "barrel"); // gives the spyglass, which brings the far shore into sight
        Assert.Contains("shore", InSight(s));
    }

    [Fact]
    public void A_clue_piece_waits_in_its_hiding_spot_until_its_lock_is_found()
    {
        // Alone, Ada holds the flag locker's first piece; the second waits in the cannon, the deck's hiding spot.
        var s = OnDeck();
        Assert.Contains(s.Pieces, p => p.PuzzleId == "flags" && p.SpotId == "cannon");
        Assert.DoesNotContain(EscapeProjector.Player(s, Room, Ada, T0).Pieces, p => p.PuzzleId == "flags"); // not even her own, yet

        // Searched before anyone has seen the locker, the cannon keeps it: finding it would give the locker away.
        s = Search(s, "cannon");
        Assert.Contains(s.Pieces, p => p.PuzzleId == "flags" && p.IsHidden);
        Assert.DoesNotContain(s.Feed, f => f.Text.Contains("clue piece"));

        // Once the locker is found, Ada's piece shows, and a second look in the cannon turns up the other, for free.
        s = Search(s, "mast");
        Assert.Single(EscapeProjector.Player(s, Room, Ada, T0).Pieces, p => p.PuzzleId == "flags");
        var deadline = s.Deadline;
        s = Search(s, "cannon");
        Assert.Equal(2, EscapeProjector.Player(s, Room, Ada, T0).Pieces.Count(p => p.PuzzleId == "flags"));
        Assert.Equal(deadline, s.Deadline);
    }

    [Fact]
    public void A_lock_whose_spot_the_game_leaves_out_is_in_sight_from_the_start()
    {
        // The flag locker turns up behind the net, a spot only Hard has.
        var room = Room with { Puzzles = Room.Puzzles.Select(p => p.Id == "flags" ? p with { RevealedBy = "spot:net" } : p).ToList() };
        Assert.Empty(EscapeRoomValidator.Validate(room));
        Assert.Contains("flags", InSight(OnDeck(room), room));

        var hard = OnDeck(room, difficulty: EscapeDifficulty.Hard);
        Assert.DoesNotContain("flags", InSight(hard, room));
        Assert.Contains("flags", InSight(Do(hard, new ExamineSpot(T0, Ada, "net"), room), room));
    }

    [Fact]
    public void When_puzzles_are_dealt_a_lock_found_later_goes_to_the_next_player()
    {
        var s = OnDeck(answering: AnswerRule.Dealt, seats: [Ada, Ben]);
        Assert.Equal(["knots", "signal"], s.Holds.Keys.Order().ToList()); // only what's in sight is dealt
        var before = s.DealOffset;
        s = Search(s, "mast", Ben);
        Assert.True(s.Holds.ContainsKey("flags"));
        Assert.Equal(before + 1, s.DealOffset); // round the table, like the rest
        Assert.Contains(s.Feed, f => f.Text.StartsWith("🃏 The Flag Locker goes to "));
    }

    // ------------------------------------------------------------------ the final lock

    [Fact]
    public void The_final_lock_appears_once_the_rest_of_its_stage_is_open_and_its_code_is_the_marks_in_the_painted_order()
    {
        var s = OnDeck(minutes: 30, gameMaster: true);
        var played = EscapeEngine.RoomFor(s, Room);
        var final = played.FindPuzzle("gangplank")!.Final!;
        Assert.Equal(["bottle", "flags", "knots", "signal"], final.Parts.Select(p => p.Puzzle).Order().ToList()); // the 30-minute game's others

        s = Search(s, "mast");
        s = Search(s, "cannon");
        s = Search(s, "lifeboat");
        // The lifeboat shows the order the code reads in, as marks.
        Assert.Equal($"Painted on its side: {FinalLocks.OrderText(final.Parts)}.", EscapeProjector.Stage(s, Room, T0).Scene!.Objects.Single(o => o.Id == "lifeboat").Look);
        foreach (var id in new[] { "knots", "flags", "bottle" }) s = Solve(s, id);
        Assert.DoesNotContain("gangplank", InSight(s)); // one lock to go

        s = Solve(s, "signal");
        Assert.Contains("gangplank", InSight(s));
        Assert.Equal("🏁 Every other lock here is open. The final lock: The Gangplank.", s.Feed[^1].Text);
        Assert.Equal(CueKind.FinalLock, s.Cues[^1].Kind); // its moment, rather than the solve that caused it
        Assert.Equal("The Gangplank", s.Cues[^1].PuzzleTitle);

        var view = EscapeProjector.Stage(s, Room, T0);
        var gangplank = view.Puzzles.Single(p => p.Id == "gangplank");
        Assert.True(gangplank.Final);
        Assert.Equal("The gangplank's keypad wants four digits. Every lock on deck left a mark when it opened.", gangplank.Prompt);
        // Each opened lock shows the mark it left.
        foreach (var part in final.Parts)
            Assert.Equal(new EscapeMarkView(part.Mark, part.Digit), view.Puzzles.Single(p => p.Id == part.Puzzle).Mark);
        Assert.Null(gangplank.Mark);

        var code = FinalLocks.Code(final.Parts);
        Assert.Equal([code], played.FindPuzzle("gangplank")!.Answers);
        Assert.Equal($"The code is {code}.", played.FindPuzzle("gangplank")!.Hints[1]);
        var wrong = code == "0000" ? "1111" : "0000";
        s = Do(s, new SubmitAnswer(T0, Ada, "gangplank", wrong));
        Assert.False(s.IsSolved("gangplank"));
        s = Do(s, new SubmitAnswer(T0.AddSeconds(5), Ada, "gangplank", code));
        Assert.Equal(EscapePhase.Escaped, s.Phase);
    }

    [Fact]
    public void The_final_code_is_built_from_the_puzzles_the_game_plays_and_changes_with_the_puzzle_set()
    {
        foreach (var (minutes, others) in new[] { (30, 4), (45, 5) })
        {
            var played = RoomLengths.Cut(RoomVariants.Build(Room, 3), minutes);
            var gangplank = played.FindPuzzle("gangplank")!;
            Assert.Equal(others, gangplank.Final!.Parts.Count);
            Assert.Equal(others, gangplank.Final.Parts.Select(p => p.Mark).Distinct().Count());
            Assert.Equal(others, gangplank.Answers[0].Length);
            Assert.All(gangplank.Final.Parts, p => Assert.Contains(p.Mark, played.FindObject("lifeboat")!.Look));
            Assert.Equal(minutes == 45, gangplank.Final.Parts.Any(p => p.Puzzle == "shore"));
        }

        // A new game, a new code (#133): over twenty puzzle sets, plenty of different ones.
        var codes = Enumerable.Range(0, 20).Select(seed => EscapeEngine.RoomFor(EscapeEngine.NewGame(seed), Room).FindPuzzle("gangplank")!.Answers[0]).ToHashSet();
        Assert.True(codes.Count > 15, $"{codes.Count} different codes");
        // And the same set always builds the same code.
        Assert.Equal(EscapeEngine.RoomFor(EscapeEngine.NewGame(11), Room).FindPuzzle("gangplank")!.Answers, EscapeEngine.RoomFor(EscapeEngine.NewGame(11), Room).FindPuzzle("gangplank")!.Answers);
    }

    [Fact]
    public void The_bot_escapes_the_lookout_however_the_puzzles_are_answered()
    {
        foreach (var rule in Enum.GetValues<AnswerRule>())
            foreach (var difficulty in Enum.GetValues<EscapeDifficulty>())
            {
                var s = Started(Room, 5, 45, difficulty, rule, seats: [Ada, Ben]);
                s = EscapeBot.PlayToEnd(s, Room, [Ada, Ben], T0);
                Assert.Equal(EscapePhase.Escaped, s.Phase);
            }
    }

    // ------------------------------------------------------------------ the recap

    [Fact]
    public void A_recap_lists_only_the_locks_the_group_found_and_a_stage_with_one_still_hidden_was_never_cleared()
    {
        var s = OnDeck();
        s = Solve(s, "knots");
        s = Do(s, new EscapeTick(T0.AddMinutes(45)));
        var deck = EscapeProjector.Recap(s, Room).Stages.Single(st => st.Title == "The Deck");
        Assert.Equal(["The Knot Board", "The Message in a Bottle", "The Signal"], deck.Puzzles.Select(p => p.Title).ToList());
        Assert.Null(deck.ClearedAt);
    }

    // ------------------------------------------------------------------ the validator

    private static EscapeRoom With(string puzzle, Func<EscapePuzzle, EscapePuzzle> change) =>
        Room with { Puzzles = Room.Puzzles.Select(p => p.Id == puzzle ? change(p) : p).ToList() };

    private static EscapeRoom WithSpot(string spot, Func<SceneObject, SceneObject> change) => Room with
    {
        Stages = Room.Stages.Select(st => st.Scene is null ? st : st with
        {
            Scene = st.Scene with { Objects = st.Scene.Objects.Select(o => o.Id == spot ? change(o) : o).ToList() },
        }).ToList(),
    };

    [Theory]
    [InlineData("spot:straw", "isn't a spot in its stage's scene")]
    [InlineData("puzzle:hatch", "isn't a puzzle in its stage")]
    [InlineData("puzzle:bottle", "can't be found by solving itself")]
    [InlineData("puzzle:gangplank", "the final lock")]
    [InlineData("item:compass", "doesn't exist")]
    [InlineData("mast", "use \"spot:<id>\"")]
    public void What_brings_a_lock_into_sight_must_be_in_its_own_stage(string revealedBy, string error) =>
        Assert.Contains(EscapeRoomValidator.Validate(With("bottle", p => p with { RevealedBy = revealedBy })), e => e.Contains("'bottle'") && e.Contains(error));

    [Fact]
    public void A_lock_that_can_never_be_found_is_caught()
    {
        // The bottle and the flag locker each wait for the other.
        var room = Room with { Puzzles = Room.Puzzles.Select(p => p.Id == "flags" ? p with { RevealedBy = "puzzle:bottle" } : p.Id == "bottle" ? p with { RevealedBy = "puzzle:flags" } : p).ToList() };
        Assert.Contains(EscapeRoomValidator.Validate(room), e => e.Contains("can't be finished") && e.Contains("is never found"));
    }

    [Fact]
    public void A_stage_has_something_in_sight_when_it_opens()
    {
        var room = Room with { Puzzles = Room.Puzzles.Select(p => p.Id is "knots" or "signal" ? p with { RevealedBy = "spot:rail" } : p).ToList() };
        Assert.Contains(EscapeRoomValidator.Validate(room), e => e.Contains("Stage 'deck' opens with nothing in sight"));
    }

    [Fact]
    public void A_final_lock_needs_its_order_written_where_the_group_can_read_it()
    {
        // Nowhere at all.
        Assert.Contains(EscapeRoomValidator.Validate(WithSpot("lifeboat", o => o with { Look = "Just a lifeboat." })), e => e.Contains("{order:gangplank}"));
        // Only on a spot Hard has: the other games can't read it.
        var hardOnly = WithSpot("lifeboat", o => o with { Look = "Just a lifeboat." });
        hardOnly = hardOnly with
        {
            Stages = hardOnly.Stages.Select(st => st.Scene is null ? st : st with
            {
                Scene = st.Scene with { Objects = st.Scene.Objects.Select(o => o.Id == "net" ? o with { Look = "Behind it: {order:gangplank}." } : o).ToList() },
            }).ToList(),
        };
        Assert.Contains(EscapeRoomValidator.Validate(hardOnly), e => e.Contains("whose order isn't written anywhere the group can reach"));
        // Somewhere that's never filled in, like the stage's description.
        var described = Room with { Stages = Room.Stages.Select(st => st.Id == "deck" ? st with { Description = "Read {order:gangplank}." } : st).ToList() };
        Assert.Contains(EscapeRoomValidator.Validate(described), e => e.Contains("final lock's order where the group can't read it"));
        // And only for a final lock.
        Assert.Contains(EscapeRoomValidator.Validate(WithSpot("rail", o => o with { Look = "{order:knots}" })), e => e.Contains("'knots' isn't a final lock"));
    }

    [Fact]
    public void A_final_lock_is_built_from_at_least_three_puzzles_each_with_its_own_mark()
    {
        // Two marks for four puzzles.
        Assert.Contains(EscapeRoomValidator.Validate(With("gangplank", p => p with { Generator = new PuzzleGenerator { Type = GeneratorType.Final, Marks = ["⚓", "🦜"] } })),
            e => e.Contains("only 2 marks"));
        // A digit in a mark would muddle the code.
        Assert.Contains(EscapeRoomValidator.Validate(With("gangplank", p => p with { Generator = new PuzzleGenerator { Type = GeneratorType.Final, Marks = ["⚓", "🦜", "🐚", "⭐", "No. 9"] } })),
            e => e.Contains("no digits"));
        // Its code is built, never written.
        Assert.Contains(EscapeRoomValidator.Validate(With("gangplank", p => p with { Answers = ["1234"] })), e => e.Contains("no answers or clue pieces of its own"));
        // Only two other puzzles in a 30-minute game.
        var small = Room with { Puzzles = Room.Puzzles.Select(p => p.Id is "flags" or "bottle" ? p with { MinMinutes = 45 } : p).ToList() };
        Assert.Contains(EscapeRoomValidator.Validate(small), e => e.StartsWith("At 30 minutes:") && e.Contains("built from 2 other puzzles"));
    }
}
