using System.Diagnostics;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game.Scenarios;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace ButlerDidIt.Ai.LiveTests;

/// <summary>
/// The #25 checklist, the same for every chat provider: it connects and counts tokens, plays a character,
/// returns JSON the game can parse, streams a long answer, and (with LIVE_FULL) writes a whole escape
/// room and mystery that pass validation. Each provider's class below runs these against its own API.
/// </summary>
public static class ChatChecks
{
    private static readonly string[] Suspects = ["colonel", "violet", "butler"];

    public static async Task Connects(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        var ai = new LiveAi(provider, model);
        var watch = Stopwatch.StartNew();
        var text = await ai.Gateway().CompleteAsync(AiRole.Actor, "You are helping test a connection.",
            [new ChatMessage(ChatRole.User, "Reply with the single word OK.")], LiveAi.Context, 64);
        Report.Add(output, name, "connects", model, $"replied \"{Short(text)}\"", ai, watch.ElapsedMilliseconds);
        Assert.Contains("ok", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(ai.InputTokens > 0 && ai.OutputTokens > 0, "the usage log records real token counts, not 0");
    }

    public static async Task PlaysACharacter(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        var ai = new LiveAi(provider, model);
        var watch = Stopwatch.StartNew();
        var text = await ai.Gateway().CompleteAsync(AiRole.Actor,
            "You are Lady Violet Ashcombe, a haughty dowager at a 1920s country-house murder mystery party. " +
            "Stay in character. Answer in one or two sentences.",
            [new ChatMessage(ChatRole.User, "Where were you at nine o'clock last night?")], LiveAi.Context, 300);
        Report.Add(output, name, "plays a character", model, $"\"{Short(text)}\"", ai, watch.ElapsedMilliseconds);
        Assert.InRange(text.Length, 10, 800);
        Assert.DoesNotContain("as an ai", text, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task ReturnsJson(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        var ai = new LiveAi(provider, model);
        var watch = Stopwatch.StartNew();
        var verdict = await ai.Gateway().CompleteJsonAsync<Verdict>(AiRole.Inspector,
            "You are the detective in a party murder mystery. The colonel was seen leaving the library, where the victim was found, " +
            "with ink on his cuff; the victim was killed with a letter opener from the library desk. Violet and the butler were in the kitchen together. " +
            "Reply with JSON only: {\"suspectId\": \"colonel\" | \"violet\" | \"butler\", \"reasoning\": \"one sentence\"}",
            [new ChatMessage(ChatRole.User, "Who did it?")], LiveAi.Context, 800);
        Report.Add(output, name, "returns JSON", model, $"suspectId={verdict.SuspectId}", ai, watch.ElapsedMilliseconds);
        Assert.Contains(verdict.SuspectId, Suspects);
    }

    public static async Task StreamsALongAnswer(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        var ai = new LiveAi(provider, model);
        var pieces = 0;
        var watch = Stopwatch.StartNew();
        var text = await ai.Gateway().CompleteAsync(AiRole.Storyteller, "You write atmospheric party-game prose.",
            [new ChatMessage(ChatRole.User, "Describe a stormy manor house at midnight in about 250 words.")], LiveAi.Context, 2_000,
            onText: _ => { pieces++; return Task.CompletedTask; });
        Report.Add(output, name, "streams", model, $"{text.Split(' ').Length} words in {pieces} pieces", ai, watch.ElapsedMilliseconds);
        Assert.True(pieces > 1, "the answer arrives in pieces, so the screen can show it being written");
        Assert.True(text.Split(' ').Length > 100);
    }

    /// <summary>A whole escape room, in the format taught in #86: every kind of puzzle, validated at every seed.</summary>
    public static async Task WritesAnEscapeRoom(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        var ai = new LiveAi(provider, model);
        var watch = Stopwatch.StartNew();
        var result = await new EscapeRoomGenerator(ai.Gateway()).GenerateAsync(
            new EscapeRoomRequest("a haunted lighthouse", ContentRating.Family, 30), LiveAi.Context, null, CancellationToken.None);
        var calls = ai.Usage.Count;
        Report.Add(output, name, "writes an escape room", model,
            $"\"{result.Room.Title}\": {result.Room.Puzzles.Count} puzzles, {calls} calls, {result.Warnings.Count} warnings", ai, watch.ElapsedMilliseconds);
        Assert.Empty(EscapeRoomValidator.Validate(result.Room));
        Assert.Empty(EscapeRoomGenerator.ShapeErrors(result.Room));
    }

    public static async Task WritesAMystery(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        var ai = new LiveAi(provider, model);
        var watch = Stopwatch.StartNew();
        var theme = new ThemeDefinition { Slug = "speakeasy", Name = "Death at the Gin Joint", Era = "1920s" };
        var result = await new MysteryGenerator(ai.Gateway()).GenerateAsync(
            new GenerationRequest(theme, 4, ContentRating.Family, MysteryLength.Short, null), LiveAi.Context, null, CancellationToken.None);
        Report.Add(output, name, "writes a mystery", model,
            $"\"{result.Scenario.Title}\": {ai.Usage.Count} calls, {result.Warnings.Count} warnings", ai, watch.ElapsedMilliseconds);
        Assert.Empty(ScenarioValidator.Validate(result.Scenario));
    }

    public static string Short(string text) => text.Length <= 70 ? text.ReplaceLineEndings(" ") : text[..70].ReplaceLineEndings(" ") + "…";

    private sealed class Verdict
    {
        public string SuspectId { get; set; } = "";
        public string Reasoning { get; set; } = "";
    }
}

public class AnthropicLiveTests(ITestOutputHelper output)
{
    private static AiProviderSettings P => Live.Anthropic;
    private const string Name = "Claude";

    [LiveFact(Live.AnthropicKey)] public Task Connects() => ChatChecks.Connects(output, Name, P, Live.AnthropicModel);
    [LiveFact(Live.AnthropicKey)] public Task Plays_a_character() => ChatChecks.PlaysACharacter(output, Name, P, Live.AnthropicModel);
    [LiveFact(Live.AnthropicKey)] public Task Returns_json() => ChatChecks.ReturnsJson(output, Name, P, Live.AnthropicModel);
    [LiveFact(Live.AnthropicKey)] public Task Streams_a_long_answer() => ChatChecks.StreamsALongAnswer(output, Name, P, Live.AnthropicModel);
    [LiveFact(Live.AnthropicKey, Live.Full)] public Task Writes_an_escape_room() => ChatChecks.WritesAnEscapeRoom(output, Name, P, Live.AnthropicModel);
    [LiveFact(Live.AnthropicKey, Live.Full)] public Task Writes_a_mystery() => ChatChecks.WritesAMystery(output, Name, P, Live.AnthropicModel);

    /// <summary>#63: every effort level is accepted, and the report shows whether low effort answers faster.</summary>
    [LiveFact(Live.AnthropicKey)]
    public async Task Effort_levels_are_accepted()
    {
        foreach (var effort in new string?[] { null, "low", "medium", "high", "xhigh" })
        {
            var ai = new LiveAi(P, Live.AnthropicModel, effort);
            var watch = Stopwatch.StartNew();
            var text = await ai.Gateway().CompleteAsync(AiRole.Actor,
                "You are Lady Violet Ashcombe at a 1920s murder mystery party. Answer in character in one sentence.",
                [new ChatMessage(ChatRole.User, "Did you like the victim?")], LiveAi.Context, 400);
            Report.Add(output, Name, $"effort {effort ?? "default"}", Live.AnthropicModel, $"\"{ChatChecks.Short(text)}\"", ai, watch.ElapsedMilliseconds);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    /// <summary>#63: a role with a refusal fallback goes through the beta messages API; an ordinary request still works there.</summary>
    [LiveFact(Live.AnthropicKey)]
    public async Task A_role_with_a_refusal_fallback_still_answers()
    {
        var ai = new LiveAi(P, Live.AnthropicModel, fallback: Live.AnthropicFallbackModel);
        var watch = Stopwatch.StartNew();
        var text = await ai.Gateway().CompleteAsync(AiRole.Storyteller, "You are helping test a connection.",
            [new ChatMessage(ChatRole.User, "Reply with the single word OK.")], LiveAi.Context, 64);
        Report.Add(output, Name, $"fallback {Live.AnthropicFallbackModel} set", Live.AnthropicModel, $"replied \"{ChatChecks.Short(text)}\"", ai, watch.ElapsedMilliseconds);
        Assert.Contains("ok", text, StringComparison.OrdinalIgnoreCase);
    }
}

public class OpenAiLiveTests(ITestOutputHelper output)
{
    private static AiProviderSettings P => Live.OpenAi;
    private const string Name = "OpenAI";

    [LiveFact(Live.OpenAiKey)] public Task Connects() => ChatChecks.Connects(output, Name, P, Live.OpenAiModel);
    [LiveFact(Live.OpenAiKey)] public Task Plays_a_character() => ChatChecks.PlaysACharacter(output, Name, P, Live.OpenAiModel);
    [LiveFact(Live.OpenAiKey)] public Task Returns_json() => ChatChecks.ReturnsJson(output, Name, P, Live.OpenAiModel);
    [LiveFact(Live.OpenAiKey)] public Task Streams_a_long_answer() => ChatChecks.StreamsALongAnswer(output, Name, P, Live.OpenAiModel);
    [LiveFact(Live.OpenAiKey, Live.Full)] public Task Writes_an_escape_room() => ChatChecks.WritesAnEscapeRoom(output, Name, P, Live.OpenAiModel);
    [LiveFact(Live.OpenAiKey, Live.Full)] public Task Writes_a_mystery() => ChatChecks.WritesAMystery(output, Name, P, Live.OpenAiModel);
}

public class GeminiLiveTests(ITestOutputHelper output)
{
    private static AiProviderSettings P => Live.Gemini;
    private const string Name = "Gemini";

    [LiveFact(Live.GeminiKey)] public Task Connects() => ChatChecks.Connects(output, Name, P, Live.GeminiModel);
    [LiveFact(Live.GeminiKey)] public Task Plays_a_character() => ChatChecks.PlaysACharacter(output, Name, P, Live.GeminiModel);
    [LiveFact(Live.GeminiKey)] public Task Returns_json() => ChatChecks.ReturnsJson(output, Name, P, Live.GeminiModel);
    [LiveFact(Live.GeminiKey)] public Task Streams_a_long_answer() => ChatChecks.StreamsALongAnswer(output, Name, P, Live.GeminiModel);
    [LiveFact(Live.GeminiKey, Live.Full)] public Task Writes_an_escape_room() => ChatChecks.WritesAnEscapeRoom(output, Name, P, Live.GeminiModel);
    [LiveFact(Live.GeminiKey, Live.Full)] public Task Writes_a_mystery() => ChatChecks.WritesAMystery(output, Name, P, Live.GeminiModel);
}

public class OllamaLiveTests(ITestOutputHelper output)
{
    private static AiProviderSettings P => Live.Ollama;
    private const string Name = "Ollama";

    [LiveFact(Live.OllamaUrl)] public Task Connects() => ChatChecks.Connects(output, Name, P, Live.OllamaModel);
    [LiveFact(Live.OllamaUrl)] public Task Plays_a_character() => ChatChecks.PlaysACharacter(output, Name, P, Live.OllamaModel);
    [LiveFact(Live.OllamaUrl)] public Task Returns_json() => ChatChecks.ReturnsJson(output, Name, P, Live.OllamaModel);
    [LiveFact(Live.OllamaUrl)] public Task Streams_a_long_answer() => ChatChecks.StreamsALongAnswer(output, Name, P, Live.OllamaModel);
    [LiveFact(Live.OllamaUrl, Live.Full)] public Task Writes_an_escape_room() => ChatChecks.WritesAnEscapeRoom(output, Name, P, Live.OllamaModel);
    [LiveFact(Live.OllamaUrl, Live.Full)] public Task Writes_a_mystery() => ChatChecks.WritesAMystery(output, Name, P, Live.OllamaModel);
}
