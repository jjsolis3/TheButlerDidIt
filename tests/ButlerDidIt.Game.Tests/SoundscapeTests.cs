using System.Text.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using static ButlerDidIt.Game.Tests.TestScenario;

namespace ButlerDidIt.Game.Tests;

/// <summary>The background sound a mystery makes up when the host hasn't uploaded music (#127).</summary>
public class SoundscapeTests
{
    private static readonly ThemeDefinition Theme = new() { Slug = "test", Name = "Test", Soundscape = Soundscape.Storm };

    /// <summary>The test mystery with its own sound and act two's, set the way the editor would (in the JSON).</summary>
    private static Scenario WithSounds(Soundscape? mystery, Soundscape? actTwo)
    {
        var root = JsonSerializer.SerializeToNode(Create(), GameJson.Options)!.AsObject();
        if (mystery is { } m) root["soundscape"] = JsonSerializer.SerializeToNode(m, GameJson.Options);
        if (actTwo is { } a) root["acts"]![1]!.AsObject()["soundscape"] = JsonSerializer.SerializeToNode(a, GameJson.Options);
        return root.Deserialize<Scenario>(GameJson.Options)!;
    }

    [Fact]
    public void A_mystery_that_names_no_sound_takes_its_theme_s_and_one_that_does_keeps_its_own()
    {
        Assert.Equal(Soundscape.Storm, ScenarioDefaults.Apply(Create(), Theme).Soundscape);
        Assert.Equal(Soundscape.Train, ScenarioDefaults.Apply(WithSounds(Soundscape.Train, null), Theme).Soundscape);
        Assert.Null(ScenarioDefaults.Apply(Create(), null).Soundscape); // no theme found: left as it is
    }

    [Fact]
    public void The_sound_follows_the_acts_and_the_reveal_is_left_in_silence()
    {
        var scenario = ScenarioDefaults.Apply(WithSounds(null, Soundscape.Night), Theme);
        var (s, _) = StartedGame();
        Soundscape Sound(GameState state) => ViewProjector.Stage(state, scenario, T0).Soundscape;

        Assert.Equal(Soundscape.Storm, Sound(GameEngine.NewGame())); // the lobby: the theme's
        var seen = new List<(Phase Phase, int Act, Soundscape Sound)>();
        for (var step = 0; s.Phase != Phase.Finished && step < 100; step++)
        {
            seen.Add((s.Phase, s.ActIndex, Sound(s)));
            s = GameEngine.Apply(s, scenario, new Advance(T0.AddHours(3)));
            if (s.Phase == Phase.Accusation)
                foreach (var p in s.Players) s = GameEngine.Apply(s, scenario, new SubmitAccusation(T0, p.SeatId, "cook", "money", "poison"));
        }
        Assert.All(seen.Where(x => x.Phase == Phase.Act && x.Act == 0), x => Assert.Equal(Soundscape.Storm, x.Sound));
        Assert.All(seen.Where(x => x.Phase == Phase.Act && x.Act == 1), x => Assert.Equal(Soundscape.Night, x.Sound)); // act two's own
        Assert.Contains(seen, x => x.Phase == Phase.Act && x.Act == 1);
        Assert.All(seen.Where(x => x.Phase is Phase.Reveal or Phase.Awards), x => Assert.Equal(Soundscape.Silence, x.Sound));
        Assert.Contains(seen, x => x.Phase == Phase.Reveal);
        Assert.Equal(Soundscape.Silence, Sound(s));
    }

    [Fact]
    public void A_scenario_loaded_without_a_theme_still_hums()
    {
        // Nothing filled in (an old party's scenario, say): the stage never goes quiet by mistake.
        var (s, scenario) = StartedGame();
        Assert.Equal(Soundscape.Drone, ViewProjector.Stage(s, scenario, T0).Soundscape);
    }
}
