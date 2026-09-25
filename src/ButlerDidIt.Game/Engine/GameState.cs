namespace ButlerDidIt.Game.Engine;

// GameState is everything that changes during a party. It is saved as a single
// JSON document (Postgres jsonb) after every command, so it must stay plain data:
// no methods with side effects, no references to services.

public enum Phase
{
    /// <summary>Guests are joining and picking characters. Only public character info is visible.</summary>
    Lobby,

    /// <summary>The game has started. Everyone reads their dossier; the stage introduces the cast.</summary>
    CastReveal,

    /// <summary>The opening cinematic: the body is discovered.</summary>
    Prologue,

    /// <summary>An act: first a cinematic, then a timed mingle where clues drop.</summary>
    Act,

    /// <summary>Everyone locks in who, why and how.</summary>
    Accusation,

    /// <summary>Step-by-step reveal of the guesses and the solution.</summary>
    Reveal,

    /// <summary>Vote for best performance and best costume.</summary>
    Awards,

    Finished,
}

public enum ActStep
{
    Cinematic,
    Mingle,
}

public sealed class GameState
{
    /// <summary>Increases by one on every successful command. Clients use it to ignore stale updates.</summary>
    public int Version { get; set; }

    public Phase Phase { get; set; } = Phase.Lobby;

    /// <summary>0-based index into Scenario.Acts; -1 before the first act.</summary>
    public int ActIndex { get; set; } = -1;

    public ActStep ActStep { get; set; } = ActStep.Cinematic;

    public Timer Timer { get; set; } = new();

    public List<PlayerState> Players { get; set; } = [];

    /// <summary>Required characters nobody took. The narrator plays them.</summary>
    public List<string> NpcCharacterIds { get; set; } = [];

    public List<DroppedClue> DroppedClues { get; set; } = [];

    /// <summary>Midway clues for the current act that have not dropped yet, in order.</summary>
    public List<string> PendingClueIds { get; set; } = [];

    public List<RevealedSecret> RevealedSecrets { get; set; } = [];

    public List<SolvedPuzzle> SolvedPuzzles { get; set; } = [];

    public Dictionary<Guid, AccusationEntry> Accusations { get; set; } = [];

    /// <summary>seatId → (award id → voted-for seatId).</summary>
    public Dictionary<Guid, Dictionary<string, Guid>> AwardVotes { get; set; } = [];

    /// <summary>How far into the reveal sequence the stage is (see GameEngine.RevealStepCount).</summary>
    public int RevealStep { get; set; }

    /// <summary>A short public log of what just happened, for stage toasts.</summary>
    public List<FeedItem> Feed { get; set; } = [];

    public DateTimeOffset? StartedAt { get; set; }

    public PlayerState? FindPlayer(Guid seatId) => Players.FirstOrDefault(p => p.SeatId == seatId);

    public PlayerState? PlayerFor(string characterId) => Players.FirstOrDefault(p => p.CharacterId == characterId);
}

/// <summary>
/// The act countdown. We store when it ends rather than ticking every second:
/// each browser counts down locally, so the server only broadcasts on changes.
/// </summary>
public sealed class Timer
{
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>When the midway clues drop. Null once they have dropped.</summary>
    public DateTimeOffset? MidwayAt { get; set; }

    /// <summary>When paused, how much time was left (EndsAt/MidwayAt are cleared).</summary>
    public TimeSpan? PausedRemaining { get; set; }

    public TimeSpan? PausedMidwayRemaining { get; set; }

    public bool IsPaused => PausedRemaining is not null;
}

public sealed class PlayerState
{
    public Guid SeatId { get; set; }
    public string Name { get; set; } = "";
    public string? CharacterId { get; set; }
    public bool IsHost { get; set; }

    /// <summary>A pass-and-play seat that lives on the host's device instead of its own phone.</summary>
    public bool IsLocal { get; set; }

    public bool Ready { get; set; }
}

public sealed class DroppedClue
{
    public required string ClueId { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Null for public clues; otherwise the only seat allowed to see it.</summary>
    public Guid? RecipientSeatId { get; set; }

    /// <summary>A private clue its holder chose to show everyone.</summary>
    public bool SharedPublicly { get; set; }
}

public sealed class RevealedSecret
{
    public required string CharacterId { get; set; }
    public required string SecretId { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class SolvedPuzzle
{
    public required string ClueId { get; set; }
    public Guid SolvedBySeatId { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class AccusationEntry
{
    public required string SuspectId { get; set; }
    public required string MotiveId { get; set; }
    public required string MethodId { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class FeedItem
{
    public DateTimeOffset At { get; set; }
    public required string Text { get; set; }
}
