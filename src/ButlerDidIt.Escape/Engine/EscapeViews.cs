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
    string? ArtUrl,
    EscapeDifficulty Difficulty,
    /// <summary>The spots to search in the stage in front of the group, or null when it has none.</summary>
    EscapeSceneView? Scene,
    /// <summary>What the group has found out, oldest first.</summary>
    IReadOnlyList<EscapeNoteView> Notebook);

public sealed record EscapeSceneView(int Width, int Height, string Backdrop, IReadOnlyList<EscapeSpotView> Objects);

/// <summary>A spot in the scene. What's there (<see cref="Look"/>) only once someone has searched it.</summary>
public sealed record EscapeSpotView(string Id, string Prop, int X, int Y, int W, int H, string Label, bool Examined, string? Look);

public sealed record EscapeNoteView(string Source, string Text, DateTimeOffset At);

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
    int PieceCount,
    /// <summary>Pieces of this puzzle still hidden somewhere in the room.</summary>
    int PiecesHidden,
    /// <summary>For Search puzzles: how many of the spots it needs have been searched, of how many.</summary>
    EscapeFindsView? Finds,
    /// <summary>For Switches puzzles: the grid as it is now.</summary>
    EscapeSwitchesView? Switches,
    /// <summary>For ciphers: which decoding tool the phones offer, once the key has been found.</summary>
    EscapeCipherView? Cipher,
    /// <summary>For deductions: the things to line up, for the phones' logic grid.</summary>
    EscapeDeductionView? Deduction);

/// <param name="Unlocked">The group has found a key for it (numbers and mirror need none).</param>
/// <param name="Keys">
/// Every key found so far, labelled with where it was found. A cipher can have a real key and decoys written in
/// different places; these never say which is which (working that out is the puzzle), and keys not yet found are never sent.
/// </param>
public sealed record EscapeCipherView(CipherType Type, bool Unlocked, IReadOnlyList<EscapeFoundKey> Keys);

/// <param name="From">Where it was found: the spot's label ("barred window"), an item's name, or a puzzle's title.</param>
/// <param name="Shift">For a shift cipher: the amount written there (the group has read it already).</param>
/// <param name="Table">For symbols and Morse: the key card written there.</param>
public sealed record EscapeFoundKey(string From, int? Shift, IReadOnlyList<EscapeKeyEntry>? Table);
public sealed record EscapeKeyEntry(string Code, string Letter);
public sealed record EscapeDeductionView(IReadOnlyList<string> Items, int Spots);

public sealed record EscapeFindsView(int Found, int Total);
public sealed record EscapeSwitchesView(int Size, IReadOnlyList<int> Lit);

/// <summary>An item the group holds. <see cref="InspectText"/> only once someone has looked at it closely.</summary>
public sealed record EscapeItemView(string Id, string Name, string Description, bool Inspectable, string? InspectText);
public sealed record EscapePlayerSummary(Guid SeatId, string Name, bool IsHost, string? PhotoUrl);

/// <summary>One phone: the public view plus that player's own clue pieces.</summary>
public sealed record EscapePlayerView(
    int Version,
    EscapeStageView Stage,
    Guid SeatId,
    string Name,
    bool IsHost,
    IReadOnlyList<EscapePieceView> Pieces);

/// <param name="FoundIn">Where this phone's player found it, for a piece that was hidden in the room.</param>
public sealed record EscapePieceView(string PuzzleId, string PuzzleTitle, string Text, string? FoundIn);

// ---------------------------------------------------------------------- the recap

/// <summary>
/// The page after the game (#111): how it went, for the group to look back on and share. A shared recap
/// can reach people who haven't played the room yet, so it never spoils it: no answers, prompts, hints,
/// solved texts (a generated puzzle writes its answer into those) or clue pieces, and only the stages the
/// group reached. Built by <see cref="EscapeProjector.Recap"/> once the game is over.
/// </summary>
public sealed record EscapeRecapView(
    string RoomId,
    string RoomTitle,
    string Synopsis,
    string Theme,
    ButlerDidIt.Game.Scenarios.ContentRating ContentRating,
    /// <summary>The room's cover picture, if one has been painted.</summary>
    string? CoverUrl,
    bool Escaped,
    string EndText,
    DateTimeOffset StartedAt,
    int ElapsedSeconds,
    /// <summary>What was left on the clock at the end: 0 when time ran out.</summary>
    int SecondsLeft,
    int TimeLimitMinutes,
    EscapeDifficulty Difficulty,
    int HintsUsed,
    int HintPenaltySeconds,
    int WrongAttempts,
    /// <summary>What the leaderboard sorts by: the time plus what the hints cost.</summary>
    int Score,
    int SolvedCount,
    int PuzzleCount,
    int StageCount,
    bool Daily,
    /// <summary>The puzzle set played, so friends can try the same puzzles (the ending already shows it).</summary>
    long PuzzleSet,
    IReadOnlyList<EscapeRecapPlayer> Team,
    /// <summary>The stages the group reached, in order: never one they didn't get to.</summary>
    IReadOnlyList<EscapeRecapStage> Stages,
    IReadOnlyList<EscapeRecapHighlight> Highlights,
    /// <summary>The game master's name, when it spoke during the game.</summary>
    string? GameMasterName,
    /// <summary>The game master's latest lines, oldest first (the state keeps the last few moments of a game).</summary>
    IReadOnlyList<string> GameMasterLines);

/// <param name="Solved">How many puzzles this player opened.</param>
public sealed record EscapeRecapPlayer(string Name, string? PhotoUrl, int Solved);

/// <param name="OpenedAt">Seconds from the start when the group got into it.</param>
/// <param name="ClearedAt">Seconds from the start when its last puzzle opened; null when time ran out first.</param>
/// <param name="Hints">Hints taken on its puzzles.</param>
public sealed record EscapeRecapStage(int Number, string Title, int OpenedAt, int? ClearedAt, int Hints, IReadOnlyList<EscapeRecapPuzzle> Puzzles);

/// <param name="SolvedBy">Who opened it; null when it was still locked at the end.</param>
/// <param name="SolvedAt">Seconds from the start.</param>
public sealed record EscapeRecapPuzzle(string Title, PuzzleKind Kind, string? SolvedBy, int? SolvedAt, int Hints);

/// <summary>Something worth a cheer: "🧠 Most puzzles opened: Ana (4)".</summary>
public sealed record EscapeRecapHighlight(string Icon, string Title, string Detail);
