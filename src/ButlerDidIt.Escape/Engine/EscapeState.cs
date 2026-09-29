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

    /// <summary>The game's length in minutes, chosen when the party was made. Null (and in parties from before lengths) is the room's own time limit.</summary>
    public int? Minutes { get; set; }
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

    /// <summary>Which AI features this party uses. All off means the room plays exactly as written.</summary>
    public EscapeAiFeatures Ai { get; set; } = new();

    /// <summary>Moments the AI game master reacts to, newest last. Only recorded when <see cref="EscapeAiFeatures.GameMaster"/> is on.</summary>
    public List<EscapeCue> Cues { get; set; } = [];
    public int NextCueId { get; set; } = 1;

    /// <summary>True once the "five minutes left" moment has passed (or can't happen in this room).</summary>
    public bool LowTimeCued { get; set; }

    /// <summary>Wrong answers in a row, across all puzzles: three make the game master comment.</summary>
    public int WrongStreak { get; set; }

    /// <summary>The last few wrong tries on each puzzle (already public in the feed), so an AI hint can see where the group is stuck.</summary>
    public Dictionary<string, List<string>> RecentWrong { get; set; } = [];

    /// <summary>Hints the AI is writing or has written. A paid hint step without one shows the room's written hint.</summary>
    public List<AiHint> AiHints { get; set; } = [];

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

/// <summary>
/// AI switches for one party. They live in the state, like the mystery's AiFeatures, so the
/// engine can enforce the rules and the screens know what to show, while the engine itself
/// never calls an AI.
/// </summary>
public sealed class EscapeAiFeatures
{
    /// <summary>The game master reacts out loud on the TV.</summary>
    public bool GameMaster { get; set; }

    /// <summary>Hints are written by the AI for where the group is stuck (the written ones are the fallback).</summary>
    public bool Hints { get; set; }

    /// <summary>The game master's lines are also spoken with the Voice role (otherwise the browser voice reads them).</summary>
    public bool Voice { get; set; }
}

public enum CueKind
{
    Start,
    StageOpened,
    Solved,
    WrongStreak,
    LowTime,
    Escaped,
    Failed,
}

/// <summary>Something the game master may react to. Its line (and recording) arrive later, from the AI.</summary>
public sealed class EscapeCue
{
    public int Id { get; set; }
    public CueKind Kind { get; set; }
    public DateTimeOffset At { get; set; }
    public string? PuzzleTitle { get; set; }
    public string? PlayerName { get; set; }
    public string? StageTitle { get; set; }
    public string? Text { get; set; }
    public string? AudioUrl { get; set; }

    /// <summary>Passed over because a newer moment came along before the AI got to it.</summary>
    public bool Skipped { get; set; }
}

/// <summary>An AI hint for step <see cref="Index"/> of a puzzle's hint ladder. Text is null while the AI writes it.</summary>
public sealed class AiHint
{
    public Guid Id { get; set; }
    public string PuzzleId { get; set; } = "";
    public int Index { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Text { get; set; }

    /// <summary>
    /// Still being written. After <see cref="EscapeEngine.AiHintTimeout"/> it counts as failed (the server may have
    /// restarted mid-call), so the written hint shows and the puzzle isn't stuck "thinking" forever.
    /// </summary>
    public bool IsPending(DateTimeOffset now) => Text is null && now - At < EscapeEngine.AiHintTimeout;
}

public sealed record SolvedPuzzle(string PuzzleId, string SolvedBy, DateTimeOffset At);
public sealed record PieceHolder(string PuzzleId, int Index, Guid SeatId);
public sealed record EscapeFeedEntry(DateTimeOffset At, string Text);
