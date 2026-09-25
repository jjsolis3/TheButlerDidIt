using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using static ButlerDidIt.Game.Tests.TestScenario;

namespace ButlerDidIt.Game.Tests;

/// <summary>Rules for the AI-assisted actions: NPC questions, hints and verdicts.</summary>
public class AiFeatureTests
{
    /// <summary>Alice (maid) and Bob (cook, the murderer) play; the butler becomes an NPC.</summary>
    private static (GameState, Scenario) TwoPlayerGameInAct1(bool ai = true)
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new SetAiFeatures(T0, new AiFeatures { NpcQuestions = ai, Hints = ai, Verdicts = ai, QuestionsPerAct = 2 }));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "maid"));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Bob, "cook"));
        s = GameEngine.Apply(s, scenario, new StartGame(T0));
        s = AdvanceTimes(s, scenario, 3); // act 1 mingle
        return (s, scenario);
    }

    [Fact]
    public void Npc_questions_follow_the_rules_and_limits()
    {
        var (s, scenario) = TwoPlayerGameInAct1();
        Assert.Contains("butler", s.NpcCharacterIds);

        // Only NPCs can be questioned, not characters a guest is playing.
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, Guid.NewGuid(), Alice, "cook", "Where were you?")));

        var q1 = Guid.NewGuid();
        s = GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, q1, Alice, "butler", "Where were you at nine?"));
        // One question at a time per NPC.
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, Guid.NewGuid(), Bob, "butler", "And you polished the silver?")));

        s = GameEngine.Apply(s, scenario, new CompleteNpcQuestion(T0, q1, "In the pantry, madam."));
        Assert.Equal("In the pantry, madam.", s.Interrogations.Single().Answer);
        Assert.Equal(1, GameEngine.QuestionsLeft(s, Alice));

        s = GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, Guid.NewGuid(), Alice, "butler", "Did you see the cook?"));
        Assert.Equal(0, GameEngine.QuestionsLeft(s, Alice));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, Guid.NewGuid(), Alice, "butler", "One more?")));
    }

    [Fact]
    public void A_failed_ai_call_gives_the_question_back()
    {
        var (s, scenario) = TwoPlayerGameInAct1();
        var id = Guid.NewGuid();
        s = GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, id, Alice, "butler", "Where were you at nine?"));
        s = GameEngine.Apply(s, scenario, new CancelNpcQuestion(T0, id));
        Assert.Empty(s.Interrogations);
        Assert.Equal(2, GameEngine.QuestionsLeft(s, Alice));
    }

    [Fact]
    public void Question_limit_resets_each_act()
    {
        var (s, scenario) = TwoPlayerGameInAct1();
        foreach (var q in new[] { "First question?", "Second question?" })
        {
            var id = Guid.NewGuid();
            s = GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, id, Alice, "butler", q));
            s = GameEngine.Apply(s, scenario, new CompleteNpcQuestion(T0, id, "Answer."));
        }
        Assert.Equal(0, GameEngine.QuestionsLeft(s, Alice));

        s = GameEngine.Apply(s, scenario, new Advance(T0)); // act 2 cinematic
        Assert.Equal(2, GameEngine.QuestionsLeft(s, Alice));
    }

    [Fact]
    public void Features_are_off_unless_switched_on()
    {
        var (s, scenario) = TwoPlayerGameInAct1(ai: false);
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, Guid.NewGuid(), Alice, "butler", "Hello there?")));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new BeginHint(T0, Guid.NewGuid(), Alice)));
        Assert.Equal(0, ViewProjector.Player(s, scenario, Alice, T0).QuestionsLeft);
    }

    [Fact]
    public void Hints_are_limited_and_private()
    {
        var (s, scenario) = TwoPlayerGameInAct1();
        var id = Guid.NewGuid();
        s = GameEngine.Apply(s, scenario, new BeginHint(T0, id, Alice));
        s = GameEngine.Apply(s, scenario, new CompleteHint(T0, id, "SECRET_HINT_FOR_ALICE"));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new BeginHint(T0, Guid.NewGuid(), Alice)));

        Assert.Contains("SECRET_HINT_FOR_ALICE", GameJson.Serialize(ViewProjector.Player(s, scenario, Alice, T0)));
        Assert.DoesNotContain("SECRET_HINT_FOR_ALICE", GameJson.Serialize(ViewProjector.Player(s, scenario, Bob, T0)));
        Assert.DoesNotContain("SECRET_HINT_FOR_ALICE", GameJson.Serialize(ViewProjector.Stage(s, scenario, T0)));
    }

    [Fact]
    public void Interrogations_are_public()
    {
        var (s, scenario) = TwoPlayerGameInAct1();
        var id = Guid.NewGuid();
        s = GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, id, Alice, "butler", "Where were you?"));
        var pending = ViewProjector.Stage(s, scenario, T0).Interrogations.Single();
        Assert.Null(pending.Answer);
        Assert.Equal("Mr. Butler", pending.CharacterName);

        s = GameEngine.Apply(s, scenario, new CompleteNpcQuestion(T0, id, "In the pantry."));
        Assert.Contains("In the pantry.", GameJson.Serialize(ViewProjector.Player(s, scenario, Bob, T0)));
    }

    [Fact]
    public void Verdicts_appear_only_after_the_unmasking()
    {
        var (s, scenario) = TwoPlayerGameInAct1();
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SetVerdicts(T0, new Dictionary<Guid, string> { [Alice] = "x" })));

        while (s.Phase != Phase.Reveal) s = GameEngine.Apply(s, scenario, new Advance(T0));
        s = GameEngine.Apply(s, scenario, new SetVerdicts(T0, new Dictionary<Guid, string> { [Alice] = "VERDICT_FOR_ALICE" }));
        Assert.DoesNotContain("VERDICT_FOR_ALICE", GameJson.Serialize(ViewProjector.Stage(s, scenario, T0)));

        s = GameEngine.Apply(s, scenario, new Advance(T0)); // unmask
        var guess = ViewProjector.Stage(s, scenario, T0).Reveal!.Guesses.Single(g => g.PlayerName == "Alice");
        Assert.Equal("VERDICT_FOR_ALICE", guess.Verdict);
    }

    [Fact]
    public void Ai_answers_are_length_capped()
    {
        var (s, scenario) = TwoPlayerGameInAct1();
        var id = Guid.NewGuid();
        s = GameEngine.Apply(s, scenario, new BeginNpcQuestion(T0, id, Alice, "butler", "Tell me everything."));
        s = GameEngine.Apply(s, scenario, new CompleteNpcQuestion(T0, id, new string('a', 5000)));
        Assert.True(s.Interrogations.Single().Answer!.Length <= 2001);
    }
}
