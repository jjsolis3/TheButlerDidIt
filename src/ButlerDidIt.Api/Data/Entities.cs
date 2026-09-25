using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace ButlerDidIt.Api.Data;

/// <summary>A host account. Guests never need one; they join with a party code.</summary>
public sealed class AppUser : IdentityUser
{
    [MaxLength(60)]
    public string DisplayName { get; set; } = "";

    public bool IsAdmin { get; set; }
}

public sealed class ThemeEntity
{
    [Key, MaxLength(80)]
    public required string Slug { get; set; }

    [MaxLength(120)]
    public required string Name { get; set; }

    /// <summary>The full theme.json, stored as Postgres jsonb.</summary>
    public required string Document { get; set; }

    public int SortOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ScenarioSource
{
    Handwritten,
    AiGenerated,
}

public sealed class ScenarioEntity
{
    [Key, MaxLength(120)]
    public required string Id { get; set; }

    [MaxLength(80)]
    public required string ThemeSlug { get; set; }

    [MaxLength(200)]
    public required string Title { get; set; }

    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public ButlerDidIt.Game.Scenarios.ContentRating ContentRating { get; set; }
    public ScenarioSource Source { get; set; }

    /// <summary>For AI-generated mysteries: the host who generated it. Only they (and admins) can play it.</summary>
    [MaxLength(450)]
    public string? OwnerUserId { get; set; }

    /// <summary>The complete scenario (including the solution) as jsonb. Never sent to browsers as-is.</summary>
    public required string Document { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum PartyMode
{
    /// <summary>A TV or laptop shows the stage; guests use their phones. The classic dinner party.</summary>
    SharedScreen,

    /// <summary>Guests are on a video call. The host screen-shares the stage; everyone uses their own device.</summary>
    Remote,

    /// <summary>One device is passed around the table.</summary>
    PassAndPlay,
}

public enum PartyStatus
{
    Lobby,
    InProgress,
    Finished,
}

public sealed class Party
{
    public Guid Id { get; set; }

    [MaxLength(8)]
    public required string Code { get; set; }

    [MaxLength(450)]
    public required string HostUserId { get; set; }

    [MaxLength(120)]
    public required string ScenarioId { get; set; }

    public PartyMode Mode { get; set; }
    public ButlerDidIt.Game.Scenarios.ContentRating ContentLevel { get; set; }
    public PartyStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Last time the game state changed. The retention job uses it to find abandoned and old parties.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When the retention job removed this finished party's seats, notes and selfies.
    /// The party row itself is kept so its recap page keeps working.
    /// </summary>
    public DateTimeOffset? PrunedAt { get; set; }

    /// <summary>The engine's GameState as jsonb, rewritten after every command.</summary>
    public required string State { get; set; }

    /// <summary>When the background ticker should next wake this party (midway clue drops). Indexed.</summary>
    public DateTimeOffset? NextDueAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Npgsql maps a uint [Timestamp] property to
    /// Postgres' built-in xmin system column, which changes on every update. If two
    /// requests load the same row and both try to save, the second one fails
    /// instead of silently overwriting the first.
    /// </summary>
    [Timestamp]
    public uint Version { get; set; }

    public List<Seat> Seats { get; set; } = [];
}

/// <summary>
/// A guest's place at a party. The seat token is like a password: we store only
/// its SHA-256 hash, so even someone with a copy of the database can't take over
/// a seat.
/// </summary>
public sealed class Seat
{
    public Guid Id { get; set; }
    public Guid PartyId { get; set; }
    public Party? Party { get; set; }

    [MaxLength(30)]
    public required string DisplayName { get; set; }

    [MaxLength(64)]
    public required string TokenHash { get; set; }

    public bool IsLocal { get; set; }
    public bool IsHost { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class PlayerNote
{
    [Key]
    public Guid SeatId { get; set; }

    [MaxLength(10_000)]
    public string Text { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum MediaKind
{
    Image,
    Audio,
    Video,

    /// <summary>A guest's costume selfie.</summary>
    Photo,
}

/// <summary>A generated (voice clip, picture) or uploaded (selfie) file. The bytes live in the media folder; this row says where.</summary>
public sealed class MediaAsset
{
    public Guid Id { get; set; }
    public MediaKind Kind { get; set; }

    [MaxLength(500)]
    public required string Path { get; set; }

    /// <summary>Hash of the generation inputs, so the same request is never paid for twice.</summary>
    [MaxLength(64)]
    public required string ContentHash { get; set; }

    [MaxLength(60)]
    public string Provider { get; set; } = "";

    public string Prompt { get; set; } = "";

    [MaxLength(60)]
    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// For guest uploads (selfies): the party they belong to, so they can be deleted
    /// with it. Deliberately not a foreign key: deleting a row must also delete the
    /// file on disk, which only the retention job can do.
    /// </summary>
    public Guid? PartyId { get; set; }
}

/// <summary>Which generated file fills which slot of a scenario (see MediaOverlay for the keys).</summary>
public sealed class ScenarioMediaEntity
{
    [MaxLength(120)]
    public required string ScenarioId { get; set; }

    [MaxLength(200)]
    public required string Key { get; set; }

    public Guid AssetId { get; set; }
}

public enum MediaJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>A request to create the voices and pictures for one scenario, processed by MediaWorker.</summary>
public sealed class MediaJobEntity
{
    public Guid Id { get; set; }

    [MaxLength(120)]
    public required string ScenarioId { get; set; }

    /// <summary>Whose budget pays for it.</summary>
    [MaxLength(450)]
    public required string HostUserId { get; set; }

    public MediaJobStatus Status { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
