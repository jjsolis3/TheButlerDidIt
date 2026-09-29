namespace ButlerDidIt.Escape.Engine;

public enum EscapePhase
{
    Lobby,
    Playing,
    Escaped,
    Failed,
}

/// <summary>
/// Everything about one escape attempt, saved as JSON in Party.State after every command.
/// The engine never changes a state in place: it works on a copy and returns it.
/// </summary>
public sealed class EscapeState
{
    /// <summary>Goes up with every change, so screens can ignore late, out-of-order updates.</summary>
    public int Version { get; set; }
    public EscapePhase Phase { get; set; }

    /// <summary>
    /// Which puzzle set this attempt plays: the room's variants and generated codes are all
    /// picked from it (see RoomVariants). Never sent to browsers during the game, since the
    /// content is open and the answers could be worked out from it; shown once it's over.
    /// </summary>
    public long Seed { get; set; }

    /// <summary>Today's challenge: every group plays the same puzzle set today and shares a leaderboard.</summary>
    public bool Daily { get; set; }
    public List<EscapePlayer> Players { get; set; } = [];

    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the clock runs out. Every hint moves it earlier.</summary>
    public DateTimeOffset? Deadline { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Index into the room's stages: the one the group is working on.</summary>
    public int StageIndex { get; set; }

    public List<SolvedPuzzle> Solved { get; set; } = [];
    public List<string> Inventory { get; set; } = [];

    /// <summary>How many of each puzzle's hints have been revealed.</summary>
    public Dictionary<string, int> HintsShown { get; set; } = [];
    public int WrongAttempts { get; set; }

    /// <summary>A wrong answer locks that puzzle for a few seconds, so a code can't be guessed by a script.</summary>
    public Dictionary<string, DateTimeOffset> LockedUntil { get; set; } = [];

    /// <summary>Which phone holds each clue piece, dealt when the clock starts.</summary>
    public List<PieceHolder> Pieces { get; set; } = [];

    /// <summary>The latest events, newest last, for the TV's ticker.</summary>
    public List<EscapeFeedEntry> Feed { get; set; } = [];

    public EscapePlayer? FindPlayer(Guid seatId) => Players.FirstOrDefault(p => p.SeatId == seatId);
    public bool IsSolved(string puzzleId) => Solved.Any(s => s.PuzzleId == puzzleId);
    public int HintsUsed => HintsShown.Values.Sum();
}

public sealed class EscapePlayer
{
    public Guid SeatId { get; set; }
    public string Name { get; set; } = "";
    public bool IsHost { get; set; }
    public bool IsLocal { get; set; }
    public string? PhotoUrl { get; set; }
}

public sealed record SolvedPuzzle(string PuzzleId, string SolvedBy, DateTimeOffset At);
public sealed record PieceHolder(string PuzzleId, int Index, Guid SeatId);
public sealed record EscapeFeedEntry(DateTimeOffset At, string Text);
