using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using ButlerDidIt.Game.Engine;

namespace ButlerDidIt.Escape.Tests;

/// <summary>The rules behind the AI game master: the moments it reacts to, and AI hints that are paid for once and checked by the engine.</summary>
public class GameMasterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ada = Guid.NewGuid(), Ben = Guid.NewGuid();
    private static EscapeRoom Workshop => Rooms.Get("the-workshop");
    private static readonly EscapeAiFeatures AllOn = new() { GameMaster = true, Hints = true, Voice = true };

    private static EscapeState Started(EscapeAiFeatures? ai, EscapeRoom? room = null, long seed = 7)
    {
        room ??= Workshop;
        var s = EscapeEngine.NewGame(seed, ai: ai);
        s = EscapeEngine.Apply(s, room, new AddEscapePlayer(T0, Ada, "Ada", true, false));
        s = EscapeEngine.Apply(s, room, new AddEscapePlayer(T0, Ben, "Ben", false, false));
        return EscapeEngine.Apply(s, room, new StartEscape(T0));
    }

    /// <summary>The first puzzle the group can answer straight away, as this puzzle set plays it.</summary>
    private static EscapePuzzle Answerable(EscapeState s, EscapeRoom? room = null)
    {
        var concrete = EscapeEngine.RoomFor(s, room ?? Workshop);
        return concrete.Stages[s.StageIndex].Puzzles.Select(id => concrete.FindPuzzle(id)!)
            .First(p => p.Kind != PuzzleKind.Use && p.Requires.Count == 0 && !s.IsSolved(p.Id));
    }

    /// <summary>Plays (searching, looking and combining as needed) until one more puzzle is solved.</summary>
    private static EscapeState SolveNext(EscapeState s, DateTimeOffset at)
    {
        var solved = s.Solved.Count;
        while (s.Phase == EscapePhase.Playing && s.Solved.Count == solved)
            s = EscapeEngine.Apply(s, Workshop, EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, Workshop), Ben, at));
        return s;
    }

    private static EscapePuzzleView View(EscapeState s, string puzzleId, DateTimeOffset now) =>
        EscapeProjector.Stage(s, Workshop, now).Puzzles.Single(p => p.Id == puzzleId);

    // ------------------------------------------------------------------ moments

    [Fact]
    public void Without_the_game_master_nothing_is_recorded_and_the_clock_works_as_before()
    {
        var s = Started(null);
        for (var i = 0; s.Phase == EscapePhase.Playing; i++) s = SolveNext(s, T0.AddMinutes(i + 1));
        Assert.Empty(s.Cues);
        Assert.Null(EscapeProjector.Stage(s, Workshop, T0).GameMaster);

        var fresh = Started(null);
        Assert.Equal(fresh.Deadline, EscapeEngine.NextDueAt(fresh));
    }

    [Fact]
    public void Every_moment_gets_one_cue_saying_what_happened()
    {
        var s = Started(AllOn);
        Assert.Equal([CueKind.Start], s.Cues.Select(c => c.Kind));

        // The state keeps only the latest few moments, so collect them after every move: one search can be a
        // moment of its own (an empty one costs time on Normal, #132), and a solve can take several moves.
        var seen = new Dictionary<int, EscapeCue>();
        for (var i = 0; s.Phase == EscapePhase.Playing; i++)
        {
            var solved = s.Solved.Count;
            while (s.Phase == EscapePhase.Playing && s.Solved.Count == solved)
            {
                s = EscapeEngine.Apply(s, Workshop, EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, Workshop), Ben, T0.AddMinutes(i + 1)));
                foreach (var cue in s.Cues) seen.TryAdd(cue.Id, cue);
            }
        }
        var cues = seen.Values.OrderBy(c => c.Id).ToList();
        var kinds = cues.Select(c => c.Kind).ToList();

        // The last moment is the escape itself, not the solve that caused it.
        Assert.Equal(CueKind.Escaped, kinds[^1]);
        Assert.Equal(Workshop.Stages.Count - 1, kinds.Count(k => k == CueKind.StageOpened));
        Assert.Contains(CueKind.Found, kinds); // searching turned things up
        Assert.DoesNotContain(CueKind.Failed, kinds);
        var opened = cues.First(c => c.Kind == CueKind.StageOpened);
        Assert.Equal("Ben", opened.PlayerName);
        Assert.Equal(Workshop.Stages[1].Title, opened.StageTitle);
        Assert.Equal(cues.Select(c => c.Id), Enumerable.Range(1, cues.Count)); // ids only go up, none skipped
    }

    [Fact]
    public void Three_wrong_answers_in_a_row_make_the_game_master_comment()
    {
        var s = Started(AllOn);
        var puzzle = Answerable(s);
        for (var i = 0; i < 3; i++) s = EscapeEngine.Apply(s, Workshop, new SubmitAnswer(T0.AddSeconds(10 * (i + 1)), Ada, puzzle.Id, "wrong" + i));

        var cue = Assert.Single(s.Cues, c => c.Kind == CueKind.WrongStreak);
        Assert.Equal("Ada", cue.PlayerName);
        Assert.Equal(["wrong0", "wrong1", "wrong2"], s.RecentWrong[puzzle.Id]);
        Assert.Equal(0, s.WrongStreak);
    }

    [Fact]
    public void The_game_master_warns_five_minutes_before_the_end_exactly_once()
    {
        var s = Started(AllOn);
        var warning = s.Deadline!.Value - EscapeEngine.LowTimeWarning;
        Assert.Equal(warning, EscapeEngine.NextDueAt(s));
        Assert.Same(s, EscapeEngine.Apply(s, Workshop, new EscapeTick(warning.AddSeconds(-1))));

        var warned = EscapeEngine.Apply(s, Workshop, new EscapeTick(warning));
        Assert.Equal(CueKind.LowTime, warned.Cues[^1].Kind);
        Assert.Equal(EscapePhase.Playing, warned.Phase);
        Assert.Equal(warned.Deadline, EscapeEngine.NextDueAt(warned));
        Assert.Same(warned, EscapeEngine.Apply(warned, Workshop, new EscapeTick(warning.AddMinutes(1))));

        var over = EscapeEngine.Apply(warned, Workshop, new EscapeTick(warned.Deadline!.Value));
        Assert.Equal([CueKind.Start, CueKind.LowTime, CueKind.Failed], over.Cues.Select(c => c.Kind));
    }

    [Fact]
    public void Lines_and_recordings_are_shown_and_older_waiting_moments_are_passed_over()
    {
        var s = Started(AllOn);
        s = SolveNext(s, T0.AddMinutes(1));
        var newest = s.Cues[^1].Id;

        s = EscapeEngine.Apply(s, Workshop, new SkipCues(T0.AddMinutes(1), newest));
        Assert.True(s.Cues.Single(c => c.Kind == CueKind.Start).Skipped);
        s = EscapeEngine.Apply(s, Workshop, new SetCueNarration(T0.AddMinutes(1), newest, "  Well done, Ben.  "));
        s = EscapeEngine.Apply(s, Workshop, new SetCueAudio(T0.AddMinutes(1), newest, "/media/line.mp3"));

        var line = Assert.Single(EscapeProjector.Stage(s, Workshop, T0).Narration);
        Assert.Equal(("Well done, Ben.", "/media/line.mp3"), (line.Text, line.AudioUrl));

        // A second line for the same moment, or a late recording for a skipped one, changes nothing.
        Assert.Same(s, EscapeEngine.Apply(s, Workshop, new SetCueNarration(T0, newest, "again")));
        Assert.Same(s, EscapeEngine.Apply(s, Workshop, new SetCueAudio(T0, s.Cues[0].Id, "/media/late.mp3")));
    }

    [Fact]
    public void A_line_and_its_recording_can_arrive_together_so_the_words_never_run_ahead_of_the_voice()
    {
        var s = Started(AllOn);
        var start = s.Cues.Single(c => c.Kind == CueKind.Start).Id;
        var line = new string('a', EscapeEngine.MaxAiText + 50);

        s = EscapeEngine.Apply(s, Workshop, new SetCueNarration(T0, start, line, "/media/welcome.mp3"));

        var shown = Assert.Single(EscapeProjector.Stage(s, Workshop, T0).Narration);
        Assert.Equal("/media/welcome.mp3", shown.AudioUrl);
        // Cut exactly as the server cuts what it records, so the voice says what the panel shows.
        Assert.Equal(EscapeEngine.CutLine(line), shown.Text);
        Assert.Equal(EscapeEngine.MaxAiText + 1, shown.Text.Length);
    }

    // ------------------------------------------------------------------ AI hints

    [Fact]
    public void An_ai_hint_is_paid_for_at_once_and_shows_when_it_arrives()
    {
        var s = Started(AllOn);
        var puzzle = Answerable(s);
        var id = Guid.NewGuid();
        var deadline = s.Deadline!.Value;

        s = EscapeEngine.Apply(s, Workshop, new BeginEscapeHint(T0.AddMinutes(1), id, Ada, puzzle.Id));
        Assert.Equal(deadline.AddSeconds(-Workshop.HintPenaltySeconds), s.Deadline);
        var waiting = View(s, puzzle.Id, T0.AddMinutes(1));
        Assert.True(waiting.HintPending);
        Assert.Empty(waiting.Hints);
        Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new BeginEscapeHint(T0.AddMinutes(1), Guid.NewGuid(), Ben, puzzle.Id)));

        s = EscapeEngine.Apply(s, Workshop, new CompleteEscapeHint(T0.AddMinutes(1), id, "Listen to what the room is ticking about."));
        var shown = View(s, puzzle.Id, T0.AddMinutes(1));
        Assert.False(shown.HintPending);
        Assert.Equal(["Listen to what the room is ticking about."], shown.Hints);
        Assert.Equal(1, s.HintsUsed);
    }

    [Fact]
    public void An_ai_hint_that_gives_the_answer_away_is_replaced_by_the_written_hint()
    {
        var s = Started(AllOn);
        var puzzle = Answerable(s);
        var id = Guid.NewGuid();
        s = EscapeEngine.Apply(s, Workshop, new BeginEscapeHint(T0, id, Ada, puzzle.Id));
        s = EscapeEngine.Apply(s, Workshop, new CompleteEscapeHint(T0, id, $"Just type {puzzle.Answers[0]}, obviously."));

        Assert.Equal([puzzle.Hints[0]], View(s, puzzle.Id, T0).Hints);
    }

    [Fact]
    public void A_failed_or_lost_ai_hint_falls_back_to_the_written_hint_and_is_never_charged_twice()
    {
        var s = Started(AllOn);
        var puzzle = Answerable(s);
        var cancelled = Guid.NewGuid();
        s = EscapeEngine.Apply(s, Workshop, new BeginEscapeHint(T0, cancelled, Ada, puzzle.Id));
        s = EscapeEngine.Apply(s, Workshop, new CancelEscapeHint(T0, cancelled));
        Assert.Equal([puzzle.Hints[0]], View(s, puzzle.Id, T0).Hints);
        Assert.Same(s, EscapeEngine.Apply(s, Workshop, new CompleteEscapeHint(T0, cancelled, "too late")));

        // A server that died mid-call leaves the hint "on its way"; after the timeout it's the written one, and the next can be asked for.
        var lost = Guid.NewGuid();
        s = EscapeEngine.Apply(s, Workshop, new BeginEscapeHint(T0, lost, Ada, puzzle.Id));
        var later = T0 + EscapeEngine.AiHintTimeout;
        Assert.Equal(puzzle.Hints.Take(2), View(s, puzzle.Id, later).Hints);
        Assert.Equal(2, s.HintsUsed);
    }

    [Fact]
    public void Ai_hints_need_the_feature_and_follow_the_same_limits_as_written_ones()
    {
        var plain = Started(null);
        var puzzle = Answerable(plain);
        Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(plain, Workshop, new BeginEscapeHint(T0, Guid.NewGuid(), Ada, puzzle.Id)));

        var s = Started(AllOn);
        for (var i = 0; i < puzzle.Hints.Count; i++)
        {
            var id = Guid.NewGuid();
            s = EscapeEngine.Apply(s, Workshop, new BeginEscapeHint(T0, id, Ada, puzzle.Id));
            s = EscapeEngine.Apply(s, Workshop, new CompleteEscapeHint(T0, id, $"Nudge {i}."));
        }
        Assert.Throws<GameRuleException>(() => EscapeEngine.Apply(s, Workshop, new BeginEscapeHint(T0, Guid.NewGuid(), Ada, puzzle.Id)));
    }
}

public class HintGuardTests
{
    private static EscapePuzzle Puzzle(PuzzleKind kind, params string[] answers) =>
        new() { Id = "p", Title = "P", Kind = kind, Prompt = "?", SolvedText = "!", Answers = [.. answers], Hints = ["h"] };

    [Theory]
    [InlineData("The code is 4729.")]
    [InlineData("Try 4-7-2-9.")]
    [InlineData("four, seven, two, nine")]
    [InlineData("Start with 4, then 7, then 2 and finally 9.")]
    public void A_code_is_caught_however_it_is_written(string hint) => Assert.True(EscapeHintGuard.Leaks(hint, Puzzle(PuzzleKind.Code, "4729")));

    [Theory]
    [InlineData("It's a SHADOW.")]
    [InlineData("Think of shadows at noon.")]
    [InlineData("Say: the ombré shadow!")]
    public void A_word_is_caught_in_any_case_or_plural(string hint) => Assert.True(EscapeHintGuard.Leaks(hint, Puzzle(PuzzleKind.Text, "shadow")));

    [Fact]
    public void Phrases_accents_and_glued_words_are_caught()
    {
        Assert.True(EscapeHintGuard.Leaks("Open Sésame, as they say.", Puzzle(PuzzleKind.Text, "open sesame")));
        Assert.True(EscapeHintGuard.Leaks("Try opensesame.", Puzzle(PuzzleKind.Text, "open sesame")));
        Assert.True(EscapeHintGuard.Leaks("It's the clock.", Puzzle(PuzzleKind.Text, "the clock")));
    }

    [Theory]
    [InlineData("Count the lamps on the wall and read them left to right.")]
    [InlineData("There are 3 phones with clues: read yours out.")]
    [InlineData("What follows you everywhere but never speaks?")]
    public void An_innocent_nudge_passes(string hint)
    {
        Assert.False(EscapeHintGuard.Leaks(hint, Puzzle(PuzzleKind.Code, "4729")));
        Assert.False(EscapeHintGuard.Leaks(hint, Puzzle(PuzzleKind.Text, "shadow")));
    }

    [Fact]
    public void Short_codes_only_count_as_a_number_of_their_own()
    {
        Assert.True(EscapeHintGuard.Leaks("It's 7.", Puzzle(PuzzleKind.Code, "7")));
        Assert.False(EscapeHintGuard.Leaks("Look at the 1970s poster.", Puzzle(PuzzleKind.Code, "7")));
    }

    [Fact]
    public void Empty_or_rambling_hints_are_refused()
    {
        Assert.True(EscapeHintGuard.Rejects("   ", Puzzle(PuzzleKind.Text, "shadow")));
        Assert.True(EscapeHintGuard.Rejects(new string('a', EscapeEngine.MaxAiText + 1), Puzzle(PuzzleKind.Text, "shadow")));
    }
}
