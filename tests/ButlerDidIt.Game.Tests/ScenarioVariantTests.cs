using System.Text.Json.Nodes;
using ButlerDidIt.Game.Scenarios;
using static ButlerDidIt.Game.Tests.TestScenario;

namespace ButlerDidIt.Game.Tests;

public class ScenarioVariantTests
{
    private static JsonObject Patch(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void A_version_changes_only_what_it_names_and_leaves_the_original_alone()
    {
        var original = Create();
        var murderer = original.Solution.MurdererId;
        var newKiller = original.Characters.First(c => c.Required && c.Id != murderer).Id;
        var firstClue = original.Clues[0];

        var version = ScenarioVariants.Apply(original, Patch($$"""
            {
              "variantOf": "{{original.Id}}",
              "variant": "B",
              "solution": { "murdererId": "{{newKiller}}" },
              "characters": { "{{newKiller}}": { "private": { "alibi": "NEW ALIBI", "objectives": ["Get away with it."] } } },
              "clues": { "{{firstClue.Id}}": { "text": "NEW CLUE TEXT", "pointsTo": ["{{newKiller}}"] } },
              "addClues": [ { "id": "brand-new", "title": "New", "text": "Added", "act": 1, "pointsTo": ["{{newKiller}}"] } ]
            }
            """));

        Assert.Equal(ScenarioVariants.VariantId(original.Id, "B"), version.Id);
        Assert.Equal(original.Id, version.VariantOf);
        Assert.Equal("B", version.Variant);
        Assert.Equal(original.Title, version.Title); // nothing on screen hints at the version

        Assert.Equal(newKiller, version.Solution.MurdererId);
        Assert.Equal(original.Solution.Explanation, version.Solution.Explanation); // objects merge field by field
        var edited = version.FindCharacter(newKiller)!;
        Assert.Equal("NEW ALIBI", edited.Private.Alibi);
        Assert.Equal(["Get away with it."], edited.Private.Objectives);       // lists are replaced
        Assert.Equal(original.FindCharacter(newKiller)!.Private.Backstory, edited.Private.Backstory); // untouched fields kept
        Assert.Equal("NEW CLUE TEXT", version.FindClue(firstClue.Id)!.Text);
        Assert.Equal(firstClue.Title, version.FindClue(firstClue.Id)!.Title);
        Assert.NotNull(version.FindClue("brand-new"));

        // The original object is unchanged.
        Assert.Equal(murderer, original.Solution.MurdererId);
        Assert.Null(original.FindClue("brand-new"));
        Assert.Null(original.VariantOf);
    }

    [Fact]
    public void Null_removes_a_clue_and_mistakes_are_reported_clearly()
    {
        var original = Create();
        var clue = original.Clues[^1].Id;
        var version = ScenarioVariants.Apply(original, Patch($$"""{ "variantOf": "{{original.Id}}", "variant": "C", "clues": { "{{clue}}": null } }"""));
        Assert.Null(version.FindClue(clue));
        Assert.Equal(original.Clues.Count - 1, version.Clues.Count);

        Assert.Contains("doesn't have", Assert.Throws<InvalidDataException>(() =>
            ScenarioVariants.Apply(original, Patch($$"""{ "variantOf": "x", "variant": "D", "clues": { "no-such-clue": { "text": "?" } } }"""))).Message);
        Assert.Contains("only clues can be removed", Assert.Throws<InvalidDataException>(() =>
            ScenarioVariants.Apply(original, Patch($$"""{ "variantOf": "x", "variant": "D", "characters": { "butler": null } }"""))).Message);
        Assert.Contains("variant", Assert.Throws<InvalidDataException>(() =>
            ScenarioVariants.Apply(original, Patch("""{ "variantOf": "x" }"""))).Message);
    }
}
