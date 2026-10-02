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

    [Fact]
    public void Clue_pictures_follow_the_clue_title_and_never_replace_a_hand_placed_one()
    {
        var scenario = Create();
        var c1 = scenario.FindClue("c1")!;
        var media = new Dictionary<string, string>
        {
            [MediaOverlay.ClueImage("c1", c1.Title)] = "/media/assets/boots",
            [MediaOverlay.ClueImage("c2", "An old title")] = "/media/assets/stale",
        };

        var result = MediaOverlay.Apply(scenario, media);
        Assert.Equal("/media/assets/boots", result.FindClue("c1")!.Image);
        Assert.Null(result.FindClue("c2")!.Image); // made for a different title, so it isn't used

        var handPlaced = Create();
        handPlaced.Clues[0] = new Clue { Id = "c1", Title = c1.Title, Text = c1.Text, Act = 1, PointsTo = c1.PointsTo, Image = "/original.jpg" };
        Assert.Equal("/original.jpg", MediaOverlay.Apply(handPlaced, media).FindClue("c1")!.Image);
    }

    [Fact]
    public void A_host_s_upload_replaces_even_hand_placed_media()
    {
        var scenario = Create();
        scenario.Clues[0] = new Clue { Id = "c1", Title = "Muddy boots", Text = "PUBLIC_CLUE_1", Act = 1, PointsTo = ["cook"], Image = "/original.jpg" };
        var media = new Dictionary<string, string> { [MediaOverlay.ClueImage("c1", "Muddy boots")] = "/media/assets/my-photo" };
        Assert.Equal("/original.jpg", MediaOverlay.Apply(scenario, media).FindClue("c1")!.Image); // painted by the AI: only fills gaps
        Assert.Equal("/media/assets/my-photo", MediaOverlay.Apply(scenario, media, media.Keys.ToHashSet()).FindClue("c1")!.Image); // uploaded: wins
    }

    [Fact]
    public void A_scene_s_video_replaces_its_narration_and_pictures_but_lines_and_toasts_still_play()
    {
        var scenario = Create();
        scenario.Prologue.Add(new Cue { Type = CueType.Image, Text = "The hall" });
        scenario.Prologue.Add(new Cue { Type = CueType.Toast, Text = "To Lord Victim!", Alternative = "or a sip of juice" });
        scenario.Acts[0].Cues.Add(new Cue { Type = CueType.Line, Speaker = "butler", Text = "I was polishing silver." });
        var media = new Dictionary<string, string>
        {
            [MediaOverlay.Video(MediaOverlay.Prologue)] = "/media/assets/opening",
            [MediaOverlay.Video("act1")] = "/media/assets/act1-video",
            [MediaOverlay.Cue("prologue", 0)] = "/media/assets/narration", // a voice for narration the video replaced: unused
        };

        var result = MediaOverlay.Apply(scenario, media, media.Keys.ToHashSet());
        Assert.Equal([(CueType.Video, "/media/assets/opening"), (CueType.Toast, null)], result.Prologue.Select(c => (c.Type, c.Src)));
        Assert.Equal([CueType.Video, CueType.Line], result.Acts[0].Cues.Select(c => c.Type));
        Assert.Equal("/media/assets/act1-video", result.Acts[0].Cues[0].Src);
        Assert.Empty(result.Acts[1].Cues); // no video, nothing changes
        Assert.Empty(result.Finale);
        Assert.Empty(ScenarioValidator.Validate(result));
    }

    [Fact]
    public void Background_music_plays_until_the_reveal_and_an_act_can_have_its_own()
    {
        var media = new Dictionary<string, string> { [MediaOverlay.Music] = "/media/assets/evening", [MediaOverlay.ActMusic("act2")] = "/media/assets/act2" };
        var (s, plain) = StartedGame();
        var scenario = MediaOverlay.Apply(plain, media, media.Keys.ToHashSet());
        string? Music(GameState state) => ViewProjector.Stage(state, scenario, T0).MusicUrl;

        Assert.Equal("/media/assets/evening", Music(GameEngine.NewGame())); // the lobby
        Assert.Equal("/media/assets/evening", Music(s));
        var seen = new List<(Phase, int, string?)>();
        for (var step = 0; s.Phase != Phase.Finished && step < 100; step++)
        {
            seen.Add((s.Phase, s.ActIndex, Music(s)));
            s = GameEngine.Apply(s, scenario, new Advance(T0.AddHours(3)));
            if (s.Phase == Phase.Accusation) s = Accuse(s, scenario);
        }
        Assert.All(seen.Where(x => x.Item1 == Phase.Act && x.Item2 == 0), x => Assert.Equal("/media/assets/evening", x.Item3));
        Assert.All(seen.Where(x => x.Item1 == Phase.Act && x.Item2 == 1), x => Assert.Equal("/media/assets/act2", x.Item3));
        Assert.Contains(seen, x => x.Item1 == Phase.Act && x.Item2 == 1);
        Assert.All(seen.Where(x => x.Item1 is Phase.Reveal or Phase.Awards), x => Assert.Null(x.Item3)); // the ending in silence
        Assert.Contains(seen, x => x.Item1 == Phase.Reveal);
        Assert.Null(ViewProjector.Stage(s, plain, T0).MusicUrl); // no music uploaded: none
    }

    /// <summary>Everyone accuses the cook, so the game can move on to the reveal.</summary>
    private static GameState Accuse(GameState s, Scenario scenario)
    {
        foreach (var p in s.Players) s = GameEngine.Apply(s, scenario, new SubmitAccusation(T0, p.SeatId, "cook", "money", "poison"));
        return s;
    }
}
