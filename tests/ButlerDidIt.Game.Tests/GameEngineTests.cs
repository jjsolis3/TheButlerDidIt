using ButlerDidIt.Game.Engine;
using static ButlerDidIt.Game.Tests.TestScenario;

namespace ButlerDidIt.Game.Tests;

public class GameEngineTests
{
    [Fact]
    public void Starting_assigns_characters_and_makes_unfilled_required_characters_npcs()
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new StartGame(T0));

        Assert.Equal(Phase.CastReveal, s.Phase);
        Assert.All(s.Players, p => Assert.NotNull(p.CharacterId));
        // Required characters are handed out first, so nobody ends up as the optional guest.
        Assert.DoesNotContain(s.Players, p => p.CharacterId == "guest");
        // Three required characters, two players: exactly one NPC.
        Assert.Single(s.NpcCharacterIds);
        Assert.DoesNotContain("guest", s.NpcCharacterIds);
    }

    [Fact]
    public void While_the_ai_tailors_the_mystery_the_cast_is_frozen()
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new BeginTailoring(T0));

        Assert.True(ViewProjector.Stage(s, scenario, T0).Tailoring);
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "cook")));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new AddPlayer(T0, Cara, "Cara", false, false)));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new AutoAssignCharacters(T0)));

        // Cancelling unfreezes the lobby; starting ends the tailoring too.
        Assert.False(GameEngine.Apply(s, scenario, new CancelTailoring(T0)).Tailoring);
        var started = GameEngine.Apply(s, scenario, new StartGame(T0));
        Assert.False(started.Tailoring);
        Assert.Equal(Phase.CastReveal, started.Phase);
    }

    [Fact]
    public void Apply_does_not_mutate_the_input_state()
    {
        var scenario = Create();
        var original = GameEngine.NewGame();
        var next = GameEngine.Apply(original, scenario, new AddPlayer(T0, Alice, "Alice", true, false));

        Assert.Empty(original.Players);
        Assert.Equal(0, original.Version);
        Assert.Single(next.Players);
        Assert.Equal(1, next.Version);
    }

    [Fact]
    public void Rule_violations_throw_and_leave_state_untouched()
    {
        var (s, scenario) = StartedGame();
        var before = GameJson.Serialize(s);

        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new AddPlayer(T0, Guid.NewGuid(), "Late", false, false)));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SubmitAccusation(T0, Alice, "cook", "money", "poison")));
        Assert.Equal(before, GameJson.Serialize(s));
    }

    [Fact]
    public void Two_players_cannot_take_the_same_character()
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "cook"));

        var ex = Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Bob, "cook")));
        Assert.Contains("Alice", ex.Message);
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var scenario = Create();
        var s = GameEngine.Apply(GameEngine.NewGame(), scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, " alice ", false, false)));
    }

    [Fact]
    public void Full_flow_walks_every_phase_in_order()
    {
        var (s, scenario) = StartedGame();
        var phases = new List<(Phase, int, ActStep)>();
        while (s.Phase != Phase.Finished)
        {
            phases.Add((s.Phase, s.ActIndex, s.ActStep));
            s = GameEngine.Apply(s, scenario, new Advance(T0));
        }

        Assert.Equal(
        [
            (Phase.CastReveal, -1, ActStep.Cinematic),
            (Phase.Prologue, -1, ActStep.Cinematic),
            (Phase.Act, 0, ActStep.Cinematic),
            (Phase.Act, 0, ActStep.Mingle),
            (Phase.Act, 1, ActStep.Cinematic),
            (Phase.Act, 1, ActStep.Mingle),
            (Phase.Accusation, 1, ActStep.Cinematic),
            // Reveal: guesses, unmasking, 2 explanation paragraphs, scores = 5 steps
            (Phase.Reveal, 1, ActStep.Cinematic),
            (Phase.Reveal, 1, ActStep.Cinematic),
            (Phase.Reveal, 1, ActStep.Cinematic),
            (Phase.Reveal, 1, ActStep.Cinematic),
            (Phase.Reveal, 1, ActStep.Cinematic),
            (Phase.Awards, 1, ActStep.Cinematic),
        ], phases);
    }

    [Fact]
    public void Mingle_drops_start_clues_and_routes_private_clues_to_the_recipient()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3); // cast → prologue → act1 cinematic → act1 mingle

        Assert.Equal(ActStep.Mingle, s.ActStep);
        Assert.Equal(T0.AddMinutes(20), s.Timer.EndsAt);
        Assert.Contains(s.DroppedClues, d => d.ClueId == "c1" && d.RecipientSeatId is null);
        Assert.Contains(s.DroppedClues, d => d.ClueId == "c2" && d.RecipientSeatId == Alice); // Alice plays the maid
        Assert.Equal(["c3"], s.PendingClueIds);
    }

    [Fact]
    public void Midway_clues_drop_when_the_ticker_reaches_half_time()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3);

        var early = GameEngine.Apply(s, scenario, new Tick(T0.AddMinutes(9)));
        Assert.Same(s, early); // nothing due: same object back, nothing to save

        var onTime = GameEngine.Apply(s, scenario, new Tick(T0.AddMinutes(10)));
        Assert.Contains(onTime.DroppedClues, d => d.ClueId == "c3");
        Assert.Empty(onTime.PendingClueIds);
        Assert.Null(GameEngine.NextDueAt(onTime));
    }

    [Fact]
    public void Host_can_drop_the_next_clue_early()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3);
        s = GameEngine.Apply(s, scenario, new DropNextClue(T0.AddMinutes(1)));

        Assert.Contains(s.DroppedClues, d => d.ClueId == "c3");
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new DropNextClue(T0.AddMinutes(2))));
    }

    [Fact]
    public void Leaving_an_act_drops_any_clues_still_pending()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 4); // into act 2 cinematic without waiting for the midway drop

        Assert.Contains(s.DroppedClues, d => d.ClueId == "c3");
    }

    [Fact]
    public void Private_clue_for_an_absent_character_becomes_public()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 5); // act 2 mingle; c5 is addressed to the absent guest

        var c5 = Assert.Single(s.DroppedClues, d => d.ClueId == "c5");
        Assert.Null(c5.RecipientSeatId);
    }

    [Fact]
    public void Pause_and_resume_preserve_remaining_time()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3); // 20 minute timer starting at T0

        s = GameEngine.Apply(s, scenario, new PauseTimer(T0.AddMinutes(5)));
        Assert.True(s.Timer.IsPaused);
        Assert.Null(GameEngine.NextDueAt(s)); // paused games are not ticked

        s = GameEngine.Apply(s, scenario, new ResumeTimer(T0.AddMinutes(30)));
        Assert.Equal(T0.AddMinutes(45), s.Timer.EndsAt); // 15 minutes were left
        Assert.Equal(T0.AddMinutes(35), s.Timer.MidwayAt); // midway was 5 minutes away
    }

    [Fact]
    public void Extending_an_expired_timer_counts_from_now()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3);
        s = GameEngine.Apply(s, scenario, new ExtendTimer(T0.AddMinutes(25), 5));
        Assert.Equal(T0.AddMinutes(30), s.Timer.EndsAt);
    }

    [Fact]
    public void Secrets_unlock_by_act_and_can_be_revealed_once()
    {
        var (s, scenario) = StartedGame();
        // Cara plays the butler, whose second secret unlocks in act 2.
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new RevealSecret(T0, Cara, "butler-s2")));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new RevealSecret(T0, Cara, "maid-s1")));

        s = GameEngine.Apply(s, scenario, new RevealSecret(T0, Cara, "butler-s1"));
        Assert.Single(s.RevealedSecrets);

        s = AdvanceTimes(s, scenario, 4); // act 2 cinematic
        s = GameEngine.Apply(s, scenario, new RevealSecret(T0, Cara, "butler-s2"));
        Assert.Equal(2, s.RevealedSecrets.Count);
    }

    [Fact]
    public void Puzzle_accepts_answers_ignoring_case_and_punctuation()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 5); // act 2 mingle, cipher clue c4 is public

        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SolvePuzzle(T0, Bob, "c4", "a watch")));
        s = GameEngine.Apply(s, scenario, new SolvePuzzle(T0, Bob, "c4", "  A Clock! "));
        Assert.Single(s.SolvedPuzzles);
    }

    [Fact]
    public void Players_can_only_share_their_own_private_clues()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3);

        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new ShareClue(T0, Bob, "c2")));
        s = GameEngine.Apply(s, scenario, new ShareClue(T0, Alice, "c2"));
        Assert.True(s.DroppedClues.Single(d => d.ClueId == "c2").SharedPublicly);
    }

    [Fact]
    public void Converting_a_player_to_npc_hands_their_clues_to_the_table()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3);
        s = GameEngine.Apply(s, scenario, new ConvertToNpc(T0, Alice));

        Assert.Null(s.FindPlayer(Alice));
        Assert.Contains("maid", s.NpcCharacterIds);
        Assert.Null(s.DroppedClues.Single(d => d.ClueId == "c2").RecipientSeatId);
    }

    [Fact]
    public void Scoring_rewards_detectives_and_a_murderer_who_fools_people()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 5);
        s = GameEngine.Apply(s, scenario, new SolvePuzzle(T0, Cara, "c4", "clock"));
        s = AdvanceTimes(s, scenario, 1); // accusation
        s = GameEngine.Apply(s, scenario, new SubmitAccusation(T0, Alice, "cook", "money", "knife"));
        s = GameEngine.Apply(s, scenario, new SubmitAccusation(T0, Cara, "maid", "love", "poison"));
        s = GameEngine.Apply(s, scenario, new SubmitAccusation(T0, Bob, "butler", "love", "knife"));

        var scores = Scoring.Compute(s, scenario).ToDictionary(x => x.SeatId, x => x.Points);
        Assert.Equal(3 + 1, scores[Alice]); // killer + motive
        Assert.Equal(1 + 1, scores[Cara]); // method + puzzle
        Assert.Equal(1, scores[Bob]); // the murderer fooled Cara only
    }

    [Fact]
    public void Award_votes_cannot_be_for_yourself_and_ties_share_the_award()
    {
        var (s, scenario) = StartedGame();
        while (s.Phase != Phase.Awards) s = GameEngine.Apply(s, scenario, new Advance(T0));

        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new CastAwardVote(T0, Alice, "costume", Alice)));
        s = GameEngine.Apply(s, scenario, new CastAwardVote(T0, Alice, "costume", Bob));
        s = GameEngine.Apply(s, scenario, new CastAwardVote(T0, Bob, "costume", Cara));

        var costume = Scoring.TallyAwards(s).Single(a => a.AwardId == "costume");
        Assert.Equal(["Bob", "Cara"], costume.Winners);
    }
}
