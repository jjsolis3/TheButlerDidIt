using System.Text.Json;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Tests;

public class ScenarioValidatorTests
{
    [Fact]
    public void Test_scenario_is_valid()
    {
        Assert.Empty(ScenarioValidator.Validate(TestScenario.Create()));
    }

    [Fact]
    public void Broken_references_are_reported()
    {
        var s = TestScenario.Create();
        s.Clues.Add(new Clue { Id = "bad", Title = "Bad", Text = "x", Act = 9, PointsTo = ["nobody"], Visibility = ClueVisibility.Private });

        var errors = ScenarioValidator.Validate(s);
        Assert.Contains(errors, e => e.Contains("act 9"));
        Assert.Contains(errors, e => e.Contains("unknown character 'nobody'"));
        Assert.Contains(errors, e => e.Contains("valid recipient"));
    }

    [Fact]
    public void Mystery_must_be_solvable()
    {
        var s = TestScenario.Create();
        s.Clues.RemoveAll(c => c.PointsTo.Contains("cook"));

        Assert.Contains(ScenarioValidator.Validate(s), e => e.Contains("point to the murderer"));
    }

    /// <summary>A copy of the test scenario with one JSON change (Scenario is init-only, like the files it's read from).</summary>
    private static Scenario With(Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(TestScenario.Create(), GameJson.Options)!.AsObject();
        change(node);
        return node.Deserialize<Scenario>(GameJson.Options)!;
    }

    [Fact]
    public void A_character_marked_never_the_killer_cannot_be_the_murderer()
    {
        var s = With(n => n["characters"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "cook")!["killerEligible"] = false);
        Assert.Contains(ScenarioValidator.Validate(s), e => e.Contains("never the killer"));
    }

    [Fact]
    public void Family_mysteries_may_not_mention_alcohol_but_ginger_is_fine()
    {
        var family = With(n =>
        {
            n["contentRating"] = "family";
            n["victim"]!["description"] = "Found beside a glass of ginger beer and a bottle of rum.";
        });
        var errors = ScenarioValidator.Validate(family);
        Assert.Contains(errors, e => e.Contains("\"rum\""));
        Assert.Contains(errors, e => e.Contains("\"beer\""));
        Assert.DoesNotContain(errors, e => e.Contains("\"gin\""));

        // The same text is fine in a mystery for adults.
        var adults = With(n => n["victim"]!["description"] = "Found beside a bottle of rum.");
        Assert.Empty(ScenarioValidator.Validate(adults));
    }

    [Fact]
    public void Duplicate_ids_are_reported()
    {
        var s = TestScenario.Create();
        s.Clues.Add(new Clue { Id = "c1", Title = "Dup", Text = "x" });
        Assert.Contains(ScenarioValidator.Validate(s), e => e.Contains("Duplicate clue id 'c1'"));
    }
}
