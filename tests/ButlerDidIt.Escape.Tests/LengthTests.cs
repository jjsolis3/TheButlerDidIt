using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;

namespace ButlerDidIt.Escape.Tests;

/// <summary>Rooms in several lengths: a shorter game is a shorter room, and every length is proven escapable.</summary>
public class LengthTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static EscapeRoom Workshop => Rooms.Get("the-workshop");

    /// <summary>A private copy to break, so the shared library stays untouched.</summary>
    private static EscapeRoom Copy(EscapeRoom room) => GameJson.Deserialize<EscapeRoom>(GameJson.Serialize(room));

    private static EscapeRoom WithPuzzle(EscapeRoom room, string id, Func<EscapePuzzle, EscapePuzzle> change) =>
        room with { Puzzles = room.Puzzles.Select(p => p.Id == id ? change(p) : p).ToList() };

    [Fact]
    public void A_shorter_game_leaves_out_the_longer_games_puzzles_and_sets_the_clock()
    {
        var room = RoomVariants.Build(Workshop, 3);
        var thirty = RoomLengths.Cut(room, 30);
        Assert.Equal(30, thirty.TimeLimitMinutes);
        Assert.DoesNotContain(thirty.Puzzles, p => p.Id is "cabinet" or "fusebox" or "doll");
        Assert.DoesNotContain(thirty.Stages.SelectMany(s => s.Puzzles), id => id is "cabinet" or "fusebox" or "doll");

        var sixty = RoomLengths.Cut(room, 60);
        Assert.Equal(room.Puzzles.Count(p => p.MinDifficulty is null), sixty.Puzzles.Count); // everything but Hard's extras, the doll included

        // The room's own length (and parties from before lengths, with none recorded) skips only the longer game's extras.
        var standard = RoomLengths.Cut(room, null);
        Assert.Equal(45, standard.TimeLimitMinutes);
        Assert.Contains(standard.Puzzles, p => p.Id == "cabinet");
        Assert.DoesNotContain(standard.Puzzles, p => p.Id == "doll");
        Assert.Same(standard, RoomLengths.Cut(room, 45));
    }

    [Fact]
    public void A_stage_left_with_no_puzzles_is_skipped()
    {
        var room = Copy(Workshop);
        var workbench = room.Stages.Single(s => s.Id == "workbench").Puzzles.ToHashSet();
        room = room with { Puzzles = room.Puzzles.Select(p => workbench.Contains(p.Id) ? p with { MinMinutes = 60 } : p).ToList() };
        Assert.DoesNotContain(RoomLengths.Cut(room, 45).Stages, s => s.Id == "workbench");
    }

    [Fact]
    public void The_game_plays_its_length_from_start_to_finish()
    {
        var s = EscapeEngine.NewGame(5, minutes: 30);
        var ada = Guid.NewGuid();
        s = EscapeEngine.Apply(s, Workshop, new AddEscapePlayer(T0, ada, "Ada", true, false));
        s = EscapeEngine.Apply(s, Workshop, new StartEscape(T0));
        Assert.Equal(T0.AddMinutes(30), s.Deadline);
        var view = EscapeProjector.Stage(s, Workshop, T0);
        Assert.Equal(30, view.TimeLimitMinutes);
        // While it plays, the count is what the group has found so far (#134): the first stage's puzzles in sight.
        var played = RoomLengths.Cut(RoomVariants.Build(Workshop, 5), 30);
        Assert.Equal(EscapeEngine.InSight(s, played).Count(), view.PuzzleCount);
        Assert.True(view.PuzzleCount < played.Stages[0].Puzzles.Count, "the locker behind the drums is still to be found");
        // A puzzle this length leaves out can't be tried.
        Assert.Throws<ButlerDidIt.Game.Engine.GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0, ada, "cabinet", "x")));
        // Once it's over, the whole game's count: the puzzles this length plays.
        s = EscapeEngine.Apply(s, Workshop, new EscapeTick(T0.AddMinutes(30)));
        Assert.Equal(played.Puzzles.Count, EscapeProjector.Stage(s, Workshop, T0).PuzzleCount);
    }

    [Fact]
    public void A_shorter_game_that_still_needs_a_key_from_a_left_out_puzzle_is_caught()
    {
        // The tape gives the rusty key the shackles need (once it's oiled); keeping the shackles but not the tape at 30 minutes breaks the room.
        var room = WithPuzzle(Copy(Workshop), "tape", p => p with { MinMinutes = 45 });
        var errors = EscapeRoomValidator.Validate(room);
        Assert.Contains(errors, e => e.StartsWith("At 30 minutes:") && e.Contains("'shackles'") && e.Contains("oiled-key"));
    }

    [Fact]
    public void Lengths_and_kept_puzzles_must_make_sense()
    {
        Assert.Contains(EscapeRoomValidator.Validate(Copy(Workshop) with { Lengths = [20, 45] }), e => e.Contains("not 20"));
        Assert.Contains(EscapeRoomValidator.Validate(Copy(Workshop) with { Lengths = [30, 60] }), e => e.Contains("must include the room's time limit"));
        Assert.Contains(EscapeRoomValidator.Validate(WithPuzzle(Copy(Workshop), "doll", p => p with { MinMinutes = 50 })),
            e => e.Contains("'doll'") && e.Contains("50-minute"));

        var tooShort = Copy(Workshop);
        tooShort = tooShort with { Puzzles = tooShort.Puzzles.Select(p => p.Id is "tape" or "shackles" ? p : p with { MinMinutes = 45 }).ToList() };
        Assert.Contains(EscapeRoomValidator.Validate(tooShort), e => e.Contains("keeps only 2 puzzles"));
    }

    [Fact]
    public void A_room_without_lengths_has_one_its_time_limit()
    {
        var room = Copy(Workshop) with { Lengths = [], Puzzles = Workshop.Puzzles.Select(p => p with { MinMinutes = null }).ToList() };
        Assert.Equal([45], room.PlayableLengths);
        Assert.Empty(EscapeRoomValidator.Validate(room));
    }
}
