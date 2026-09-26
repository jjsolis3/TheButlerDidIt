using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Tests;

/// <summary>A tiny but valid mystery used by the engine tests. Every private string is unique so leak tests can search for it.</summary>
public static class TestScenario
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 31, 19, 0, 0, TimeSpan.Zero);

    public static Scenario Create() => new()
    {
        Id = "test-mystery",
        ThemeSlug = "test",
        Title = "Test Mystery",
        MinPlayers = 2,
        MaxPlayers = 4,
        Setting = new Setting { Place = "Test Hall", Era = "1920s" },
        Victim = new Victim { Name = "Lord Victim" },
        Characters =
        [
            Character("butler", "Mr. Butler", required: true, secrets: [("butler-s1", "BUTLER_SECRET_START", 0), ("butler-s2", "BUTLER_SECRET_ACT2", 2)]),
            Character("maid", "Ms. Maid", required: true, secrets: [("maid-s1", "MAID_SECRET_START", 0)]),
            Character("cook", "Chef Cook", required: true, secrets: [("cook-s1", "COOK_SECRET_START", 0)]),
            Character("guest", "Dr. Guest", required: false, secrets: [("guest-s1", "GUEST_SECRET_START", 0)]),
        ],
        Clues =
        [
            new Clue { Id = "c1", Title = "Muddy boots", Text = "PUBLIC_CLUE_1", Act = 1, PointsTo = ["cook"] },
            new Clue { Id = "c2", Title = "A letter", Text = "PRIVATE_CLUE_TO_MAID", Act = 1, Visibility = ClueVisibility.Private, Recipient = "maid", PointsTo = ["cook"] },
            new Clue { Id = "c3", Title = "Torn glove", Text = "MIDWAY_CLUE_1", Act = 1, Wave = ClueWave.Midway, PointsTo = ["butler"], RedHerring = true },
            new Clue
            {
                Id = "c4", Title = "Cipher", Text = "CIPHER_TEXT", Act = 2, PointsTo = ["cook"],
                Puzzle = new Puzzle { Prompt = "What has hands but cannot clap?", Answers = ["a clock", "clock"], SolvedText = "PUZZLE_SOLVED_TEXT" },
            },
            new Clue { Id = "c5", Title = "Guest's note", Text = "PRIVATE_CLUE_TO_GUEST", Act = 2, Visibility = ClueVisibility.Private, Recipient = "guest", PointsTo = ["maid"] },
            new Clue { Id = "c6", Title = "Flour on the stairs", Text = "PUBLIC_CLUE_2", Act = 2, Wave = ClueWave.Midway, PointsTo = ["cook"] },
        ],
        Prologue = [new Cue { Type = CueType.Narration, Text = "A scream!" }],
        Acts =
        [
            new Act { Id = "act1", Title = "Act One", MingleMinutes = 20, Cues = [new Cue { Type = CueType.Narration, Text = "Act one begins." }] },
            new Act { Id = "act2", Title = "Act Two", MingleMinutes = 10 },
        ],
        Accusation = new AccusationOptions
        {
            Motives = [new Option { Id = "money", Text = "Money" }, new Option { Id = "love", Text = "Love" }],
            Methods = [new Option { Id = "poison", Text = "Poison" }, new Option { Id = "knife", Text = "Knife" }],
        },
        Solution = new Solution
        {
            MurdererId = "cook",
            MotiveId = "money",
            MethodId = "poison",
            Explanation = ["SOLUTION_PARAGRAPH_1", "SOLUTION_PARAGRAPH_2"],
            Timeline = [new TimelineEntry { Time = "9pm", Event = "SOLUTION_TIMELINE" }],
        },
    };

    private static Character Character(string id, string name, bool required, (string Id, string Text, int Act)[] secrets) => new()
    {
        Id = id,
        Name = name,
        Required = required,
        PublicBio = $"{name} bio",
        Private = new CharacterPrivate
        {
            Backstory = $"{id.ToUpperInvariant()}_BACKSTORY",
            Alibi = $"{id.ToUpperInvariant()}_ALIBI",
            Objectives = [$"{id.ToUpperInvariant()}_OBJECTIVE"],
            Secrets = secrets.Select(x => new Secret { Id = x.Id, Text = x.Text, UnlockAct = x.Act }).ToList(),
            Lines = new() { ["act1"] = [$"{id.ToUpperInvariant()}_LINE_ACT1"] },
        },
    };

    public static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    public static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    public static readonly Guid Cara = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    /// <summary>Alice plays the maid, Bob the cook (the murderer), Cara the butler. The guest is absent.</summary>
    public static (GameState State, Scenario Scenario) StartedGame()
    {
        var scenario = Create();
        var s = GameEngine.NewGame();
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Alice, "Alice", IsHost: true, IsLocal: false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Bob, "Bob", false, false));
        s = GameEngine.Apply(s, scenario, new AddPlayer(T0, Cara, "Cara", false, false));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Alice, "maid"));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Bob, "cook"));
        s = GameEngine.Apply(s, scenario, new ChooseCharacter(T0, Cara, "butler"));
        s = GameEngine.Apply(s, scenario, new StartGame(T0));
        return (s, scenario);
    }

    public static GameState AdvanceTimes(GameState s, Scenario scenario, int times, DateTimeOffset? now = null)
    {
        for (var i = 0; i < times; i++) s = GameEngine.Apply(s, scenario, new Advance(now ?? T0));
        return s;
    }
}
