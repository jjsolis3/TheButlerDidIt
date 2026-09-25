namespace ButlerDidIt.Game.Scenarios;

// A Scenario is one complete mystery: who died, who did it, every character's
// secrets, every clue, and the order the evening plays out in.
//
// The same shape is used for hand-written mysteries (JSON files in /content)
// and, in milestone 2, for AI-generated ones. Keeping a single format means the
// engine, the validator and the UI never need to know where a mystery came from.
//
// These are plain classes with init-only properties rather than positional
// records so the JSON can omit optional fields and still deserialize cleanly.

public enum ContentRating
{
    Family,
    Mature,
}

public sealed class Scenario
{
    public required string Id { get; init; }
    public required string ThemeSlug { get; init; }
    public required string Title { get; init; }
    public string Synopsis { get; init; } = "";
    public ContentRating ContentRating { get; init; } = ContentRating.Mature;
    public int MinPlayers { get; init; } = 2;
    public int MaxPlayers { get; init; } = 8;
    public int EstimatedMinutes { get; init; } = 120;

    /// <summary>If true, the murderer's dossier tells them they did it and they must bluff.</summary>
    public bool MurdererKnows { get; init; } = true;

    public required Setting Setting { get; init; }
    public required Victim Victim { get; init; }
    public List<Character> Characters { get; init; } = [];
    public List<Clue> Clues { get; init; } = [];

    /// <summary>Cinematic that plays after the cast has been introduced (the body is found).</summary>
    public List<Cue> Prologue { get; init; } = [];

    public List<Act> Acts { get; init; } = [];
    public required AccusationOptions Accusation { get; init; }
    public required Solution Solution { get; init; }

    /// <summary>Cinematic for the very end, after the solution is explained.</summary>
    public List<Cue> Finale { get; init; } = [];

    public Character? FindCharacter(string id) => Characters.FirstOrDefault(c => c.Id == id);
    public Clue? FindClue(string id) => Clues.FirstOrDefault(c => c.Id == id);
}

public sealed class Setting
{
    public string Place { get; init; } = "";
    public string Era { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Image { get; init; }
}

public sealed class Victim
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string? Portrait { get; init; }
}

public sealed class Character
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Pronouns { get; init; } = "they/them";
    public string Title { get; init; } = "";
    public string PublicBio { get; init; } = "";
    public string CostumeTips { get; init; } = "";
    public string? Portrait { get; init; }
    public VoiceProfile Voice { get; init; } = new();

    /// <summary>
    /// Required characters are essential to the plot (the murderer, key witnesses).
    /// If no guest takes one, it becomes an NPC voiced by the narrator.
    /// Optional characters are simply left out of the evening.
    /// </summary>
    public bool Required { get; init; }

    public CharacterPrivate Private { get; init; } = new();
}

/// <summary>Hints for text-to-speech. Browser voices now; cloud voices in milestone 3.</summary>
public sealed class VoiceProfile
{
    public string Accent { get; init; } = "en-GB";
    public double Pitch { get; init; } = 1.0;
    public double Rate { get; init; } = 1.0;
    public string Style { get; init; } = "";
}

/// <summary>Everything only the person playing this character may see.</summary>
public sealed class CharacterPrivate
{
    public string Backstory { get; init; } = "";
    public List<Secret> Secrets { get; init; } = [];
    public List<string> Objectives { get; init; } = [];
    public List<string> Knows { get; init; } = [];
    public string Alibi { get; init; } = "";

    /// <summary>Lines to say aloud, keyed by act id. NPC lines are read by the narrator instead.</summary>
    public Dictionary<string, List<string>> Lines { get; init; } = [];
}

public sealed class Secret
{
    public required string Id { get; init; }
    public required string Text { get; init; }

    /// <summary>0 = known from the start; N = unlocks when act N begins.</summary>
    public int UnlockAct { get; init; }
}

public enum ClueVisibility
{
    /// <summary>Shown on the stage screen for everyone.</summary>
    Public,

    /// <summary>Sent only to the recipient character's phone. They choose whether to share it.</summary>
    Private,
}

public enum ClueWave
{
    /// <summary>Dropped when the act's mingle timer starts.</summary>
    Start,

    /// <summary>Dropped halfway through the act's mingle timer (or earlier if the host chooses).</summary>
    Midway,
}

public sealed class Clue
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public string? Image { get; init; }
    public ClueVisibility Visibility { get; init; } = ClueVisibility.Public;

    /// <summary>1-based act number this clue belongs to.</summary>
    public int Act { get; init; } = 1;

    public ClueWave Wave { get; init; } = ClueWave.Start;

    /// <summary>For private clues: the character id whose phone receives it.</summary>
    public string? Recipient { get; init; }

    /// <summary>Character ids this clue casts suspicion on. Used by the validator to prove solvability.</summary>
    public List<string> PointsTo { get; init; } = [];

    public bool RedHerring { get; init; }

    /// <summary>Optional riddle or cipher. The clue's full meaning unlocks when someone solves it.</summary>
    public Puzzle? Puzzle { get; init; }
}

public sealed class Puzzle
{
    public required string Prompt { get; init; }

    /// <summary>Accepted answers, compared case-insensitively with punctuation stripped.</summary>
    public List<string> Answers { get; init; } = [];

    public string Hint { get; init; } = "";
    public required string SolvedText { get; init; }
}

public sealed class Act
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public List<Cue> Cues { get; init; } = [];
    public int MingleMinutes { get; init; } = 20;

    /// <summary>Conversation starters shown on the stage while guests mingle.</summary>
    public List<string> Prompts { get; init; } = [];
}

public enum CueType
{
    /// <summary>Narrator text, spoken aloud (audio file if present, otherwise browser speech).</summary>
    Narration,

    /// <summary>A full-screen image, optionally with a slow pan/zoom "motion comic" effect.</summary>
    Image,

    /// <summary>Background music (loops until replaced or stopped).</summary>
    Music,

    /// <summary>A one-shot sound effect.</summary>
    Sfx,

    /// <summary>A video clip.</summary>
    Video,

    /// <summary>A line spoken by a character. Only played on stage when that character is an NPC.</summary>
    Line,
}

public sealed class Cue
{
    public CueType Type { get; init; }
    public string? Text { get; init; }

    /// <summary>Path to an image/audio/video file. Optional: the stage falls back gracefully when missing.</summary>
    public string? Src { get; init; }

    /// <summary>Character id for <see cref="CueType.Line"/> cues.</summary>
    public string? Speaker { get; init; }

    /// <summary>Visual effect for images, e.g. "kenburns".</summary>
    public string? Effect { get; init; }
}

public sealed class AccusationOptions
{
    public List<Option> Motives { get; init; } = [];
    public List<Option> Methods { get; init; } = [];
}

public sealed class Option
{
    public required string Id { get; init; }
    public required string Text { get; init; }
}

public sealed class Solution
{
    public required string MurdererId { get; init; }
    public required string MotiveId { get; init; }
    public required string MethodId { get; init; }

    /// <summary>Paragraphs revealed one at a time on the stage during the reveal.</summary>
    public List<string> Explanation { get; init; } = [];

    public List<TimelineEntry> Timeline { get; init; } = [];
}

public sealed class TimelineEntry
{
    public required string Time { get; init; }
    public required string Event { get; init; }
}
