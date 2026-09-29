using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Engine;

// What the screens receive. Built field by field by EscapeProjector, so an answer or a clue
// piece can only reach a browser if a line below copies it on purpose.

/// <summary>The TV: public to everyone in the room.</summary>
public sealed record EscapeStageView(
    int Version,
    EscapePhase Phase,
    string RoomId,
    string RoomTitle,
    string Synopsis,
    string Theme,
    string Intro,
    int TimeLimitMinutes,
    int HintPenaltySeconds,
    int StageNumber,
    int StageCount,
    EscapeStageInfo? Stage,
    IReadOnlyList<EscapePuzzleView> Puzzles,
    IReadOnlyList<EscapeItemView> Inventory,
    IReadOnlyList<EscapePlayerSummary> Players,
    DateTimeOffset? StartedAt,
    DateTimeOffset? Deadline,
    DateTimeOffset? EndedAt,
    DateTimeOffset ServerNow,
    IReadOnlyList<EscapeFeedEntry> Feed,
    int SolvedCount,
    int PuzzleCount,
    int HintsUsed,
    int WrongAttempts,
    /// <summary>The escape or failure text, once the game is over.</summary>
    string? EndText,
    /// <summary>Today's challenge: the same puzzles for every group today.</summary>
    bool Daily,
    /// <summary>Which puzzle set was played, only once the game is over.</summary>
    long? PuzzleSet,
    /// <summary>The AI game master, or null when this party plays without one.</summary>
    EscapeGameMasterView? GameMaster,
    /// <summary>The game master's latest lines, newest last.</summary>
    IReadOnlyList<EscapeNarrationView> Narration,
    /// <summary>The background sound to play now: the current stage's, or the room's.</summary>
    Soundscape Soundscape,
    /// <summary>A generated picture of the stage in front of the group (the room's cover in the lobby and at the end), or null.</summary>
    string? ArtUrl);

public sealed record EscapeGameMasterView(
    string Name,
    ButlerDidIt.Game.Scenarios.VoiceProfile Voice,
    /// <summary>It reacts out loud to what the group does.</summary>
    bool Narrates,
    /// <summary>Its hints are written for where the group is stuck.</summary>
    bool WritesHints,
    /// <summary>Lines come with a recording (otherwise the TV's browser reads them).</summary>
    bool Voiced);

public sealed record EscapeNarrationView(int Id, string Text, string? AudioUrl, DateTimeOffset At);

public sealed record EscapeStageInfo(string Id, string Title, string Description);

public sealed record EscapePuzzleView(
    string Id,
    string Title,
    PuzzleKind Kind,
    string Prompt,
    bool Solved,
    string? SolvedBy,
    string? SolvedText,
    /// <summary>Names of the items the group still needs before trying it.</summary>
    IReadOnlyList<string> Needs,
    /// <summary>Only the hints already paid for.</summary>
    IReadOnlyList<string> Hints,
    int HintsLeft,
    /// <summary>The game master is writing the hint just paid for.</summary>
    bool HintPending,
    DateTimeOffset? LockedUntil,
    /// <summary>How many phones hold a piece of this puzzle.</summary>
    int PieceCount);

public sealed record EscapeItemView(string Id, string Name, string Description);
public sealed record EscapePlayerSummary(Guid SeatId, string Name, bool IsHost, string? PhotoUrl);

/// <summary>One phone: the public view plus that player's own clue pieces.</summary>
public sealed record EscapePlayerView(
    int Version,
    EscapeStageView Stage,
    Guid SeatId,
    string Name,
    bool IsHost,
    IReadOnlyList<EscapePieceView> Pieces);

public sealed record EscapePieceView(string PuzzleId, string PuzzleTitle, string Text);
