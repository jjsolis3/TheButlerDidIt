using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;

namespace ButlerDidIt.Escape.Tests;

/// <summary>
/// Who answers a puzzle, and who can find its things (#132): taking a puzzle, dealing them round the table, handing
/// back and passing on, and searching with a purpose. Played in the Laboratory, whose formula cipher has its key
/// (and two decoys) written on spots, and on every built-in room with the bot.
/// </summary>
public class TurnTests
{
    private static EscapeRoom Room => Lab.Room;
    private static readonly DateTimeOffset T0 = Lab.T0;
    private static readonly Guid Ada = Lab.Ada, Ben = Lab.Ben, Cy = Lab.Cy;
    private static readonly string[] Names = ["Ada", "Ben", "Cy"];

    private static EscapeState Game(AnswerRule rule, EscapeDifficulty? level = null, params Guid[] seats)
    {
        if (seats.Length == 0) seats = [Ada, Ben];
        var s = EscapeEngine.NewGame(7, difficulty: level, answering: rule);
        for (var i = 0; i < seats.Length; i++) s = s.Do(new AddEscapePlayer(T0, seats[i], Names[i], i == 0, false));
        return s.Do(new StartEscape(T0));
    }

    private static string Answer(EscapeState s, string puzzleId) => EscapeEngine.RoomFor(s, Room).FindPuzzle(puzzleId)!.Answers[0];
    private static EscapePuzzleView View(EscapeState s, string puzzleId, DateTimeOffset? at = null) =>
        EscapeProjector.Stage(s, Room, at ?? T0).Puzzles.Single(p => p.Id == puzzleId);

    // ------------------------------------------------------------------ who answers

    [Fact]
    public void Anyone_answers_anything_unless_the_party_says_otherwise_and_a_lone_player_is_never_held_back()
    {
        var anyone = Game(AnswerRule.Anyone);
        Assert.True(anyone.Do(new SubmitAnswer(T0, Ben, "digits", Answer(anyone, "digits"))).IsSolved("digits"));
        Assert.Throws<GameRuleException>(() => anyone.Do(new TakePuzzle(T0, Ada, "digits")));
        Assert.Equal(AnswerRule.Anyone, EscapeProjector.Stage(anyone, Room, T0).Answering);

        // Taking turns needs someone to take turns with.
        var alone = Game(AnswerRule.TakeIt, seats: [Ada]);
        Assert.False(alone.TakesTurns());
        Assert.Equal(AnswerRule.Anyone, EscapeProjector.Stage(alone, Room, T0).Answering);
        Assert.True(alone.Do(new SubmitAnswer(T0, Ada, "digits", Answer(alone, "digits"))).IsSolved("digits"));
    }

    [Fact]
    public void With_take_it_only_the_holder_answers_and_each_player_works_on_one_puzzle_at_a_time()
    {
        var s = Game(AnswerRule.TakeIt);
        Assert.Equal(AnswerRule.TakeIt, EscapeProjector.Stage(s, Room, T0).Answering);
        var ex = Assert.Throws<GameRuleException>(() => s.Do(new SubmitAnswer(T0, Ada, "digits", Answer(s, "digits"))));
        Assert.Contains("Take this puzzle first", ex.Message);
        Assert.Throws<GameRuleException>(() => s.Do(new TakePuzzle(T0, Ada, "gather"))); // a Search puzzle is everyone's

        s = s.Do(new TakePuzzle(T0, Ada, "digits"));
        Assert.Contains("Ada is working on", s.Feed[^1].Text);
        Assert.Equal(new EscapeHoldView(Ada, "Ada", false), View(s, "digits").HeldBy);
        Assert.Contains("Ada is working on this one", Assert.Throws<GameRuleException>(() => s.Do(new SubmitAnswer(T0, Ben, "digits", "1"))).Message);
        Assert.Contains("Ada is working on this one", Assert.Throws<GameRuleException>(() => s.Do(new TakePuzzle(T0, Ben, "digits"))).Message);
        Assert.Contains("Hand it back first", Assert.Throws<GameRuleException>(() => s.Do(new TakePuzzle(T0, Ada, "formula"))).Message);

        s = s.Do(new TakePuzzle(T0, Ben, "formula"));
        s = s.Do(new SubmitAnswer(T0, Ada, "digits", Answer(s, "digits")));
        Assert.True(s.IsSolved("digits"));
        Assert.Null(View(s, "digits").HeldBy);
        s = s.Do(new TakePuzzle(T0, Ada, "sequence")); // free again once it's solved
        Assert.Equal(Ada, s.Holds["sequence"].SeatId);
    }

    [Fact]
    public void Three_wrong_tries_in_a_row_put_a_puzzle_back_on_the_table()
    {
        var s = Game(AnswerRule.TakeIt).Do(new TakePuzzle(T0, Ada, "digits"));
        for (var i = 0; i < EscapeEngine.MissesBeforeFree; i++) s = s.Do(new SubmitAnswer(T0.AddSeconds(5 * i), Ada, "digits", "0"));
        Assert.False(s.Holds.ContainsKey("digits"));
        Assert.Contains("free to take after 3 tries", s.Feed[^1].Text);
        Assert.Equal(Ben, s.Do(new TakePuzzle(T0.AddSeconds(20), Ben, "digits")).Holds["digits"].SeatId);
    }

    [Fact]
    public void A_puzzle_whose_holder_has_stopped_trying_can_be_taken_over()
    {
        var s = Game(AnswerRule.TakeIt).Do(new TakePuzzle(T0, Ada, "digits"));
        // A try keeps it theirs: the clock for "stopped trying" starts again.
        s = s.Do(new SubmitAnswer(T0.AddMinutes(2), Ada, "digits", "0"));
        Assert.Throws<GameRuleException>(() => s.Do(new TakePuzzle(T0.AddMinutes(4), Ben, "digits")));
        Assert.False(View(s, "digits", T0.AddMinutes(4)).HeldBy!.Free);

        var later = T0.AddMinutes(2) + EscapeEngine.TakeOverAfter;
        Assert.True(View(s, "digits", later).HeldBy!.Free);
        s = s.Do(new TakePuzzle(later, Ben, "digits"));
        Assert.Equal(Ben, s.Holds["digits"].SeatId);
        Assert.Contains("Ben took over", s.Feed[^1].Text);
        Assert.Equal(0, s.Holds["digits"].Misses); // a fresh start for the new holder
    }

    [Fact]
    public void A_puzzle_can_be_handed_back_passed_on_or_freed_by_the_host()
    {
        var s = Game(AnswerRule.TakeIt, seats: [Ada, Ben, Cy]).Do(new TakePuzzle(T0, Ada, "digits")).Do(new TakePuzzle(T0, Cy, "formula"));
        Assert.Throws<GameRuleException>(() => s.Do(new ReleasePuzzle(T0, Ben, "digits"))); // not Ben's to hand back
        Assert.Throws<GameRuleException>(() => s.Do(new PassPuzzle(T0, Ben, "digits", Cy)));
        Assert.Contains("already working on", Assert.Throws<GameRuleException>(() => s.Do(new PassPuzzle(T0, Ada, "digits", Cy))).Message);

        var passed = s.Do(new PassPuzzle(T0, Ada, "digits", Ben));
        Assert.Equal(Ben, passed.Holds["digits"].SeatId);
        Assert.Contains("Ada passed", passed.Feed[^1].Text);

        var back = s.Do(new ReleasePuzzle(T0, Ada, "digits"));
        Assert.False(back.Holds.ContainsKey("digits"));
        Assert.Contains("handed back", back.Feed[^1].Text);

        var freed = s.Do(new ReleasePuzzle(T0, null, "formula"));
        Assert.False(freed.Holds.ContainsKey("formula"));
        Assert.Contains("The host freed", freed.Feed[^1].Text);
    }

    [Fact]
    public void Dealt_puzzles_go_round_the_table_evenly_stage_after_stage()
    {
        var seats = new[] { Ada, Ben, Cy };
        var s = Game(AnswerRule.Dealt, seats: seats);
        Assert.Contains(s.Feed, f => f.Text.Contains("dealt"));
        // The first stage's three puzzles go to three different people; its Search puzzle to nobody.
        Assert.Equal(["digits", "formula", "sequence"], s.Holds.Keys.Order());
        Assert.Equal(3, s.Holds.Values.Select(h => h.SeatId).Distinct().Count());

        // A dealt player can still take a free puzzle, as many as they like.
        var freed = s.Do(new ReleasePuzzle(T0, null, "formula"));
        var holder = s.Holds["digits"].SeatId;
        Assert.Equal(holder, freed.Do(new TakePuzzle(T0, holder, "formula")).Holds["formula"].SeatId);

        // The next stage is dealt as it opens, carrying on round the table: over the game, nobody gets two more than anyone.
        var counts = s.Holds.Values.GroupBy(h => h.SeatId).ToDictionary(g => g.Key, g => g.Count());
        var t = T0;
        while (s.StageIndex == 0)
        {
            t = t.AddSeconds(5);
            s = s.Do(EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, Room), Ada, t));
        }
        Assert.Equal(["frame", "jars", "lights"], s.Holds.Keys.Order()); // (the Morse signal is Hard's)
        foreach (var h in s.Holds.Values) counts[h.SeatId] = counts.GetValueOrDefault(h.SeatId) + 1;
        Assert.True(counts.Values.Max() - counts.Values.Min() <= 1, string.Join(", ", counts.Values));
    }

    [Fact]
    public void A_player_who_leaves_puts_their_puzzles_back_on_the_table()
    {
        var s = Game(AnswerRule.TakeIt, seats: [Ada, Ben, Cy]).Do(new TakePuzzle(T0, Ben, "digits"));
        s = s.Do(new RemoveEscapePlayer(T0, Ben));
        Assert.Empty(s.Holds);
    }

    // ------------------------------------------------------------------ searching with a purpose

    [Fact]
    public void Only_the_puzzles_holder_can_read_where_its_key_is_written()
    {
        var s = Game(AnswerRule.TakeIt);
        // The real key and its decoys, on the spots shown at this difficulty.
        var room = EscapeEngine.RoomFor(s, Room);
        var keySpots = room.Stages[0].Scene!.Objects.Count(o => room.FindPuzzle("formula")!.KeyAt.Contains($"object:{o.Id}"));
        Assert.True(keySpots >= 2);
        Assert.Equal(keySpots, View(s, "formula").KeysHidden);

        // Nobody has the cipher: Ben finds nothing on the painting, it costs time, and its writing stays unseen.
        var ben = s.Do(new ExamineSpot(T0, Ben, "painting"));
        Assert.DoesNotContain("painting", ben.Examined);
        Assert.DoesNotContain("formula", ben.KeysFound);
        Assert.Equal(s.Deadline!.Value.AddSeconds(-EscapeEngine.SearchPenaltySeconds(EscapeDifficulty.Normal)), ben.Deadline);
        var painted = room.FindObject("painting")!.Look;
        foreach (var json in new[]
        {
            GameJson.Serialize(EscapeProjector.Stage(ben, Room, T0)),
            GameJson.Serialize(EscapeProjector.Player(ben, Room, Ada, T0)),
            GameJson.Serialize(EscapeProjector.Player(ben, Room, Ben, T0)),
        })
            Assert.DoesNotContain($"\n{painted}\n", ViewText.Decoded(json));

        // Ada takes it, and the painting is hers to read.
        s = ben.Do(new TakePuzzle(T0, Ada, "formula")).Do(new ExamineSpot(T0, Ada, "painting"));
        Assert.Contains("painting", s.Examined);
        Assert.Contains("formula", s.KeysFound);
        Assert.Contains("Ada found writing", s.Feed[^1].Text);
        Assert.Equal(keySpots - 1, View(s, "formula").KeysHidden);
        Assert.Equal(painted, EscapeProjector.Stage(s, Room, T0).Scene!.Objects.Single(o => o.Id == "painting").Look);

        // Still not Ben's: the ledge stays Ada's to find.
        Assert.DoesNotContain("ledge", s.Do(new ExamineSpot(T0, Ben, "ledge")).Examined);

        // Once the cipher is solved, its key is nobody's secret: anyone can read what's left.
        s = s.Do(new SubmitAnswer(T0, Ada, "formula", Answer(s, "formula")));
        Assert.Contains("ledge", s.Do(new ExamineSpot(T0, Ben, "ledge")).Examined);
    }

    [Fact]
    public void Only_the_puzzles_holder_finds_its_hidden_clue_pieces()
    {
        // Two players and more pieces than that: the spare pieces wait in hiding spots.
        var s = Game(AnswerRule.TakeIt);
        var hidden = s.Pieces.First(p => p.IsHidden);
        var spot = hidden.SpotId!;

        // Ben searches there first: whatever the spot itself holds is his, the piece isn't.
        var ben = s.Do(new ExamineSpot(T0, Ben, spot));
        Assert.Contains(spot, ben.Examined);
        Assert.Contains(ben.Pieces, p => p == hidden);

        // Ada takes the puzzle and searches the same spot again: the piece is hers, and it's no wasted search.
        var ada = ben.Do(new TakePuzzle(T0, Ada, hidden.PuzzleId)).Do(new ExamineSpot(T0, Ada, spot));
        Assert.Contains(ada.Pieces, p => p.PuzzleId == hidden.PuzzleId && p.Index == hidden.Index && p.SeatId == Ada);
        Assert.Equal(ben.Deadline, ada.Deadline);
        Assert.Contains("Ada found a clue piece", ada.Feed[^1].Text);
    }

    [Fact]
    public void Anyone_still_finds_items_and_with_anyone_answering_anyone_finds_everything()
    {
        var turns = Game(AnswerRule.TakeIt).Do(new ExamineSpot(T0, Ben, "crate"));
        Assert.Contains("bulb", turns.Inventory); // an item is the spot's own: first come, first served

        var anyone = Game(AnswerRule.Anyone).Do(new ExamineSpot(T0, Ben, "painting"));
        Assert.Contains("painting", anyone.Examined);
        Assert.Contains("formula", anyone.KeysFound);
    }

    // ------------------------------------------------------------------ every room still plays

    [Theory]
    [InlineData(AnswerRule.TakeIt, EscapeDifficulty.Normal)]
    [InlineData(AnswerRule.TakeIt, EscapeDifficulty.Hard)]
    [InlineData(AnswerRule.Dealt, EscapeDifficulty.Normal)]
    [InlineData(AnswerRule.Dealt, EscapeDifficulty.Easy)]
    public void Every_room_can_be_escaped_by_a_group_taking_turns(AnswerRule rule, EscapeDifficulty level)
    {
        Guid[] seats = [Ada, Ben, Cy];
        foreach (var template in Rooms.Library.Append(Room))
        {
            var s = EscapeEngine.NewGame(11, difficulty: level, answering: rule);
            for (var i = 0; i < seats.Length; i++) s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, seats[i], Names[i], i == 0, false));
            s = EscapeEngine.Apply(s, template, new StartEscape(T0));
            var end = EscapeBot.PlayToEnd(s, template, seats, T0);
            Assert.True(end.Phase == EscapePhase.Escaped, $"{template.Id} ({rule}, {level}): {end.Feed[^1].Text}");
        }
    }
}
