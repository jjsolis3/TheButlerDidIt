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

    /// <summary>Pairs of items that make a new one when combined (the two halves of a torn map). Never sent to browsers.</summary>
    public List<EscapeRecipe> Recipes { get; init; } = [];

    public GameMaster Host => GameMaster ?? Rooms.GameMaster.Default;

    public EscapePuzzle? FindPuzzle(string id) => Puzzles.FirstOrDefault(p => p.Id == id);
    public EscapeItem? FindItem(string id) => Items.FirstOrDefault(i => i.Id == id);
    public EscapeStage? StageOf(string puzzleId) => Stages.FirstOrDefault(s => s.Puzzles.Contains(puzzleId));
    public IEnumerable<SceneObject> SceneObjects => Stages.SelectMany(s => s.Scene?.Objects ?? []);
    public SceneObject? FindObject(string id) => SceneObjects.FirstOrDefault(o => o.Id == id);
}

/// <summary>
/// How hard a game is. Chosen by the host with the length, and each has its own leaderboard.
/// Normal plays the room exactly as written; Easy and Hard adjust it (see <see cref="RoomVariants"/>).
/// </summary>
public enum EscapeDifficulty
{
    Easy,
    Normal,
    Hard,
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

    /// <summary>The picture of this part of the room, with the spots players can search. Left out, there's nothing to search.</summary>
    public EscapeScene? Scene { get; init; }
}

/// <summary>
/// The part of the room in front of the group, drawn on the TV and the phones. Positions are on a
/// fixed <see cref="Width"/> × <see cref="Height"/> canvas, so the screens can scale it to fit.
/// </summary>
public sealed record EscapeScene
{
    public int Width { get; init; } = 1000;
    public int Height { get; init; } = 600;

    /// <summary>A background the screens know how to draw ("workshop", "carnival"…).</summary>
    public string Backdrop { get; init; } = "";
    public List<SceneObject> Objects { get; init; } = [];
}

/// <summary>
/// Something in the scene a player can search: a rug to lift, a painting to look behind.
/// What it holds stays on the server until someone searches it. One with only a <see cref="Look"/>
/// is a decoy: harmless, except on Hard, where searching it costs time.
/// </summary>
public sealed record SceneObject
{
    /// <summary>Unique in the whole room.</summary>
    public required string Id { get; init; }

    /// <summary>What the screens draw: one of <see cref="SceneProps.Known"/>.</summary>
    public required string Prop { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; } = 100;
    public int H { get; init; } = 100;

    /// <summary>Everyone sees this ("the rug").</summary>
    public required string Label { get; init; }

    /// <summary>What the searcher finds. Private until the spot is searched.</summary>
    public required string Look { get; init; }

    /// <summary>An item found here.</summary>
    public string? Gives { get; init; }

    /// <summary>A clue written into the group's notebook when it's found.</summary>
    public string? Clue { get; init; }

    /// <summary>An item needed to find anything here (the UV lamp). It isn't used up.</summary>
    public string? Requires { get; init; }

    /// <summary>What a player without <see cref="Requires"/> is told instead.</summary>
    public string? LockedText { get; init; }

    /// <summary>A hiding place for clue pieces nobody's phone holds, when fewer players join than a puzzle has pieces.</summary>
    public bool HidesPieces { get; init; }
}

/// <summary>The props the screens can draw. A new one needs a drawing in src/web too.</summary>
public static class SceneProps
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "rug", "painting", "crate", "pipe", "bookshelf", "clock", "chest", "barrel", "lamp", "window", "desk", "vent",
        "poster", "door", "safe", "plant", "mirror", "shelf", "box", "table", "cabinet", "statue", "drawer", "bed", "sign", "machine",
    };
}

/// <summary>Two items that make a third when combined. Both are used up.</summary>
public sealed record EscapeRecipe
{
    public List<string> Items { get; init; } = [];
    public required string Makes { get; init; }

    /// <summary>Read out when it works ("The two halves line up…").</summary>
    public string Text { get; init; } = "";
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

    /// <summary>No answer: solved by itself once every spot in <see cref="EscapePuzzle.Finds"/> has been searched.</summary>
    Search,

    /// <summary>A grid of lights: pressing one flips it and its neighbours. Solved when every light is on.</summary>
    Switches,
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

    /// <summary>Only played at this difficulty or harder (Hard: an extra puzzle for experts). Null: always played.</summary>
    public EscapeDifficulty? MinDifficulty { get; init; }

    /// <summary>For Search puzzles: the scene objects, in the same stage, that must all be searched.</summary>
    public List<string> Finds { get; init; } = [];

    /// <summary>For Switches puzzles: the lights at the start. Made by the generator; never written by hand.</summary>
    public SwitchGrid? Grid { get; init; }

    /// <summary>
    /// For ciphers: where the key can be read ("object:rug", "item:locket", "puzzle:safe"), so the
    /// validator can check it's found in time. Made by <see cref="RoomVariants"/>; never written by hand.
    /// </summary>
    public List<string> KeyAt { get; init; } = [];

    /// <summary>For ciphers: which code, and its key as written into the room. Made by <see cref="RoomVariants"/>; never sent as it is.</summary>
    public CipherDecoder? Decoder { get; init; }

    /// <summary>For deductions: the things in the row, in the order the code reads them (public: the prompt lists them too).</summary>
    public List<string> Lineup { get; init; } = [];
}

public sealed record CipherDecoder(CipherType Type, string Key);

/// <summary>A square grid of lights, numbered row by row from the top left. <see cref="Lit"/> lists the ones that are on.</summary>
public sealed record SwitchGrid(int Size, List<int> Lit);

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

    /// <summary>
    /// A word from <see cref="PuzzleGenerator.Words"/> in code (see <see cref="CipherType"/>). The prompt shows it
    /// with {cipher}; the key is written with {key:&lt;puzzle id&gt;} somewhere the group has to find it.
    /// </summary>
    Cipher,

    /// <summary>A number pattern: the prompt shows the first terms with {sequence}, and the code is the next one.</summary>
    Sequence,

    /// <summary>
    /// A logic puzzle: <see cref="PuzzleGenerator.Words"/> are things in a row, and the clue pieces say where
    /// each one is, just enough for one answer. The code is each thing's place, in the order {items} lists them.
    /// </summary>
    Deduction,

    /// <summary>The starting lights of a Switches puzzle, always solvable.</summary>
    Switches,
}

public enum CipherType
{
    /// <summary>Every letter moved along the alphabet by the same amount; the key is the amount.</summary>
    Shift,

    /// <summary>Every letter a symbol; the key is a table of them (with a couple of spare letters).</summary>
    Symbols,

    /// <summary>Dots and dashes; the key is a table of the letters needed (and a couple more).</summary>
    Morse,

    /// <summary>Letters as numbers, A=1 to Z=26.</summary>
    Numbers,

    /// <summary>The alphabet folded in half: A is Z, B is Y.</summary>
    Mirror,
}

public sealed class PuzzleGenerator
{
    public GeneratorType Type { get; init; }

    /// <summary>How many digits or words: one piece each.</summary>
    public int Count { get; init; } = 3;

    /// <summary>
    /// The text of each piece. Placeholders: {ordinal} (FIRST, SECOND…), {fact}, {color}, {digit}, {word};
    /// for Deduction, {clue}. Ciphers, sequences and switches have no pieces.
    /// </summary>
    public string PieceTemplate { get; init; } = "";

    /// <summary>For WordSequence: the words to choose from. For Cipher: the words that can be hidden. For Deduction: the things in the row.</summary>
    public List<string> Words { get; init; } = [];

    /// <summary>For Cipher: which code.</summary>
    public CipherType Cipher { get; init; }

    /// <summary>For ColorDigits: the colours to choose from.</summary>
    public List<string> Colors { get; init; } = [];
}

public sealed record EscapeItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";

    /// <summary>What a closer look shows (numbers scratched inside the locket). Private until someone looks.</summary>
    public string? Inspect { get; init; }

    /// <summary>An item needed for the closer look (the magnifying glass). It isn't used up.</summary>
    public string? InspectRequires { get; init; }

    /// <summary>An item found by looking closely (the photo inside the locket).</summary>
    public string? InspectGives { get; init; }
}
