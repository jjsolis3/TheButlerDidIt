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

    [Fact]
    public void Duplicate_ids_are_reported()
    {
        var s = TestScenario.Create();
        s.Clues.Add(new Clue { Id = "c1", Title = "Dup", Text = "x" });
        Assert.Contains(ScenarioValidator.Validate(s), e => e.Contains("Duplicate clue id 'c1'"));
    }
}
