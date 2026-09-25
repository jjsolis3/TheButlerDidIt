using System.ComponentModel.DataAnnotations;
using ButlerDidIt.Ai;

namespace ButlerDidIt.Api.Data;

/// <summary>A configured AI service (an Anthropic account, an OpenAI key, a local Ollama server…).</summary>
public sealed class AiProviderEntity
{
    public Guid Id { get; set; }

    [MaxLength(80)]
    public required string Name { get; set; }

    public AiProviderKind Kind { get; set; }

    [MaxLength(300)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The API key, encrypted with ASP.NET Data Protection. Anyone reading the
    /// database (or a backup of it) sees only ciphertext; decrypting it needs the
    /// keys in the app's `keys` volume.
    /// </summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>Defined by environment variables; re-applied at every startup.</summary>
    public bool FromConfig { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Which provider and model does each job (Storyteller, Actor, Inspector).</summary>
public sealed class AiRoleEntity
{
    [Key]
    public AiRole Role { get; set; }

    public Guid ProviderId { get; set; }
    public AiProviderEntity? Provider { get; set; }

    [MaxLength(120)]
    public required string Model { get; set; }

    public int? MaxOutputTokens { get; set; }
    public float? Temperature { get; set; }
}

/// <summary>Admin-maintained price list, used to estimate what each call cost.</summary>
public sealed class AiModelPriceEntity
{
    [Key, MaxLength(120)]
    public required string Model { get; set; }

    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }

    /// <summary>A flat price per call, e.g. per generated image.</summary>
    public decimal PerRequest { get; set; }
}

/// <summary>One row per AI call: the basis for cost reports and monthly budgets.</summary>
public sealed class AiUsageEntity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public AiRole Role { get; set; }
    public AiProviderKind ProviderKind { get; set; }

    [MaxLength(80)]
    public string ProviderName { get; set; } = "";

    [MaxLength(120)]
    public string Model { get; set; } = "";

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }

    /// <summary>False when no price was set for the model, so the cost shown is 0 but unknown.</summary>
    public bool PriceKnown { get; set; }

    public long DurationMs { get; set; }
    public bool Success { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    [MaxLength(450)]
    public string HostUserId { get; set; } = "";

    public Guid? PartyId { get; set; }
    public Guid? JobId { get; set; }

    [MaxLength(60)]
    public string Purpose { get; set; } = "";
}

public enum GenerationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>A request to write a new mystery, processed in the background by GenerationWorker.</summary>
public sealed class GenerationJobEntity
{
    public Guid Id { get; set; }

    [MaxLength(450)]
    public required string HostUserId { get; set; }

    [MaxLength(80)]
    public required string ThemeSlug { get; set; }

    /// <summary>The GenerationRequest options as jsonb.</summary>
    public required string Request { get; set; }

    public GenerationStatus Status { get; set; }

    [MaxLength(300)]
    public string Progress { get; set; } = "";

    [MaxLength(120)]
    public string? ScenarioId { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    /// <summary>Warnings about the finished mystery (e.g. "very hard to solve"), as jsonb.</summary>
    public string Warnings { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
