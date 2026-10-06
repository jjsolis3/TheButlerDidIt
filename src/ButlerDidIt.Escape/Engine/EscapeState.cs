using ButlerDidIt.Escape.Rooms;

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

    /// <summary>How hard the game is, chosen when the party was made. Null (and in parties from before difficulties) is Normal.</summary>
    public EscapeDifficulty? Difficulty { get; set; }
    public EscapeDifficulty Level => Difficulty ?? EscapeDifficulty.Normal;

    public List<EscapePlayer> Players { get; set; } = [];

    /// <summary>Who may answer a puzzle (#132), chosen when the party was made. Parties from before have Anyone.</summary>
    public AnswerRule Answering { get; set; }

    /// <summary>Who is working on each open puzzle, while <see cref="TakesTurns"/>. A puzzle not in here is free to take.</summary>
    public Dictionary<string, PuzzleHold> Holds { get; set; } = [];

    /// <summary>Where the next deal starts round the table (Dealt), so the stages share out evenly.</summary>
    public int DealOffset { get; set; }

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

    /// <summary>Which phone holds each clue piece, dealt when the clock starts. Pieces for missing players wait in hiding spots.</summary>
    public List<PieceHolder> Pieces { get; set; } = [];

    /// <summary>Scene objects someone has searched.</summary>
    public List<string> Examined { get; set; } = [];

    /// <summary>Items someone has looked at closely.</summary>
    public List<string> Inspected { get; set; } = [];

    /// <summary>What the group has found out, oldest first, shared by everyone.</summary>
    public List<NotebookEntry> Notebook { get; set; } = [];

    /// <summary>Ciphers whose key the group has found (it stays found, even once the item it was written on is used up).</summary>
    public List<string> KeysFound { get; set; } = [];

    /// <summary>The lights that are on in each Switches puzzle that has been touched (the others are as the room starts them).</summary>
    public Dictionary<string, List<int>> Switches { get; set; } = [];

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

    /// <summary>Puzzles go to people (taken or dealt). Not with one player: there's nobody to share with.</summary>
    public bool TakesTurns() => Answering != AnswerRule.Anyone && Players.Count > 1;

    public EscapePlayer? FindPlayer(Guid seatId) => Players.FirstOrDefault(p => p.SeatId == seatId);
    public bool IsSolved(string puzzleId) => Solved.Any(s => s.PuzzleId == puzzleId);
    public int HintsUsed => HintsShown.Values.Sum();
}

/// <summary>
/// Who may answer a puzzle (#132). With anyone answering anything, one keen player can do the whole room; giving
/// puzzles to people shares the room out. Whoever holds a puzzle is also the only one who can find its own things
/// by searching: its hidden clue pieces, and the spots where its cipher key is written.
/// </summary>
public enum AnswerRule
{
    /// <summary>Anyone answers anything, as escape rooms first played.</summary>
    Anyone,

    /// <summary>A player takes a puzzle and only they answer it, one puzzle at a time.</summary>
    TakeIt,

    /// <summary>Each stage's puzzles are dealt round the table as it opens. A player can pass theirs on.</summary>
    Dealt,
}

/// <summary>
/// A puzzle someone is working on. It goes back to the table after <see cref="EscapeEngine.MissesBeforeFree"/> wrong
/// answers in a row, and someone else may take it over once its holder hasn't tried for <see cref="EscapeEngine.TakeOverAfter"/>.
/// </summary>
public sealed class PuzzleHold
{
    public Guid SeatId { get; set; }

    /// <summary>When it came to them, or they last tried it: what "hasn't tried for a while" is measured from.</summary>
    public DateTimeOffset Active { get; set; }

    /// <summary>Wrong answers in a row on it.</summary>
    public int Misses { get; set; }
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
    /// <summary>Someone found something by searching or looking closely.</summary>
    Found,

    /// <summary>Someone searched and found nothing they could use, and it cost time (Normal and Hard, #132).</summary>
    Decoy,
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

    /// <summary>For <see cref="CueKind.Found"/>: what was found or searched (public: the ticker says it too).</summary>
    public string? Thing { get; set; }
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
/// <summary>
/// Who holds a clue piece. A piece meant for a player who didn't join waits in a hiding spot
/// (<see cref="SpotId"/>, with no seat) until someone searches there.
/// </summary>
public sealed record PieceHolder(string PuzzleId, int Index, Guid? SeatId, string? SpotId = null)
{
    public bool IsHidden => SeatId is null;
}

/// <summary>A line in the group's notebook: where it came from ("The rug") and what it says.</summary>
public sealed record NotebookEntry(DateTimeOffset At, string Source, string Text);
public sealed record EscapeFeedEntry(DateTimeOffset At, string Text);
