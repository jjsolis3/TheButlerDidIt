using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// A hand-written escape room, loaded from content/escape/&lt;id&gt;.json.
///
/// A room is a series of stages. A stage is open until every puzzle in it is solved; then the
/// next stage opens, and solving the last stage means the group escaped. Puzzles can need items
/// (keys, tools) that other puzzles give out, and can split their clues across the players'
/// phones so nobody can solve them alone.
///
/// A record, so a built puzzle set or a shorter length is a copy made with <c>room with { … }</c>:
/// every setting carries over, including ones added later.
/// </summary>
public sealed record EscapeRoom
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

    /// <summary>The background sound on the TV, made in the browser (no audio files). A stage can change it.</summary>
    public Soundscape Soundscape { get; init; } = Soundscape.Drone;

    /// <summary>
    /// The game lengths a host can pick, in minutes (30, 45 or 60). A shorter game plays fewer
    /// puzzles (see <see cref="EscapePuzzle.MinMinutes"/>). Left out, the room has one length: its time limit.
    /// </summary>
    public List<int> Lengths { get; init; } = [];

    /// <summary>Seasonal shelves the room is on, like a mystery theme's: "halloween".</summary>
    public List<string> Seasons { get; init; } = [];

    /// <summary>The lengths on offer, shortest first.</summary>
    public IReadOnlyList<int> PlayableLengths => Lengths.Count == 0 ? [TimeLimitMinutes] : Lengths.Distinct().Order().ToList();

    /// <summary>Who speaks for the room when the AI game master is on. Rooms without one get <see cref="GameMaster.Default"/>.</summary>
    public GameMaster? GameMaster { get; init; }

    public List<EscapeStage> Stages { get; init; } = [];
    public List<EscapePuzzle> Puzzles { get; init; } = [];
    public List<EscapeItem> Items { get; init; } = [];

    public GameMaster Host => GameMaster ?? Rooms.GameMaster.Default;

    public EscapePuzzle? FindPuzzle(string id) => Puzzles.FirstOrDefault(p => p.Id == id);
    public EscapeItem? FindItem(string id) => Items.FirstOrDefault(i => i.Id == id);
}

/// <summary>
/// The character the AI plays while the group is in the room: reacting out loud on the TV and giving
/// hints. Only its voice is written here; its lines are written by the AI during the game.
/// </summary>
public sealed class GameMaster
{
    public static readonly GameMaster Default = new() { Name = "The Game Master", Persona = "A calm, slightly amused host who watches the group through a hidden camera." };

    public required string Name { get; init; }

    /// <summary>How it talks, for the AI: a sentence or two.</summary>
    public string Persona { get; init; } = "";

    /// <summary>How it sounds: picks the AI voice (see VoiceCasting) and tunes the browser's voice when there is none.</summary>
    public VoiceProfile Voice { get; init; } = new();
}

public sealed record EscapeStage
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>Read on the TV when the stage opens.</summary>
    public required string Description { get; init; }

    /// <summary>Puzzle ids, in the order the TV lists them.</summary>
    public List<string> Puzzles { get; init; } = [];

    /// <summary>A different background sound for this stage; the room's when left out.</summary>
    public Soundscape? Soundscape { get; init; }
}

/// <summary>
/// Background sound presets, synthesised live on the TV (src/web/src/escape/sound.ts), so rooms
/// need no audio files and nothing to license. New presets need a matching one there.
/// </summary>
public enum Soundscape
{
    /// <summary>No background sound.</summary>
    Silence,

    /// <summary>A low, uneasy hum: fits anywhere.</summary>
    Drone,

    /// <summary>Machinery hum, a ticking clock and the odd drip.</summary>
    Workshop,

    /// <summary>A slightly out-of-tune music box over a crowd murmur.</summary>
    Carnival,

    /// <summary>Waves and wind.</summary>
    Sea,

    /// <summary>A slow, pulsing synth pad and faint beeps.</summary>
    Space,

    /// <summary>Wind and distant, low bells.</summary>
    Haunted,
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

public sealed record EscapePuzzle
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

    /// <summary>
    /// Hand-written alternatives (a different riddle, different numbers). Each play picks one from
    /// the seed; fields a variant leaves out keep the values above.
    /// </summary>
    public List<PuzzleVariant> Variants { get; init; } = [];

    /// <summary>
    /// Builds the pieces and the answer from the seed instead (see <see cref="RoomVariants"/>).
    /// Prompt, hints and solved text may use {order}, {facts} and {answer}.
    /// </summary>
    public PuzzleGenerator? Generator { get; init; }

    /// <summary>Only played in games at least this long (e.g. 45: left out of a 30-minute game). Null: always played.</summary>
    public int? MinMinutes { get; init; }
}

public sealed class PuzzleVariant
{
    public string? Prompt { get; init; }
    public List<string>? Answers { get; init; }
    public List<string>? Pieces { get; init; }
    public List<string>? Hints { get; init; }
    public string? SolvedText { get; init; }
}

public enum GeneratorType
{
    /// <summary>A code whose digits are everyday facts ("the number of days in a week"), one fact per phone.</summary>
    DigitFacts,

    /// <summary>A code read from coloured objects in a given colour order; each phone sees one colour and its number.</summary>
    ColorDigits,

    /// <summary>A password of words in order; each phone remembers one word and its position.</summary>
    WordSequence,
}

public sealed class PuzzleGenerator
{
    public GeneratorType Type { get; init; }

    /// <summary>How many digits or words: one piece each.</summary>
    public int Count { get; init; } = 3;

    /// <summary>The text of each piece. Placeholders: {ordinal} (FIRST, SECOND…), {fact}, {color}, {digit}, {word}.</summary>
    public required string PieceTemplate { get; init; }

    /// <summary>For WordSequence: the words to choose from.</summary>
    public List<string> Words { get; init; } = [];

    /// <summary>For ColorDigits: the colours to choose from.</summary>
    public List<string> Colors { get; init; } = [];
}

public sealed class EscapeItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
}
