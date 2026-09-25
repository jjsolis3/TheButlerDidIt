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
}

public sealed record AiProviderSettings(Guid Id, string Name, AiProviderKind Kind, string? BaseUrl, string? ApiKey);

public sealed record AiRoleSettings(AiRole Role, AiProviderSettings Provider, string Model, int? MaxOutputTokens, float? Temperature);

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

/// <summary>Price per million tokens. Ollama and unknown models cost 0.</summary>
public sealed record AiModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    public decimal Cost(long inputTokens, long outputTokens) =>
        (inputTokens * InputPerMillion + outputTokens * OutputPerMillion) / 1_000_000m;
}
