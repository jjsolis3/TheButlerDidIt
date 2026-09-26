using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Ai.Tests;

/// <summary>The AI remix: a new version of a story in which a given guest's character is the killer.</summary>
public class RemixTests
{
    private static Scenario Load(string id)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content"))) dir = dir.Parent;
        return ContentLibrary.Load(Path.Combine(dir!.FullName, "content")).SelectMany(t => t.Scenarios).Single(s => s.Id == id);
    }

    [Fact]
    public async Task The_fake_storyteller_makes_a_valid_version_with_the_asked_for_killer()
    {
        var original = Load("the-captains-last-cocoa");
        var ai = new TestAi();
        var result = await new VersionRemixer(ai.Gateway()).RemixAsync(original, "nell", "ai123abc", TestAi.Context, null, CancellationToken.None);

        var version = result.Scenario;
        Assert.Equal("the-captains-last-cocoa--ai123abc", version.Id);
        Assert.Equal(original.Id, version.VariantOf);
        Assert.Equal("nell", version.Solution.MurdererId);
        Assert.True(version.FindCharacter("nell")!.Required); // an optional character is made essential
        Assert.Empty(ScenarioValidator.Validate(version));
        // Everything guests saw in the lobby is unchanged.
        Assert.Equal(original.Title, version.Title);
        Assert.Equal(original.Characters.Select(c => (c.Name, c.PublicBio, c.CostumeTips)), version.Characters.Select(c => (c.Name, c.PublicBio, c.CostumeTips)));
        Assert.Contains(ai.Usage, u => u.Context.Purpose == "blind-solve");
    }

    [Fact]
    public void A_patch_that_changes_what_guests_have_seen_is_rejected()
    {
        var original = Load("death-at-blackwood-manor");
        var (version, errors) = VersionRemixer.Check(original, "violet", "aiX",
            """{"setting":{"place":"The Moon"},"characters":{"violet":{"publicBio":"A spy."}},"acts":{"act1":{"title":"New!"}}}""");

        Assert.Null(version);
        Assert.Contains(errors, e => e.Contains("\"setting\""));
        Assert.Contains(errors, e => e.Contains("publicBio"));
        Assert.Contains(errors, e => e.Contains("act1"));
    }

    [Fact]
    public void A_patch_with_the_wrong_killer_or_unfair_clues_is_sent_back()
    {
        var original = Load("death-at-blackwood-manor");
        // A new solution naming Violet, but no clue points at her and nothing else changes.
        var solution = GameJson.Serialize(original.Solution).Replace("\"murdererId\":\"finch\"", "\"murdererId\":\"violet\"");
        var (_, errors) = VersionRemixer.Check(original, "evelyn", "aiX", $$"""{"solution":{{solution}}}""");

        Assert.Contains(errors, e => e.Contains("must be \"evelyn\""));
        Assert.Contains(errors, e => e.Contains("point to the murderer"));
    }

    [Fact]
    public async Task Gives_up_cleanly_after_repeated_failures()
    {
        var ai = new TestAi { Client = new ScriptedChatClient("not json at all") };
        await Assert.ThrowsAsync<AiCallFailedException>(() =>
            new VersionRemixer(ai.Gateway()).RemixAsync(Load("death-at-blackwood-manor"), "violet", "aiX", TestAi.Context, null, CancellationToken.None));
    }
}
