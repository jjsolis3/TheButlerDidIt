using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using static ButlerDidIt.Game.Tests.TestScenario;

namespace ButlerDidIt.Game.Tests;

public class MediaTests
{
    private static Scenario WithToast()
    {
        var s = Create();
        s.Acts[0].Cues.Add(new Cue { Type = CueType.Toast, Text = "Raise a glass!", Alternative = "Or a lemonade." });
        return s;
    }

    [Fact]
    public void Toasts_only_show_when_drinking_prompts_are_on()
    {
        var scenario = WithToast();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        var off = AdvanceTimes(GameEngine.Apply(s, scenario, new StartGame(T0)), scenario, 2);
        Assert.DoesNotContain(ViewProjector.Stage(off, scenario, T0).Cues, c => c.Type == CueType.Toast);

        s = GameEngine.Apply(s, scenario, new SetPartyOptions(T0, new PartyOptions { DrinkingPrompts = true }));
        var on = AdvanceTimes(GameEngine.Apply(s, scenario, new StartGame(T0)), scenario, 2);
        var toast = Assert.Single(ViewProjector.Stage(on, scenario, T0).Cues, c => c.Type == CueType.Toast);
        Assert.Equal("Or a lemonade.", toast.Alternative);
    }

    [Fact]
    public void Options_can_only_change_in_the_lobby()
    {
        var (s, scenario) = StartedGame();
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SetPartyOptions(T0, new PartyOptions { DrinkingPrompts = true })));
    }

    [Fact]
    public void Player_photos_appear_in_the_public_player_list()
    {
        var (s, scenario) = StartedGame();
        s = GameEngine.Apply(s, scenario, new SetPlayerPhoto(T0, Alice, "/media/assets/abc"));
        Assert.Equal("/media/assets/abc", ViewProjector.Stage(s, scenario, T0).Players.Single(p => p.SeatId == Alice).PhotoUrl);

        s = GameEngine.Apply(s, scenario, new SetPlayerPhoto(T0, Alice, null));
        Assert.Null(ViewProjector.Stage(s, scenario, T0).Players.Single(p => p.SeatId == Alice).PhotoUrl);
    }

    [Fact]
    public void Overlay_fills_empty_slots_without_touching_hand_placed_media()
    {
        var scenario = Create();
        scenario.Prologue.Add(new Cue { Type = CueType.Image, Text = "Hand-placed", Src = "/original.jpg" });
        var media = new Dictionary<string, string>
        {
            [MediaOverlay.Portrait("butler")] = "/media/assets/portrait",
            [MediaOverlay.Cue("prologue", 0)] = "/media/assets/narration",
            [MediaOverlay.Cue("prologue", 1)] = "/media/assets/should-not-replace",
            [MediaOverlay.Cue("act1", 0)] = "/media/assets/act1",
            [MediaOverlay.Line("butler", "act1", 0)] = "/media/assets/line",
            [MediaOverlay.Victim] = "/media/assets/victim",
        };

        var result = MediaOverlay.Apply(scenario, media);

        Assert.Equal("/media/assets/portrait", result.FindCharacter("butler")!.Portrait);
        Assert.Equal("/media/assets/narration", result.Prologue[0].Src);
        Assert.Equal("/original.jpg", result.Prologue[1].Src);
        Assert.Equal("/media/assets/act1", result.Acts[0].Cues[0].Src);
        Assert.Equal("/media/assets/victim", result.Victim.Portrait);
        Assert.Equal("/media/assets/line", result.FindCharacter("butler")!.Private.LineAudio["act1/0"]);
        Assert.Null(scenario.FindCharacter("butler")!.Portrait); // the original is untouched
        Assert.Empty(ScenarioValidator.Validate(result));
    }

    [Fact]
    public void Npc_line_cues_carry_their_recorded_audio()
    {
        var scenario = MediaOverlay.Apply(Create(), new Dictionary<string, string> { [MediaOverlay.Line("butler", "act1", 0)] = "/media/assets/line" });
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "maid"));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Bob, "cook"));
        s = AdvanceTimes(GameEngine.Apply(s, scenario, new StartGame(T0)), scenario, 2); // butler is an NPC; act 1 cinematic

        var line = Assert.Single(ViewProjector.Stage(s, scenario, T0).Cues, c => c.Type == CueType.Line);
        Assert.Equal("/media/assets/line", line.Src);
    }
}
