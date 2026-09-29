using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// A hand-written escape room, loaded from content/escape/&lt;id&gt;.json.
///
/// A room is a series of stages. A stage is open until every puzzle in it is solved; then the
/// next stage opens, and solving the last stage means the group escaped. Puzzles can need items
/// (keys, tools) that other puzzles give out, and can split their clues across the players'
/// phones so nobody can solve them alone.
/// </summary>
public sealed class EscapeRoom
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Synopsis { get; init; }
    public ContentRating ContentRating { get; init; } = ContentRating.Family;

    /// <summary>Used for the page colours and, later, generated art.</summary>
    public string Theme { get; init; } = "escape";
    public string ArtStyle { get; init; } = "";

    public int MinPlayers { get; init; } = 2;
    public int MaxPlayers { get; init; } = 8;
    public int TimeLimitMinutes { get; init; } = 45;

    /// <summary>Asking for a hint takes this many seconds off the clock.</summary>
    public int HintPenaltySeconds { get; init; } = 120;

    /// <summary>Read on the TV when the clock starts.</summary>
    public required string Intro { get; init; }

    /// <summary>Read when the group escapes, and when time runs out.</summary>
    public required string EscapedText { get; init; }
    public required string FailedText { get; init; }

    public List<EscapeStage> Stages { get; init; } = [];
    public List<EscapePuzzle> Puzzles { get; init; } = [];
    public List<EscapeItem> Items { get; init; } = [];

    public EscapePuzzle? FindPuzzle(string id) => Puzzles.FirstOrDefault(p => p.Id == id);
    public EscapeItem? FindItem(string id) => Items.FirstOrDefault(i => i.Id == id);
}

public sealed class EscapeStage
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Read on the TV when the stage opens.</summary>
    public required string Description { get; init; }

    /// <summary>Puzzle ids, in the order the TV lists them.</summary>
    public List<string> Puzzles { get; init; } = [];
}

public enum PuzzleKind
{
    /// <summary>A number keypad. Answers are digits.</summary>
    Code,

    /// <summary>A word or a phrase.</summary>
    Text,

    /// <summary>No answer: use the items it needs (a key in a lock, a fuse in the box).</summary>
    Use,
}

public sealed class EscapePuzzle
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public PuzzleKind Kind { get; init; }

    /// <summary>What everyone sees on the TV.</summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Accepted answers. Compared after lower-casing, dropping a leading "a", "an" or "the",
    /// and removing spaces and punctuation, so "The Clock!" matches "clock". Never sent to browsers.
    /// </summary>
    public List<string> Answers { get; init; } = [];

    /// <summary>Items the group must hold before this puzzle can be attempted (for Use puzzles: the items used).</summary>
    public List<string> Requires { get; init; } = [];

    /// <summary>Items the group receives when it's solved.</summary>
    public List<string> Rewards { get; init; } = [];

    /// <summary>
    /// Clue pieces dealt out to the players' phones when the game starts, one or more per phone.
    /// Each piece is visible only on the phone that holds it: the group has to talk.
    /// </summary>
    public List<string> Pieces { get; init; } = [];

    /// <summary>Revealed one at a time, each costing the room's hint penalty.</summary>
    public List<string> Hints { get; init; } = [];

    /// <summary>Read out when it's solved.</summary>
    public required string SolvedText { get; init; }
}

public sealed class EscapeItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
}
