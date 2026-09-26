using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Tests;

/// <summary>Loads the real /content folder so a broken scenario file fails CI, not a party.</summary>
public class ContentTests
{
    private static string ContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content", "themes"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("content/ not found"), "content");
    }

    [Fact]
    public void All_content_loads_and_validates()
    {
        var themes = ContentLibrary.Load(ContentRoot());
        Assert.True(themes.Count >= 8);
        Assert.Contains(themes, t => t.Theme.Slug == "the-butler-did-it" && t.Scenarios.Count >= 1);
        Assert.Equal(themes.Count, themes.Select(t => t.Theme.Slug).Distinct().Count());
    }

    /// <summary>Every hand-written mystery, by id, for the theory below. New files are picked up automatically.</summary>
    public static TheoryData<string> ScenarioIds()
    {
        var data = new TheoryData<string>();
        foreach (var s in ContentLibrary.Load(ContentRoot()).SelectMany(t => t.Scenarios)) data.Add(s.Id);
        return data;
    }

    private static Scenario Load(string id) => ContentLibrary.Load(ContentRoot()).SelectMany(t => t.Scenarios).Single(s => s.Id == id);

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void Every_mystery_plays_from_start_to_finish_with_minimum_players(string id)
    {
        var scenario = Load(id);
        var now = TestScenario.T0;
        var seats = Enumerable.Range(1, scenario.MinPlayers).Select(_ => Guid.NewGuid()).ToList();

        var s = GameEngine.NewGame();
        foreach (var (seat, i) in seats.Select((x, i) => (x, i)))
        {
            s = GameEngine.Apply(s, scenario, new AddPlayer(now, seat, $"Player {i + 1}", i == 0, false));
        }
        s = GameEngine.Apply(s, scenario, new StartGame(now));

        // Every required character is present, either played or as an NPC.
        var inPlay = GameEngine.CharactersInPlay(s, scenario).Select(c => c.Id).ToHashSet();
        Assert.All(scenario.Characters.Where(c => c.Required), c => Assert.Contains(c.Id, inPlay));

        while (s.Phase != Phase.Finished)
        {
            s = GameEngine.Apply(s, scenario, new Advance(now));
            // Views must build in every phase for every seat.
            ViewProjector.Stage(s, scenario, now);
            foreach (var seat in seats) ViewProjector.Player(s, scenario, seat, now);
        }

        Assert.Equal(scenario.Clues.Count, s.DroppedClues.Count);
        ViewProjector.Recap(s, scenario, now);
    }

    [Fact]
    public void There_is_a_hand_written_mystery_on_both_the_family_and_the_adult_shelf()
    {
        var all = ContentLibrary.Load(ContentRoot()).SelectMany(t => t.Scenarios).ToList();
        Assert.Contains(all, s => s.ContentRating == ContentRating.Family);
        Assert.Contains(all, s => s.ContentRating == ContentRating.Mature);
    }

    private static readonly string[] AlcoholWords = ["wine", "rum", "beer", "gin", "whisky", "whiskey", "vodka", "brandy", "grog", "cocktail", "drunk", "booze"];

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void Family_mysteries_never_mention_alcohol(string id)
    {
        var scenario = Load(id);
        if (scenario.ContentRating != ContentRating.Family) return;

        // Everything a family audience reads or hears, as one lowercase text.
        var text = GameJson.Serialize(scenario).ToLowerInvariant();
        foreach (var word in AlcoholWords)
        {
            // Whole words only, so "ginger" or "virginia" don't trip the check.
            Assert.DoesNotMatch($@"\b{word}s?\b", text);
        }
    }
}
