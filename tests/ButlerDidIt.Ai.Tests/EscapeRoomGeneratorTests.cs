using System.Text.Json.Nodes;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Ai.Tests;

public class EscapeRoomGeneratorTests
{
    private static readonly EscapeRoomRequest Request = new("a haunted lighthouse", ContentRating.Family, 30);
    private const string SolvedBoth = """{"answers":{"1":"echo","2":"a map"}}""";

    private static string FakeRoom() =>
        new StreamReader(typeof(EscapeRoomGenerator).Assembly.GetManifestResourceStream("ButlerDidIt.Ai.Fake.FakeEscapeRoom.json")!).ReadToEnd();

    private static string Edit(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(FakeRoom())!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    private static Task<EscapeRoomResult> Generate(TestAi ai) =>
        new EscapeRoomGenerator(ai.Gateway()).GenerateAsync(Request, TestAi.Context, null, CancellationToken.None);

    [Fact]
    public async Task Fake_provider_writes_a_valid_room_with_the_requested_settings()
    {
        var ai = new TestAi();
        var result = await Generate(ai);

        var room = result.Room;
        Assert.Empty(EscapeRoomValidator.Validate(room));
        Assert.Empty(EscapeRoomGenerator.ShapeErrors(room));
        Assert.Empty(result.Warnings);
        Assert.StartsWith("ai-escape-the-fake-lighthouse-", room.Id);
        Assert.Equal(ContentRating.Family, room.ContentRating);
        Assert.Equal(30, room.TimeLimitMinutes);
        Assert.Equal(90, room.HintPenaltySeconds);
        Assert.Equal("ai", room.Theme);
        Assert.Equal("Keeper Barnacle", room.GameMaster?.Name);
        // Puzzle ids reach the browsers, so the model's own names ("echo-riddle") are replaced.
        Assert.Equal(["puzzle-1", "puzzle-2"], room.Stages[0].Puzzles);
        Assert.All(room.Puzzles, p => Assert.Matches(@"^puzzle-\d$", p.Id));
        Assert.Contains(ai.Usage, u => u.Context.Purpose == "escape-room-solve");
    }

    [Fact]
    public async Task Server_settings_override_whatever_the_model_chose()
    {
        var mature = Edit(n =>
        {
            n["contentRating"] = "mature";
            n["timeLimitMinutes"] = 5;
            n["hintPenaltySeconds"] = 1;
            n["maxPlayers"] = 50;
            n["puzzles"]![0]!["variants"] = JsonNode.Parse("""[{}, {"answers": ["other"]}]""");
        });
        var ai = new TestAi { Client = new ScriptedChatClient(mature, SolvedBoth) };
        var room = (await new EscapeRoomGenerator(ai.Gateway()).GenerateAsync(new("a spaceship", ContentRating.Family, 60), TestAi.Context, null, CancellationToken.None)).Room;

        Assert.Equal(ContentRating.Family, room.ContentRating);
        Assert.Equal(60, room.TimeLimitMinutes);
        Assert.Equal(120, room.HintPenaltySeconds);
        Assert.Equal(8, room.MaxPlayers);
        Assert.All(room.Puzzles, p => Assert.Empty(p.Variants));
    }

    [Fact]
    public async Task Validation_errors_are_sent_back_and_fixed()
    {
        var broken = Edit(n => n["puzzles"]![1]!["requires"] = new JsonArray("no-such-key"));
        var client = new ScriptedChatClient(broken, FakeRoom(), SolvedBoth);
        var result = await Generate(new TestAi { Client = client });

        Assert.Empty(EscapeRoomValidator.Validate(result.Room));
        // The repair request quoted the validator's complaint back to the model, with the TASK line.
        var repair = client.Calls[1].Last().Text!;
        Assert.StartsWith($"TASK: {EscapeRoomGenerator.WriteTask}", repair);
        Assert.Contains("no-such-key", repair);
    }

    [Fact]
    public async Task A_fixed_code_written_by_the_model_is_refused()
    {
        var madeUpCode = Edit(n =>
        {
            var lockbox = n["puzzles"]![2]!.AsObject();
            lockbox.Remove("generator");
            lockbox["answers"] = new JsonArray("123");
        });
        var client = new ScriptedChatClient(madeUpCode, FakeRoom(), SolvedBoth);
        await Generate(new TestAi { Client = client });

        Assert.Contains("codes must use", client.Calls[1].Last().Text);
    }

    [Fact]
    public void Shape_rules_catch_a_riddle_that_gives_itself_away()
    {
        var leaky = GameJson.Deserialize<EscapeRoom>(Edit(n => n["puzzles"]![0]!["prompt"] = "Shout and hear your echo come back. What is it?"));
        Assert.Contains(EscapeRoomGenerator.ShapeErrors(leaky), e => e.Contains("echo-riddle") && e.Contains("gives its own answer away"));
    }

    [Fact]
    public async Task Gives_up_cleanly_after_repeated_failures()
    {
        var ai = new TestAi { Client = new ScriptedChatClient("not json at all") };
        await Assert.ThrowsAsync<AiCallFailedException>(() => Generate(ai));
    }

    [Fact]
    public async Task A_missed_riddle_gets_one_repair_then_a_warning()
    {
        var wrong = """{"answers":{"1":"a parrot","2":"map"}}""";
        var client = new ScriptedChatClient(FakeRoom(), wrong, FakeRoom(), wrong);
        var result = await Generate(new TestAi { Client = client });

        Assert.Contains("a parrot", client.Calls[2].Last().Text);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("The Whispering Wall", warning);
    }

    [Fact]
    public async Task A_failing_tester_does_not_throw_away_a_valid_room()
    {
        var result = await Generate(new TestAi { Client = new ScriptedChatClient(FakeRoom(), "no json here") });
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_tester_sees_riddles_and_their_pieces_but_never_answers_or_hints()
    {
        var room = GameJson.Deserialize<EscapeRoom>(Edit(n => n["puzzles"]![3]!["pieces"] = new JsonArray("The paper has a blue edge.")));
        var prompt = EscapeRoomSolver.Prompt(room);

        Assert.StartsWith($"TASK: {EscapeRoomGenerator.SolveTask}", prompt);
        Assert.Contains("I speak without a mouth", prompt);
        Assert.Contains("The paper has a blue edge.", prompt);
        Assert.DoesNotContain("echo-riddle", prompt);
        foreach (var riddle in room.Puzzles.Where(EscapeRoomGenerator.IsRiddle))
        {
            Assert.All(riddle.Answers, a => Assert.DoesNotContain(a, prompt, StringComparison.OrdinalIgnoreCase));
            Assert.All(riddle.Hints, h => Assert.DoesNotContain(h, prompt));
        }
        // Codes and passwords come from generators and aren't the tester's job.
        Assert.DoesNotContain("lockbox", prompt);
    }
}
