namespace ButlerDidIt.Ai;

/// <summary>Which company's API (or local server) a provider talks to.</summary>
public enum AiProviderKind
{
    Anthropic,
    OpenAI,

    /// <summary>Google Gemini, reached through its OpenAI-compatible endpoint.</summary>
    Gemini,

    /// <summary>Local models served by Ollama (no API key, no per-token cost).</summary>
    Ollama,

    /// <summary>Canned answers for automated tests. Only allowed when Ai:AllowFakeProvider is true.</summary>
    Fake,

    /// <summary>ElevenLabs: expressive voices. Voices only (#33).</summary>
    ElevenLabs,

    /// <summary>A local Piper text-to-speech server: free voices, no key. Voices only (#33).</summary>
    Piper,

    /// <summary>A local Stable Diffusion WebUI (AUTOMATIC1111 or Forge, started with --api): free pictures, no key. Pictures only (#33).</summary>
    StableDiffusion,
}

/// <summary>What each kind of provider can do: chat, voices, pictures. The admin page only offers a provider for a role it can do.</summary>
public static class AiProviderAbilities
{
    public static bool Chats(AiProviderKind kind) => kind is not (AiProviderKind.ElevenLabs or AiProviderKind.Piper or AiProviderKind.StableDiffusion);
    public static bool Speaks(AiProviderKind kind) => kind is AiProviderKind.OpenAI or AiProviderKind.Gemini or AiProviderKind.ElevenLabs or AiProviderKind.Piper or AiProviderKind.Fake;
    public static bool Paints(AiProviderKind kind) => kind is AiProviderKind.OpenAI or AiProviderKind.Gemini or AiProviderKind.StableDiffusion or AiProviderKind.Fake;

    /// <summary>Video from a picture: OpenAI's Sora and Google's Veo.</summary>
    public static bool Films(AiProviderKind kind) => kind is AiProviderKind.OpenAI or AiProviderKind.Gemini or AiProviderKind.Fake;

    /// <summary>Whether a provider of this kind can do the role's job.</summary>
    public static bool Can(AiProviderKind kind, AiRole role) => role switch
    {
        AiRole.Voice => Speaks(kind),
        AiRole.Illustrator => Paints(kind),
        AiRole.Filmmaker => Films(kind),
        _ => Chats(kind),
    };

    /// <summary>Servers on the host's own machine: no key, and nothing to pay per call.</summary>
    public static bool Local(AiProviderKind kind) => kind is AiProviderKind.Ollama or AiProviderKind.Piper or AiProviderKind.StableDiffusion;
}

/// <summary>
/// The jobs AI does in the game. Each role can use a different provider and
/// model, e.g. a strong model to write mysteries and a fast, cheap one to play NPCs.
/// </summary>
public enum AiRole
{
    /// <summary>Writes new mysteries.</summary>
    Storyteller,

    /// <summary>Plays NPC characters when guests question them.</summary>
    Actor,

    /// <summary>Gives hints, checks generated mysteries are solvable, and delivers verdicts.</summary>
    Inspector,

    /// <summary>Turns narration, NPC lines and answers into speech (text-to-speech).</summary>
    Voice,

    /// <summary>Paints character portraits and scene art.</summary>
    Illustrator,

    /// <summary>Brings an escape room's stage pictures to life as short clips for its reveals (#110). Off unless set up: clips cost far more than pictures.</summary>
    Filmmaker,
}

public sealed record AiProviderSettings(Guid Id, string Name, AiProviderKind Kind, string? BaseUrl, string? ApiKey);

/// <param name="Effort">For Claude: how hard the model thinks (<see cref="AiEffort"/>). Null: the model's default. Other providers ignore it.</param>
/// <param name="RefusalFallbackModel">For Claude: a model that retries a request the first one declined (a safety refusal). Null: no retry.</param>
public sealed record AiRoleSettings(AiRole Role, AiProviderSettings Provider, string Model, int? MaxOutputTokens, float? Temperature,
    string? Effort = null, string? RefusalFallbackModel = null);

/// <summary>
/// Claude's effort levels, as the admin page and configuration write them. Lower effort thinks less:
/// faster and cheaper answers, which suits the Actor's short in-character replies (#63).
/// </summary>
public static class AiEffort
{
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high", "xhigh"];

    /// <summary>The level as Microsoft.Extensions.AI names it, which the Anthropic client sends as <c>output_config.effort</c>. Null when unset or unknown.</summary>
    public static Microsoft.Extensions.AI.ReasoningEffort? Parse(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
        "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
        "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
        "xhigh" => Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh,
        _ => null,
    };

    /// <summary>The level to store: one of <see cref="Levels"/>, or null for blank or unknown.</summary>
    public static string? Normalize(string? level) => Parse(level) is null ? null : level!.Trim().ToLowerInvariant();
}

/// <summary>Who an AI call is for, so usage can be attributed and budgets enforced.</summary>
public sealed record AiCallContext(string HostUserId, Guid? PartyId = null, Guid? JobId = null, string Purpose = "");

public sealed record AiUsageRecord(
    DateTimeOffset At,
    AiRole Role,
    AiProviderKind ProviderKind,
    string ProviderName,
    string Model,
    long InputTokens,
    long OutputTokens,
    long DurationMs,
    bool Success,
    string? Error,
    AiCallContext Context);

/// <summary>Where role settings come from (the database, filled in by the admin page or environment variables).</summary>
public interface IAiSettingsSource
{
    Task<AiRoleSettings?> GetRoleAsync(AiRole role, CancellationToken ct);
}

/// <summary>Records every call so cost can be reported and budgets enforced.</summary>
public interface IAiUsageSink
{
    Task RecordAsync(AiUsageRecord record, CancellationToken ct);
}

public interface IAiBudget
{
    /// <summary>Throws <see cref="AiBudgetExceededException"/> if the host has used their monthly allowance.</summary>
    Task EnsureWithinBudgetAsync(string hostUserId, CancellationToken ct);
}

/// <summary>Base for AI errors whose message is safe to show to players.</summary>
public class AiException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AiUnavailableException(string message) : AiException(message);

public sealed class AiBudgetExceededException(string message) : AiException(message);

public sealed class AiCallFailedException(string message, Exception? inner = null) : AiException(message, inner);

/// <summary>
/// What a model costs. Chat models charge per million tokens. Text-to-speech is
/// usually priced per million characters (the usage log records characters as
/// "input tokens" for voice calls), and image models charge per image (PerRequest).
/// Ollama and unknown models cost 0.
/// </summary>
public sealed record AiModelPrice(decimal InputPerMillion, decimal OutputPerMillion, decimal PerRequest = 0m)
{
    public decimal Cost(long inputTokens, long outputTokens) =>
        (inputTokens * InputPerMillion + outputTokens * OutputPerMillion) / 1_000_000m + PerRequest;
}
