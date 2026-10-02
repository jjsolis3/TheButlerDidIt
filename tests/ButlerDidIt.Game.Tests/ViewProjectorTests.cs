using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using static ButlerDidIt.Game.Tests.TestScenario;

namespace ButlerDidIt.Game.Tests;

/// <summary>
/// "No leak" tests. Views are serialized to JSON, exactly what a browser would
/// receive, and searched for text that screen must never contain.
/// </summary>
public class ViewProjectorTests
{
    private static readonly string[] SolutionMarkers = ["SOLUTION_PARAGRAPH_1", "SOLUTION_PARAGRAPH_2", "SOLUTION_TIMELINE"];

    private static string StageJson(GameState s, Scenario scenario) => GameJson.Serialize(ViewProjector.Stage(s, scenario, T0));
    private static string PlayerJson(GameState s, Scenario scenario, Guid seat) => GameJson.Serialize(ViewProjector.Player(s, scenario, seat, T0));

    /// <summary>Walks the whole game up to (not including) the reveal and yields every intermediate state.</summary>
    private static IEnumerable<GameState> StatesBeforeReveal()
    {
        var (s, scenario) = StartedGame();
        while (s.Phase != Phase.Reveal)
        {
            yield return s;
            if (s.Phase == Phase.Act && s.ActStep == ActStep.Mingle)
            {
                yield return GameEngine.Apply(s, scenario, new Tick(T0.AddHours(1))); // midway clues dropped
            }
            s = GameEngine.Apply(s, scenario, new Advance(T0));
        }
    }

    [Fact]
    public void Stage_never_contains_private_information_before_the_reveal()
    {
        var scenario = Create();
        var forbidden = new[]
        {
            "BUTLER_SECRET", "MAID_SECRET", "COOK_SECRET", "GUEST_SECRET",
            "_BACKSTORY", "_ALIBI", "_OBJECTIVE",
            "PRIVATE_CLUE_TO_MAID", "PUZZLE_SOLVED_TEXT", "a clock",
        }.Concat(SolutionMarkers);

        foreach (var state in StatesBeforeReveal())
        {
            var json = StageJson(state, scenario);
            foreach (var marker in forbidden)
            {
                Assert.False(json.Contains(marker, StringComparison.Ordinal), $"Stage leaked '{marker}' during {state.Phase}/{state.ActStep}.");
            }
            // Clue metadata that would give the game away is never serialized.
            Assert.DoesNotContain("pointsTo", json);
            Assert.DoesNotContain("redHerring", json);
        }
    }

    [Fact]
    public void A_scene_s_video_and_an_act_s_music_reach_the_stage_only_when_their_moment_comes()
    {
        // Every upload a host can make for the scenes, each with its own address.
        var media = new Dictionary<string, string>
        {
            [MediaOverlay.Video(MediaOverlay.Prologue)] = "/media/assets/video-prologue",
            [MediaOverlay.Video("act1")] = "/media/assets/video-act1",
            [MediaOverlay.Video("act2")] = "/media/assets/video-act2",
            [MediaOverlay.Video(MediaOverlay.Finale)] = "/media/assets/video-finale",
            [MediaOverlay.Music] = "/media/assets/music",
            [MediaOverlay.ActMusic("act1")] = "/media/assets/music-act1",
            [MediaOverlay.ActMusic("act2")] = "/media/assets/music-act2",
        };
        var scenario = MediaOverlay.Apply(Create(), media, media.Keys.ToHashSet());

        var seen = new HashSet<string>();
        foreach (var state in StatesBeforeReveal())
        {
            var json = StageJson(state, scenario);
            // A later act's video or music, and the finale's video (which may show who did it), never arrive early.
            var allowed = new HashSet<string> { "/media/assets/music" };
            if (state.Phase == Phase.Prologue) allowed.Add("/media/assets/video-prologue");
            if (state.Phase == Phase.Act)
            {
                var act = scenario.Acts[state.ActIndex].Id;
                allowed.Add($"/media/assets/music-{act}");
                if (state.ActStep == ActStep.Cinematic) allowed.Add($"/media/assets/video-{act}");
            }
            foreach (var url in media.Values.Where(u => !allowed.Contains(u)))
                Assert.False(json.Contains($"\"{url}\"", StringComparison.Ordinal), $"Stage sent {url} during {state.Phase}/{state.ActStep} of act {state.ActIndex + 1}.");
            foreach (var url in allowed.Where(u => json.Contains($"\"{u}\"", StringComparison.Ordinal))) seen.Add(url);
            foreach (var seat in new[] { Alice, Bob, Cara })
                Assert.DoesNotContain("/media/assets/video-finale", PlayerJson(state, scenario, seat));
        }
        // And each one does arrive at its moment (the finale's comes with the reveal).
        foreach (var url in media.Values.Where(u => u != "/media/assets/video-finale")) Assert.Contains(url, seen);
    }

    [Fact]
    public void Player_sees_only_their_own_private_information()
    {
        var scenario = Create();
        foreach (var state in StatesBeforeReveal().Where(s => s.Phase != Phase.Lobby))
        {
            // Alice plays the maid.
            var alice = PlayerJson(state, scenario, Alice);
            Assert.Contains("MAID_BACKSTORY", alice);
            foreach (var marker in new[] { "BUTLER_", "COOK_", "GUEST_SECRET", "GUEST_BACKSTORY" }.Concat(SolutionMarkers))
            {
                Assert.False(alice.Contains(marker, StringComparison.Ordinal), $"Alice saw '{marker}' during {state.Phase}/{state.ActStep}.");
            }

            // Bob plays the cook and must never see the maid's private clue.
            var bob = PlayerJson(state, scenario, Bob);
            Assert.DoesNotContain("PRIVATE_CLUE_TO_MAID", bob);
            Assert.DoesNotContain("MAID_", bob);
        }
    }

    [Fact]
    public void A_private_clue_picture_reaches_only_its_recipient()
    {
        var scenario = MediaOverlay.Apply(Create(), new Dictionary<string, string>
        {
            [MediaOverlay.ClueImage("c1", "Muddy boots")] = "/media/assets/PUBLIC_CLUE_PICTURE",
            [MediaOverlay.ClueImage("c2", "A letter")] = "/media/assets/PRIVATE_CLUE_PICTURE",
        });
        var (s, _) = StartedGame();
        while (!(s.Phase == Phase.Act && s.ActStep == ActStep.Mingle)) s = GameEngine.Apply(s, scenario, new Advance(T0));

        Assert.Contains("PUBLIC_CLUE_PICTURE", StageJson(s, scenario));
        Assert.DoesNotContain("PRIVATE_CLUE_PICTURE", StageJson(s, scenario));
        Assert.Contains("PRIVATE_CLUE_PICTURE", PlayerJson(s, scenario, Alice)); // Alice plays the maid
        Assert.DoesNotContain("PRIVATE_CLUE_PICTURE", PlayerJson(s, scenario, Bob));
    }

    [Fact]
    public void Lobby_dossier_shows_only_the_invitation()
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "maid"));

        var view = ViewProjector.Player(s, scenario, Alice, T0);
        Assert.NotNull(view.Dossier);
        Assert.False(view.Dossier.Unlocked);
        Assert.Null(view.Dossier.Backstory);
        Assert.DoesNotContain("MAID_SECRET", GameJson.Serialize(view));
        // Not even the number of secrets: a "Surprise me" version, which may have more or fewer, is dealt at the start.
        Assert.Equal(0, view.Dossier.LockedSecrets);
    }

    [Fact]
    public void Shared_private_clue_becomes_visible_to_everyone()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3);
        Assert.DoesNotContain("PRIVATE_CLUE_TO_MAID", StageJson(s, scenario));

        s = GameEngine.Apply(s, scenario, new ShareClue(T0, Alice, "c2"));
        Assert.Contains("PRIVATE_CLUE_TO_MAID", StageJson(s, scenario));
        Assert.Contains("PRIVATE_CLUE_TO_MAID", PlayerJson(s, scenario, Bob));
    }

    [Fact]
    public void Revealed_secret_appears_on_stage()
    {
        var (s, scenario) = StartedGame();
        s = GameEngine.Apply(s, scenario, new RevealSecret(T0, Cara, "butler-s1"));
        Assert.Contains("BUTLER_SECRET_START", StageJson(s, scenario));
        Assert.DoesNotContain("BUTLER_SECRET_ACT2", StageJson(s, scenario));
    }

    [Fact]
    public void Npc_lines_play_on_stage_but_player_lines_stay_on_phones()
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "maid"));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Bob, "cook"));
        s = GameEngine.Apply(s, scenario, new StartGame(T0)); // butler becomes an NPC
        s = AdvanceTimes(s, scenario, 2); // act 1 cinematic

        var stage = ViewProjector.Stage(s, scenario, T0);
        Assert.Contains(stage.Cues, c => c.Text == "BUTLER_LINE_ACT1" && c.SpeakerName == "Mr. Butler");
        Assert.DoesNotContain(stage.Cues, c => c.Text == "MAID_LINE_ACT1");
        Assert.Equal(["MAID_LINE_ACT1"], ViewProjector.Player(s, scenario, Alice, T0).Dossier!.LinesThisAct);
    }

    [Fact]
    public void Murderer_is_told_only_when_the_scenario_says_so()
    {
        var (s, scenario) = StartedGame();
        Assert.True(ViewProjector.Player(s, scenario, Bob, T0).Dossier!.IsMurderer);
        Assert.False(ViewProjector.Player(s, scenario, Alice, T0).Dossier!.IsMurderer);
    }

    [Fact]
    public void Reveal_discloses_the_solution_one_step_at_a_time()
    {
        var (s, scenario) = StartedGame();
        while (s.Phase != Phase.Reveal) s = GameEngine.Apply(s, scenario, new Advance(T0));

        var step0 = ViewProjector.Stage(s, scenario, T0).Reveal!;
        Assert.Null(step0.MurdererName);
        Assert.Empty(step0.Explanation);

        s = GameEngine.Apply(s, scenario, new Advance(T0));
        var step1 = ViewProjector.Stage(s, scenario, T0).Reveal!;
        Assert.Equal("Chef Cook", step1.MurdererName);
        Assert.Empty(step1.Explanation);

        s = GameEngine.Apply(s, scenario, new Advance(T0));
        Assert.Equal(["SOLUTION_PARAGRAPH_1"], ViewProjector.Stage(s, scenario, T0).Reveal!.Explanation);

        s = AdvanceTimes(s, scenario, 2);
        var final = ViewProjector.Stage(s, scenario, T0).Reveal!;
        Assert.Equal(2, final.Explanation.Count);
        Assert.NotEmpty(final.Scores);
        Assert.NotEmpty(final.Timeline);
    }

    [Fact]
    public void Award_results_stay_hidden_until_voting_closes()
    {
        var (s, scenario) = StartedGame();
        while (s.Phase != Phase.Awards) s = GameEngine.Apply(s, scenario, new Advance(T0));
        s = GameEngine.Apply(s, scenario, new CastAwardVote(T0, Alice, "costume", Bob));

        Assert.Null(ViewProjector.Stage(s, scenario, T0).Awards!.Results);
        s = GameEngine.Apply(s, scenario, new Advance(T0));
        Assert.Equal(["Bob"], ViewProjector.Stage(s, scenario, T0).Awards!.Results!.Single(r => r.AwardId == "costume").Winners);
    }

    [Fact]
    public void The_recap_is_refused_until_the_game_is_over()
    {
        var (s, scenario) = StartedGame();
        while (s.Phase != Phase.Awards)
        {
            Assert.Throws<InvalidOperationException>(() => ViewProjector.Recap(s, scenario, T0));
            s = GameEngine.Apply(s, scenario, new Advance(T0));
        }
        Assert.Throws<InvalidOperationException>(() => ViewProjector.Recap(s, scenario, T0));
    }

    [Fact]
    public void The_recap_tells_the_whole_story_but_keeps_private_hints_out()
    {
        var (s, scenario) = StartedGame();
        s.Hints.Add(new HintEntry { Id = Guid.NewGuid(), SeatId = Alice, Act = 1, Text = "PRIVATE_HINT" });
        while (s.Phase != Phase.Finished) s = GameEngine.Apply(s, scenario, new Advance(T0));

        var recap = ViewProjector.Recap(s, scenario, T0);
        var json = GameJson.Serialize(recap);

        Assert.All(SolutionMarkers, m => Assert.Contains(m, json)); // the full solution and timeline
        Assert.Contains("BUTLER_SECRET_ACT2", json);                 // every secret, even ones never unlocked
        Assert.DoesNotContain("PRIVATE_HINT", json);
        Assert.Equal("Alice", recap.Cast.Single(c => c.CharacterId == "maid").PlayedBy);
        Assert.Single(recap.Cast, c => c.IsMurderer);
        Assert.DoesNotContain(recap.Cast, c => c.CharacterId == "guest"); // optional, unplayed: not part of the story
    }
}
