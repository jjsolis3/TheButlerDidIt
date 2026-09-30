using ButlerDidIt.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ButlerDidIt.Ai.LiveTests;

/// <summary>
/// Which real services to check, from environment variables. Nothing is read from the app's database or
/// appsettings: a live run only ever uses the keys you set for it, and only for the length of the run.
/// </summary>
public static class Live
{
    public const string AnthropicKey = "LIVE_ANTHROPIC_KEY";
    public const string OpenAiKey = "LIVE_OPENAI_KEY";
    public const string GeminiKey = "LIVE_GEMINI_KEY";
    public const string OllamaUrl = "LIVE_OLLAMA_URL";

    /// <summary>Also run the checks that write a whole mystery or escape room: a few minutes and roughly $0.50–$2 per provider.</summary>
    public const string Full = "LIVE_FULL";

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static string? Key(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public static AiProviderSettings Anthropic => new(Guid.NewGuid(), "Claude (live)", AiProviderKind.Anthropic, null, Key(AnthropicKey));
    public static AiProviderSettings OpenAi => new(Guid.NewGuid(), "OpenAI (live)", AiProviderKind.OpenAI, null, Key(OpenAiKey));
    public static AiProviderSettings Gemini => new(Guid.NewGuid(), "Gemini (live)", AiProviderKind.Gemini, null, Key(GeminiKey));
    public static AiProviderSettings Ollama => new(Guid.NewGuid(), "Ollama (live)", AiProviderKind.Ollama, Key(OllamaUrl), null);

    // The model each check uses. Override any of them to test the model you run in production.
    public static string AnthropicModel => Env("LIVE_ANTHROPIC_MODEL", "claude-opus-5-5");
    public static string AnthropicFallbackModel => Env("LIVE_ANTHROPIC_FALLBACK_MODEL", "claude-opus-4-8");
    public static string OpenAiModel => Env("LIVE_OPENAI_MODEL", "gpt-5-mini");
    public static string OpenAiSpeechModel => Env("LIVE_OPENAI_TTS_MODEL", "tts-1");
    public static string OpenAiImageModel => Env("LIVE_OPENAI_IMAGE_MODEL", "gpt-image-1");
    public static string GeminiModel => Env("LIVE_GEMINI_MODEL", "gemini-2.5-flash");
    public static string GeminiSpeechModel => Env("LIVE_GEMINI_TTS_MODEL", "gemini-2.5-flash-preview-tts");
    public static string GeminiImageModel => Env("LIVE_GEMINI_IMAGE_MODEL", "gemini-2.5-flash-image");
    public static string OllamaModel => Env("LIVE_OLLAMA_MODEL", "llama3.1");
}

/// <summary>A test that runs only when every one of its environment variables is set; otherwise it's reported as skipped, with how to run it.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(params string[] variables)
    {
        var missing = variables.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
        if (missing.Count > 0) Skip = $"Set {string.Join(" and ", missing)} to run this live check (docs/verifying-providers.md).";
    }
}

/// <summary>The gateway's settings, usage log and budget for a live run: one role, pointed at the provider being checked, with no budget cap.</summary>
public sealed class LiveAi(AiProviderSettings provider, string model, string? effort = null, string? fallback = null)
    : IAiSettingsSource, IAiUsageSink, IAiBudget
{
    public List<AiUsageRecord> Usage { get; } = [];

    public Task<AiRoleSettings?> GetRoleAsync(AiRole role, CancellationToken ct) =>
        Task.FromResult<AiRoleSettings?>(new AiRoleSettings(role, provider, model, null, null, effort, fallback));

    public Task RecordAsync(AiUsageRecord record, CancellationToken ct)
    {
        lock (Usage) Usage.Add(record);
        return Task.CompletedTask;
    }

    public Task EnsureWithinBudgetAsync(string hostUserId, CancellationToken ct) => Task.CompletedTask;

    public AiGateway Gateway() => new(this, new ChatClientFactory(allowFake: false), this, this, TimeProvider.System, NullLogger<AiGateway>.Instance);

    public static readonly AiCallContext Context = new("live-check");

    public long InputTokens => Usage.Sum(u => u.InputTokens);
    public long OutputTokens => Usage.Sum(u => u.OutputTokens);
}

/// <summary>
/// One line per check, printed with the test and appended to live-report.md (next to the test binaries, or
/// wherever LIVE_REPORT points), ready to paste into #25 / #32 / #63.
/// </summary>
public static class Report
{
    private static readonly object Gate = new();

    public static string PathName => Environment.GetEnvironmentVariable("LIVE_REPORT") is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "live-report.md");

    public static void Add(ITestOutputHelper output, string provider, string check, string model, string result, LiveAi? ai = null, long? ms = null)
    {
        var tokens = ai is null ? "" : $"{ai.InputTokens} in / {ai.OutputTokens} out";
        var line = $"| {provider} | {check} | `{model}` | {result} | {tokens} | {(ms is { } t ? $"{t} ms" : "")} |";
        output.WriteLine(line);
        lock (Gate)
        {
            if (!File.Exists(PathName)) File.WriteAllText(PathName, "| Provider | Check | Model | Result | Tokens | Time |\n|---|---|---|---|---|---|\n");
            File.AppendAllText(PathName, line + "\n");
        }
    }
}
