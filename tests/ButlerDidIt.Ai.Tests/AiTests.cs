using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.Extensions.AI;

namespace ButlerDidIt.Ai.Tests;

public class GatewayTests
{
    [Fact]
    public async Task Records_usage_for_every_call()
    {
        var ai = new TestAi();
        var reply = await ai.Gateway().CompleteAsync(AiRole.Actor, "TASK: ping", [new ChatMessage(ChatRole.User, "hi")], TestAi.Context);

        Assert.Equal("OK", reply);
        var record = Assert.Single(ai.Usage);
        Assert.Equal(AiRole.Actor, record.Role);
        Assert.Equal(1000, record.InputTokens);
        Assert.True(record.Success);
    }

    [Fact]
    public async Task Budget_is_checked_before_calling()
    {
        var ai = new TestAi { OverBudget = true };
        await Assert.ThrowsAsync<AiBudgetExceededException>(() =>
            ai.Gateway().CompleteAsync(AiRole.Actor, "x", [new ChatMessage(ChatRole.User, "hi")], TestAi.Context));
        Assert.Empty(ai.Usage); // nothing was spent
    }

    [Fact]
    public async Task Unconfigured_role_gives_a_friendly_error()
    {
        var ai = new TestAi { Configured = false };
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            ai.Gateway().CompleteAsync(AiRole.Storyteller, "x", [], TestAi.Context));
        Assert.Contains("Admin", ex.Message);
    }

    [Fact]
    public void Json_is_extracted_from_chatty_replies()
    {
        var parsed = JsonExtraction.Parse<Dictionary<string, int>>("Sure! Here it is:\n```json\n{\"a\": 1}\n```\nEnjoy.");
        Assert.Equal(1, parsed["a"]);
        Assert.Throws<AiCallFailedException>(() => JsonExtraction.Parse<Dictionary<string, int>>("no json here"));
    }

    [Fact]
    public void Cost_is_computed_per_million_tokens()
    {
        var price = new AiModelPrice(5m, 25m); // e.g. Claude Opus 5
        Assert.Equal(0.0075m, price.Cost(1000, 100));
        Assert.Equal(0m, new AiModelPrice(0, 0).Cost(1_000_000, 1_000_000)); // Ollama
    }
}

public class GeneratorTests
{
    private static GenerationRequest Request(ContentRating rating = ContentRating.Family) =>
        new(TestAi.Theme, Players: 4, rating, MysteryLength.Short, Twist: "a missing parrot");

    [Fact]
    public async Task Fake_provider_produces_a_valid_scenario_for_the_theme()
    {
        var ai = new TestAi();
        var progress = new List<string>();
        var result = await new MysteryGenerator(ai.Gateway()).GenerateAsync(Request(), TestAi.Context, new SyncProgress(progress), CancellationToken.None);

        var s = result.Scenario;
        Assert.Empty(ScenarioValidator.Validate(s));
        Assert.Equal("speakeasy", s.ThemeSlug);
        Assert.StartsWith("ai-speakeasy-", s.Id);
        Assert.Equal(ContentRating.Family, s.ContentRating);
        Assert.NotEmpty(progress);
        Assert.Contains(ai.Usage, u => u.Context.Purpose == "blind-solve");
    }

    [Fact]
    public async Task Validation_errors_are_sent_back_and_fixed()
    {
        var good = new StreamReader(typeof(MysteryGenerator).Assembly.GetManifestResourceStream("ButlerDidIt.Ai.Fake.FakeScenario.json")!).ReadToEnd();
        var broken = good.Replace("\"murdererId\": \"colonel\"", "\"murdererId\": \"nobody\"");
        var client = new ScriptedChatClient("{\"title\":\"x\"}", broken, good, "{\"suspectId\":\"colonel\"}");
        var ai = new TestAi { Client = client };

        var result = await new MysteryGenerator(ai.Gateway()).GenerateAsync(Request(), TestAi.Context, null, CancellationToken.None);

        Assert.Equal("colonel", result.Scenario.Solution.MurdererId);
        // The repair request quoted the validator's complaint back to the model.
        var repair = client.Calls[2].Last().Text;
        Assert.Contains("nobody", repair);
    }

    [Fact]
    public async Task Gives_up_cleanly_after_repeated_failures()
    {
        var ai = new TestAi { Client = new ScriptedChatClient("{\"title\":\"x\"}", "not json at all") };
        await Assert.ThrowsAsync<AiCallFailedException>(() =>
            new MysteryGenerator(ai.Gateway()).GenerateAsync(Request(), TestAi.Context, null, CancellationToken.None));
    }

    [Fact]
    public async Task Unsolvable_mysteries_get_one_strengthening_pass_then_a_warning()
    {
        var good = new StreamReader(typeof(MysteryGenerator).Assembly.GetManifestResourceStream("ButlerDidIt.Ai.Fake.FakeScenario.json")!).ReadToEnd();
        var wrongGuess = "{\"suspectId\":\"gardener\"}";
        var client = new ScriptedChatClient("{\"title\":\"x\"}", good, wrongGuess, good, wrongGuess);
        var ai = new TestAi { Client = client };

        var result = await new MysteryGenerator(ai.Gateway()).GenerateAsync(Request(), TestAi.Context, null, CancellationToken.None);
        Assert.Single(result.Warnings);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}

public class PromptTests
{
    private static Scenario Blackwood()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content"))) dir = dir.Parent;
        return ContentLibrary.Load(Path.Combine(dir!.FullName, "content")).SelectMany(t => t.Scenarios).Single(s => s.Id == "death-at-blackwood-manor");
    }

    /// <summary>A started game where nobody plays anyone except one guest as Violet, so the rest are NPCs.</summary>
    private static GameState GameWithNpcs(Scenario scenario)
    {
        var now = DateTimeOffset.UtcNow;
        var seat = Guid.NewGuid();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(now, seat, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(now, Guid.NewGuid(), "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(now, Guid.NewGuid(), "Cara", false, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(now, seat, "violet"));
        s = GameEngine.Apply(s, scenario, new StartGame(now));
        return s;
    }

    [Fact]
    public void Innocent_npc_prompt_contains_no_solution_or_other_secrets()
    {
        var scenario = Blackwood();
        var state = GameWithNpcs(scenario);
        var (system, conversation) = NpcPrompt.Build(scenario, state, "hargrove", "Alice", "Where were you?", ContentRating.Mature);
        var all = system + string.Join("\n", conversation.Select(m => m.Text));

        Assert.Contains("half-brother", all); // his own secret
        Assert.DoesNotContain("YOU ARE THE MURDERER", all);
        Assert.DoesNotContain("never confess", all, StringComparison.OrdinalIgnoreCase);
        foreach (var paragraph in scenario.Solution.Explanation) Assert.DoesNotContain(paragraph, all);
        foreach (var other in scenario.Characters.Where(c => c.Id != "hargrove"))
        {
            Assert.DoesNotContain(other.Private.Backstory, all);
            foreach (var secret in other.Private.Secrets) Assert.DoesNotContain(secret.Text, all);
        }
        // Clues that haven't dropped yet aren't mentioned either.
        foreach (var clue in scenario.Clues) Assert.DoesNotContain(clue.Text, all);
    }

    [Fact]
    public void Murderer_npc_is_told_to_never_confess()
    {
        var scenario = Blackwood();
        var (system, _) = NpcPrompt.Build(scenario, GameWithNpcs(scenario), "finch", "Alice", "Did you do it?", ContentRating.Mature);
        Assert.Contains("Never confess", system);
    }

    [Fact]
    public void Npc_secrets_unlock_with_the_acts()
    {
        var scenario = Blackwood();
        var (system, _) = NpcPrompt.Build(scenario, GameWithNpcs(scenario), "hargrove", "Alice", "?", ContentRating.Mature);
        var act2Secret = scenario.FindCharacter("hargrove")!.Private.Secrets.Single(x => x.UnlockAct == 2).Text;
        Assert.DoesNotContain(act2Secret, system);
    }

    [Fact]
    public void The_hosts_tone_changes_how_the_ai_speaks_within_the_mysterys_rating()
    {
        var scenario = Blackwood();
        string Prompt(ContentRating level, Tone tone) => NpcPrompt.Build(scenario, GameWithNpcs(scenario), "hargrove", "Alice", "?", level, tone).System;

        Assert.Contains("drinking", Prompt(ContentRating.Mature, Tone.Standard));
        Assert.Contains("PG-13", Prompt(ContentRating.Mature, Tone.Clean));
        Assert.DoesNotContain("drinking", Prompt(ContentRating.Mature, Tone.Clean));
        Assert.Contains("silly and funny", Prompt(ContentRating.Family, Tone.Playful));
        Assert.Contains("family-friendly", Prompt(ContentRating.Family, Tone.Playful));
        // "Clean" can't loosen a Family mystery: it stays family-friendly.
        Assert.Contains("family-friendly", Prompt(ContentRating.Family, Tone.Clean));
    }

    [Fact]
    public void Hint_safety_catches_any_part_of_the_killers_name()
    {
        var finch = Blackwood().FindCharacter("finch")!;
        Assert.True(HintSafety.MentionsCharacter("Watch the doctor Finch closely", finch));
        Assert.True(HintSafety.MentionsCharacter("Cornelius seems nervous", finch));
        Assert.False(HintSafety.MentionsCharacter("Look at the port glass again", finch));
        Assert.False(HintSafety.MentionsCharacter("Dr. Watson would check the clock", finch)); // titles don't count
    }

    [Fact]
    public void Verdict_prompt_numbers_every_player()
    {
        var scenario = Blackwood();
        var state = GameWithNpcs(scenario);
        var (system, seats) = InspectorPrompts.Verdicts(scenario, state, ContentRating.Mature);
        Assert.Equal(3, seats.Count);
        Assert.Contains("3. Cara", system);
    }
}

public class ImageSizeTests
{
    [Theory]
    [InlineData("dall-e-3", ImageShape.Portrait, 1024, 1792)]
    [InlineData("dall-e-3", ImageShape.Landscape, 1792, 1024)]
    [InlineData("dall-e-2", ImageShape.Portrait, 1024, 1024)]
    [InlineData("gpt-image-1", ImageShape.Portrait, 1024, 1536)]
    [InlineData("gpt-image-1-mini", ImageShape.Landscape, 1536, 1024)]
    public void Each_model_gets_a_size_it_accepts(string model, ImageShape shape, int width, int height) =>
        Assert.Equal((width, height), ImageSizes.For(model, shape));
}
