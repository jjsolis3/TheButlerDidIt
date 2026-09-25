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

    [Fact]
    public void Blackwood_manor_plays_from_start_to_finish_with_minimum_players()
    {
        var scenario = ContentLibrary.Load(ContentRoot())
            .SelectMany(t => t.Scenarios)
            .Single(s => s.Id == "death-at-blackwood-manor");
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
    }
}
