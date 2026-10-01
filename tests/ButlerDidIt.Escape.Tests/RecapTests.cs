using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;

namespace ButlerDidIt.Escape.Tests;

/// <summary>The recap after the game (#111): the timeline adds up, and a shared recap never spoils the room.</summary>
public class RecapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid[] Seats = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private static EscapeState Started(EscapeRoom template, long seed, int? minutes = null)
    {
        var s = EscapeEngine.NewGame(seed, minutes: minutes);
        for (var i = 0; i < Seats.Length; i++) s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, Seats[i], $"P{i}", i == 0, false));
        return EscapeEngine.Apply(s, template, new StartEscape(T0));
    }

    /// <summary>Plays until the group walks into stage <paramref name="stageIndex"/>, then lets the clock run out.</summary>
    private static EscapeState TrappedIn(EscapeRoom template, long seed, int stageIndex)
    {
        var s = Started(template, seed);
        var t = T0;
        for (var move = 0; s.StageIndex < stageIndex; move++)
        {
            t = t.AddSeconds(5);
            s = EscapeEngine.Apply(s, template, EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, template), Seats[move % Seats.Length], t));
        }
        return EscapeEngine.Apply(s, template, new EscapeTick(s.Deadline!.Value.AddSeconds(1)));
    }

    [Fact]
    public void The_timeline_adds_up()
    {
        var template = Rooms.Get("the-workshop");
        var s = EscapeBot.PlayToEnd(Started(template, 42), template, Seats, T0);
        Assert.Equal(EscapePhase.Escaped, s.Phase);
        var room = EscapeEngine.RoomFor(s, template);

        var recap = EscapeProjector.Recap(s, template);

        Assert.True(recap.Escaped);
        Assert.Equal(room.EscapedText, recap.EndText);
        Assert.Equal((int)(s.EndedAt!.Value - T0).TotalSeconds, recap.ElapsedSeconds);
        Assert.Equal(EscapeEngine.Score(s, template), recap.Score);
        Assert.Equal(room.Stages.Count, recap.Stages.Count);
        Assert.Equal(room.Puzzles.Count, recap.SolvedCount);
        Assert.Equal(42, recap.PuzzleSet);
        Assert.True(recap.SecondsLeft > 0);

        // Each stage opens when the one before it is cleared, and is cleared by its last puzzle.
        Assert.Equal(0, recap.Stages[0].OpenedAt);
        for (var i = 0; i < recap.Stages.Count; i++)
        {
            var stage = recap.Stages[i];
            Assert.Equal(room.Stages[i].Title, stage.Title);
            Assert.All(stage.Puzzles, p => Assert.NotNull(p.SolvedBy));
            Assert.Equal(stage.Puzzles.Max(p => p.SolvedAt), stage.ClearedAt);
            Assert.All(stage.Puzzles, p => Assert.InRange(p.SolvedAt!.Value, stage.OpenedAt, stage.ClearedAt!.Value));
            if (i > 0) Assert.Equal(recap.Stages[i - 1].ClearedAt, stage.OpenedAt);
        }
        Assert.Equal(recap.ElapsedSeconds, recap.Stages[^1].ClearedAt);

        // Everyone's count adds up to the puzzles opened, and the highlights name who did most.
        Assert.Equal(recap.SolvedCount, recap.Team.Sum(p => p.Solved));
        Assert.Contains(recap.Highlights, h => h.Title == "Most puzzles opened");
        Assert.Contains(recap.Highlights, h => h.Title == "First breakthrough");
        Assert.Contains(recap.Highlights, h => h.Title == "No hints needed" && h.Detail == "Escaped without a single hint.");
    }

    [Fact]
    public void Hints_are_counted_per_puzzle_and_stage()
    {
        var template = Rooms.Get("the-workshop");
        var s = Started(template, 7);
        var first = EscapeEngine.RoomFor(s, template).Stages[0].Puzzles[0];
        s = EscapeEngine.Apply(s, template, new RequestEscapeHint(T0.AddSeconds(1), Seats[0], first));
        s = EscapeBot.PlayToEnd(s, template, Seats, T0.AddSeconds(1));

        var recap = EscapeProjector.Recap(s, template);

        Assert.Equal(1, recap.HintsUsed);
        Assert.Equal(1, recap.Stages[0].Hints);
        Assert.Equal(1, recap.Stages[0].Puzzles[0].Hints);
        Assert.Equal(recap.ElapsedSeconds + recap.HintPenaltySeconds, recap.Score);
        Assert.DoesNotContain(recap.Highlights, h => h.Detail == "Escaped without a single hint.");
    }

    [Fact]
    public void A_trapped_group_sees_only_the_stages_it_reached()
    {
        var template = Rooms.Get("the-workshop");
        var s = TrappedIn(template, 3, stageIndex: 1);
        Assert.Equal(EscapePhase.Failed, s.Phase);
        var room = EscapeEngine.RoomFor(s, template);

        var recap = EscapeProjector.Recap(s, template);

        Assert.False(recap.Escaped);
        Assert.Equal(room.FailedText, recap.EndText);
        Assert.Equal(0, recap.SecondsLeft);
        Assert.Equal(2, recap.Stages.Count);
        Assert.NotNull(recap.Stages[0].ClearedAt);
        Assert.Null(recap.Stages[1].ClearedAt);
        Assert.All(recap.Stages[1].Puzzles, p => Assert.Null(p.SolvedAt));
        Assert.Equal(room.Stages.Count, recap.StageCount); // "2 of 3" is fine: the TV showed the count all game
    }

    [Fact]
    public void There_is_no_recap_until_the_game_is_over()
    {
        var template = Rooms.Get("the-workshop");
        Assert.Throws<GameRuleException>(() => EscapeProjector.Recap(EscapeEngine.NewGame(1), template));
        Assert.Throws<GameRuleException>(() => EscapeProjector.Recap(Started(template, 1), template));
    }

    [Fact]
    public void The_cover_and_the_game_masters_lines_come_along()
    {
        var template = Rooms.Get("the-workshop");
        var s = EscapeEngine.NewGame(5, ai: new EscapeAiFeatures { GameMaster = true });
        for (var i = 0; i < Seats.Length; i++) s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, Seats[i], $"P{i}", i == 0, false));
        s = EscapeEngine.Apply(s, template, new StartEscape(T0));
        s = EscapeBot.PlayToEnd(s, template, Seats, T0);
        // The state keeps only the latest moments, so the line for the escape itself is the one that's sure to be there.
        s = EscapeEngine.Apply(s, template, new SetCueNarration(s.EndedAt!.Value, s.Cues[^1].Id, "Well, well. Out already."));

        var recap = EscapeProjector.Recap(s, template, new Dictionary<string, string> { [EscapeArt.Cover] = "/media/assets/cover" });

        Assert.Equal("/media/assets/cover", recap.CoverUrl);
        Assert.Equal(template.Host.Name, recap.GameMasterName);
        Assert.Equal(["Well, well. Out already."], recap.GameMasterLines);
    }

    public static TheoryData<string, int> RoomsAndLengths()
    {
        var data = new TheoryData<string, int>();
        foreach (var room in Rooms.Library)
            foreach (var minutes in room.PlayableLengths) data.Add(room.Id, minutes);
        return data;
    }

    /// <summary>
    /// A shared recap can reach people who haven't played the room yet. Whether the group escaped (with a hint
    /// taken, so a paid hint was on the TV) or was trapped, the recap holds no answer, prompt, hint, solved text
    /// or clue piece, and nothing from a stage the group never reached.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoomsAndLengths))]
    public void A_recap_never_spoils_the_room(string id, int minutes)
    {
        var template = Rooms.Get(id);
        foreach (var seed in new long[] { 11, 2024, 987654 })
        {
            var s = Started(template, seed, minutes);
            var room = EscapeEngine.RoomFor(s, template);
            s = EscapeEngine.Apply(s, template, new RequestEscapeHint(T0.AddSeconds(1), Seats[0], room.Stages[0].Puzzles[0]));
            AssertNoSpoilers(EscapeBot.PlayToEnd(s, template, Seats, T0.AddSeconds(1)), template);

            if (room.Stages.Count > 1)
            {
                var trapped = EscapeEngine.Apply(Started(template, seed, minutes), template,
                    new EscapeTick(T0.AddMinutes(minutes).AddSeconds(1))); // out of time in the very first stage
                Assert.Equal(EscapePhase.Failed, trapped.Phase);
                AssertNoSpoilers(trapped, template);
            }
        }
    }

    private static void AssertNoSpoilers(EscapeState s, EscapeRoom template)
    {
        var room = EscapeEngine.RoomFor(s, template);
        var json = ViewText.Decoded(GameJson.Serialize(EscapeProjector.Recap(s, template)));
        var reached = room.Stages.Take(s.StageIndex + 1).ToList();

        foreach (var p in room.Puzzles)
        {
            foreach (var answer in p.Answers.Where(a => a.Length >= 3))
                Assert.False(json.Contains($"\n{answer}\n", StringComparison.OrdinalIgnoreCase), $"answer of {p.Id} leaked");
            Assert.DoesNotContain(p.Prompt, json);
            foreach (var hint in p.Hints) Assert.DoesNotContain(hint, json);
            foreach (var piece in p.Pieces) Assert.DoesNotContain(piece, json);
            if (p.SolvedText.Length >= 12) Assert.DoesNotContain(p.SolvedText, json);
        }
        foreach (var stage in room.Stages.Except(reached))
        {
            Assert.DoesNotContain($"\n{stage.Title}\n", json);
            foreach (var p in stage.Puzzles.Select(id => room.FindPuzzle(id)!).Where(p => reached.All(r => !r.Puzzles.Contains(p.Id))))
                Assert.DoesNotContain($"\n{p.Title}\n", json);
        }
        foreach (var o in room.SceneObjects)
        {
            if (o.Look.Length >= 12) Assert.DoesNotContain(o.Look, json);
            if (o.Clue is { Length: >= 12 } clue) Assert.DoesNotContain(clue, json);
        }
    }
}
