using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Ai.Prompts;
using Microsoft.Extensions.AI;

namespace ButlerDidIt.Ai.Fake;

/// <summary>
/// A pretend AI for automated tests and demos without an API key. It reads the
/// "TASK: …" line every prompt in this project includes and returns a canned
/// answer of the right shape. It never touches the network, so tests are fast,
/// free and deterministic.
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(list)))
        {
            ModelId = options?.ModelId ?? "fake",
            Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 200 },
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private static string Answer(List<ChatMessage> messages)
    {
        // The most recent TASK line wins (repair turns reuse the scenario task).
        var task = messages.AsEnumerable().Reverse()
            .Select(m => TaskLine().Match(m.Text ?? ""))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "";
        var system = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";

        return task switch
        {
            MysteryGenerator.OutlineTask => """{"title":"The Fake Affair","synopsis":"A test mystery.","murderer":"Colonel Fake"}""",
            MysteryGenerator.ScenarioTask => LoadScenario(),
            MysteryGenerator.SolveTask => """{"suspectId":"colonel","reasoning":"The fake clues say so."}""",
            NpcPrompt.Task => NpcAnswer(system, messages),
            InspectorPrompts.HintTask => "Inspector Graves murmurs: \"Look again at who was seen near the library at nine.\"",
            InspectorPrompts.VerdictTask => Verdicts(system),
            _ => "OK",
        };
    }

    private static string NpcAnswer(string system, List<ChatMessage> messages)
    {
        var name = YouAre().Match(system) is { Success: true } m ? m.Groups[1].Value : "The character";
        var question = messages.LastOrDefault(x => x.Role == ChatRole.User)?.Text ?? "";
        return $"{name} sniffs: \"You ask me that? ({question.Length} characters of impertinence.) I was nowhere near the scene, I assure you.\"";
    }

    private static string Verdicts(string system)
    {
        var count = PlayerLine().Matches(system).Count;
        var items = Enumerable.Range(1, Math.Max(count, 1)).Select(i => $$"""{"player":{{i}},"text":"Fake verdict for guest {{i}}: a bold guess, detective."}""");
        return "{\"verdicts\":[" + string.Join(",", items) + "]}";
    }

    private static string LoadScenario()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ButlerDidIt.Ai.Fake.FakeScenario.json")
            ?? throw new InvalidOperationException("FakeScenario.json is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"TASK:\s*([a-z\-]+)")]
    private static partial Regex TaskLine();

    [GeneratedRegex(@"You are (.+?) \(")]
    private static partial Regex YouAre();

    [GeneratedRegex(@"^\d+\. ", RegexOptions.Multiline)]
    private static partial Regex PlayerLine();
}
