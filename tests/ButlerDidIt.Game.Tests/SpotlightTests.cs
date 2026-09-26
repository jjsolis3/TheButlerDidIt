using ButlerDidIt.Game.Engine;
using static ButlerDidIt.Game.Tests.TestScenario;

namespace ButlerDidIt.Game.Tests;

/// <summary>The spotlight, question cards, confrontations and the suspicion vote.</summary>
public class SpotlightTests
{
    /// <summary>Alice plays the maid, Bob the cook (the murderer). Nobody plays the butler, so the narrator does.</summary>
    private static (GameState State, ButlerDidIt.Game.Scenarios.Scenario Scenario) GameWithNpcButler()
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", true, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "maid"));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Bob, "cook"));
        s = GameEngine.Apply(s, scenario, new StartGame(T0));
        return (s, scenario);
    }

    private static SpotlightView Spot(GameState s, ButlerDidIt.Game.Scenarios.Scenario scenario) => ViewProjector.Stage(s, scenario, T0).Spotlight!;

    [Fact]
    public void The_host_spotlights_a_character_during_introductions_and_mingling_and_it_clears_on_the_next_scene()
    {
        var (s, scenario) = StartedGame(); // cast reveal: introductions
        s = GameEngine.Apply(s, scenario, new SetSpotlight(T0, "cook"));
        var spot = Spot(s, scenario);
        Assert.Equal(("Bob", Bob, "Chef Cook", false), (spot.PlayerName, spot.SeatId, spot.CharacterName, spot.IsNpc));
        Assert.StartsWith("Introduce yourself", spot.Question);
        Assert.Equal(T0 + GameEngine.SpeakerTurn, spot.EndsAt);

        s = GameEngine.Apply(s, scenario, new Advance(T0)); // prologue: a new scene
        Assert.Null(s.SpotlightCharacterId);
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SetSpotlight(T0, "cook"))); // not during a cinematic

        s = AdvanceTimes(s, scenario, 2); // act 1 mingle
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SetSpotlight(T0, "guest"))); // not at the party
        s = GameEngine.Apply(s, scenario, new SetSpotlight(T0, "butler"));
        Assert.Equal("Cara", Spot(s, scenario).PlayerName);
        s = GameEngine.Apply(s, scenario, new SetSpotlight(T0, null));
        Assert.Null(ViewProjector.Stage(s, scenario, T0).Spotlight);
    }

    [Fact]
    public void A_character_the_narrator_plays_can_have_the_floor_too()
    {
        var (s, scenario) = GameWithNpcButler();
        s = GameEngine.Apply(s, scenario, new SetSpotlight(T0, "butler"));
        var intro = Spot(s, scenario);
        Assert.True(intro.IsNpc);
        Assert.Null(intro.SeatId);
        Assert.Equal("Mr. Butler bio", intro.NpcLine); // introductions: the public bio

        s = AdvanceTimes(s, scenario, 3); // act 1 mingle
        s = GameEngine.Apply(s, scenario, new SetSpotlight(T0, "butler"));
        Assert.Equal("BUTLER_LINE_ACT1", Spot(s, scenario).NpcLine); // this act's line, which the narrator speaks anyway
    }

    [Fact]
    public void Spin_gives_everyone_a_turn_before_anyone_speaks_twice()
    {
        var (s, scenario) = GameWithNpcButler(); // maid, cook, and the narrator's butler
        var picked = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            s = GameEngine.Apply(s, scenario, new SpinSpotlight(T0.AddSeconds(i * 7)));
            picked.Add(s.SpotlightCharacterId!);
        }
        Assert.Equal(["butler", "cook", "maid"], picked.Order());

        // Everyone has spoken: the next spin starts a new round, but never repeats the current speaker.
        var last = s.SpotlightCharacterId;
        s = GameEngine.Apply(s, scenario, new SpinSpotlight(T0.AddSeconds(100)));
        Assert.NotEqual(last, s.SpotlightCharacterId);
    }

    [Fact]
    public void Question_cards_ask_about_public_clues_and_never_mention_private_ones()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3); // act 1 mingle: "Muddy boots" (public) and "A letter" (private to the maid) are out
        Assert.Contains(s.DroppedClues, d => d.ClueId == "c2");

        s = GameEngine.Apply(s, scenario, new SetSpotlight(T0, "cook"));
        Assert.Contains("Muddy boots", Spot(s, scenario).Question);

        for (var i = 0; i < 12; i++)
        {
            s = GameEngine.Apply(s, scenario, new SetSpotlight(T0, i % 2 == 0 ? "cook" : "maid"));
            Assert.DoesNotContain("A letter", Spot(s, scenario).Question);
        }
    }

    [Fact]
    public void A_guest_can_confront_someone_with_a_clue_once_per_act_and_the_evidence_goes_public()
    {
        var (s, scenario) = StartedGame();
        s = AdvanceTimes(s, scenario, 3); // act 1 mingle
        Assert.True(ViewProjector.Player(s, scenario, Alice, T0).CanConfront);

        // Alice (the maid) holds the private letter pointing at the cook.
        s = GameEngine.Apply(s, scenario, new Confront(T0, Alice, "c2", "cook"));
        var spot = Spot(s, scenario);
        Assert.Equal("cook", spot.CharacterId);
        Assert.Equal(("Ms. Maid", "A letter"), (spot.Confrontation!.AccuserName, spot.Confrontation.ClueTitle));
        Assert.Contains("explain “A letter”", spot.Question);
        Assert.Equal(T0 + GameEngine.ConfrontationTurn, spot.EndsAt);
        Assert.Contains(ViewProjector.Stage(s, scenario, T0).Clues, c => c.Title == "A letter"); // shared with the room
        Assert.Contains(s.Feed, f => f.Text.Contains("confronts Chef Cook"));

        Assert.False(ViewProjector.Player(s, scenario, Alice, T0).CanConfront);
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new Confront(T0, Alice, "c1", "butler"))); // once per act
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new Confront(T0, Bob, "c1", "cook"))); // not yourself
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new Confront(T0, Cara, "c5", "maid"))); // a clue you can't see
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new Confront(T0, Cara, "c1", "guest"))); // not at the party

        s = AdvanceTimes(s, scenario, 2); // act 2 mingle: a new act, a new confrontation
        Assert.True(ViewProjector.Player(s, scenario, Alice, T0).CanConfront);
        s = GameEngine.Apply(s, scenario, new Confront(T0, Alice, "c1", "butler"));
        Assert.Equal("butler", s.SpotlightCharacterId);
    }

    [Fact]
    public void The_room_shows_only_suspicion_totals_and_only_during_the_acts()
    {
        var (s, scenario) = StartedGame();
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SetSuspicion(T0, Alice, "cook"))); // not during introductions
        s = AdvanceTimes(s, scenario, 2); // act 1 scene

        s = GameEngine.Apply(s, scenario, new SetSuspicion(T0, Alice, "cook"));
        s = GameEngine.Apply(s, scenario, new SetSuspicion(T0, Cara, "cook"));
        s = GameEngine.Apply(s, scenario, new SetSuspicion(T0, Bob, "maid"));
        Assert.Throws<GameRuleException>(() => GameEngine.Apply(s, scenario, new SetSuspicion(T0, Bob, "cook"))); // not yourself

        var heat = ViewProjector.Stage(s, scenario, T0).Suspicion;
        Assert.Equal([("cook", 2), ("maid", 1)], heat.Select(h => (h.CharacterId, h.Votes)));
        Assert.Equal("cook", ViewProjector.Player(s, scenario, Alice, T0).MySuspicion);
        Assert.DoesNotContain(Alice.ToString(), GameJson.Serialize(heat)); // never who voted for whom

        s = GameEngine.Apply(s, scenario, new SetSuspicion(T0, Alice, null));
        Assert.Equal(1, ViewProjector.Stage(s, scenario, T0).Suspicion.Single(h => h.CharacterId == "cook").Votes);

        s = AdvanceTimes(s, scenario, 4); // on to the accusations: the heat meter goes away
        Assert.Equal(Phase.Accusation, s.Phase);
        Assert.Empty(ViewProjector.Stage(s, scenario, T0).Suspicion);
    }
}
