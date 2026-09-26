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

    /// <summary>Which AI features this party uses. Set by the server when the party is created.</summary>
    public AiFeatures Ai { get; set; } = new();

    /// <summary>Host choices for this party that change what is shown.</summary>
    public PartyOptions Options { get; set; } = new();

    /// <summary>
    /// The character whose turn it is to speak: a guest's character, or one the narrator plays.
    /// It gives new groups a speaking order: introductions during the cast reveal, then lines,
    /// theories and confrontations while mingling. Cleared whenever the game moves on.
    /// </summary>
    public string? SpotlightCharacterId { get; set; }

    /// <summary>When the current speaker's turn is up. A guide for the room, not enforced.</summary>
    public DateTimeOffset? SpotlightEndsAt { get; set; }

    /// <summary>How many turns there have been, so the question cards vary.</summary>
    public int SpotlightTurns { get; set; }

    /// <summary>Characters who have had a turn in this scene, so "Spin" picks someone who hasn't.</summary>
    public List<string> SpotlightSpoken { get; set; } = [];

    /// <summary>Set when the spotlight is a challenge: a guest confronting another with a clue.</summary>
    public Confrontation? Confrontation { get; set; }

    /// <summary>The act in which each guest last confronted someone. One confrontation per guest per act.</summary>
    public Dictionary<Guid, int> ConfrontedInAct { get; set; } = [];

    /// <summary>Each guest's current "who looks guiltiest?" pick. Shown to the room only as totals.</summary>
    public Dictionary<Guid, string> Suspicions { get; set; } = [];

    /// <summary>
    /// The host pressed "Begin the evening" and the AI is writing a version of the mystery
    /// in which one of tonight's guests is the killer. The cast is frozen until it's done.
    /// </summary>
    public bool Tailoring { get; set; }

    /// <summary>Questions guests put to NPCs, and the NPCs' answers. Public: everyone hears them.</summary>
    public List<Interrogation> Interrogations { get; set; } = [];

    /// <summary>Inspector hints. Private to the seat that asked.</summary>
    public List<HintEntry> Hints { get; set; } = [];

    /// <summary>seatId → the Inspector's comment on that guest's accusation, shown once the killer is unmasked.</summary>
    public Dictionary<Guid, string> Verdicts { get; set; } = [];

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

    /// <summary>A costume selfie the guest uploaded, used as their avatar.</summary>
    public string? PhotoUrl { get; set; }
}

public sealed class Confrontation
{
    public Guid AccuserSeatId { get; set; }
    public required string ClueId { get; set; }
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

/// <summary>
/// AI switches for one party. They live in the game state (not in server config)
/// so the rules engine can enforce limits and the views know what to show,
/// without the pure engine ever calling an AI itself.
/// </summary>
public sealed class AiFeatures
{
    public bool NpcQuestions { get; set; }
    public int QuestionsPerAct { get; set; } = 3;
    public bool Hints { get; set; }
    public int HintsPerAct { get; set; } = 1;
    public bool Verdicts { get; set; }

    /// <summary>NPC answers are also turned into speech with the Voice role (otherwise the browser voice reads them).</summary>
    public bool Voices { get; set; }
}

public sealed class PartyOptions
{
    /// <summary>Show toast cues and themed cocktails. Off by default, never on for Family parties.</summary>
    public bool DrinkingPrompts { get; set; }

    /// <summary>How the AI game master plays it: its characters' answers, hints and verdicts. The written script doesn't change.</summary>
    public Tone Tone { get; set; }
}

/// <summary>
/// A flavour on top of the mystery's content rating, chosen by the host. The rating
/// (Family or Mature) belongs to the mystery; the tone only adjusts how the AI speaks.
/// </summary>
public enum Tone
{
    /// <summary>As the mystery is rated: full Mature for Adults, ordinary Family for Family.</summary>
    Standard,

    /// <summary>For an Adults mystery in mixed company (work friends, the in-laws): scandal yes, crude or graphic no.</summary>
    Clean,

    /// <summary>Silly and funny: hammed-up characters, puns and jokes. Made for Family parties with kids.</summary>
    Playful,
}

public sealed class Interrogation
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public int Act { get; set; }
    public Guid SeatId { get; set; }
    public required string AskerName { get; set; }
    public required string CharacterId { get; set; }
    public required string Question { get; set; }

    /// <summary>Null while the NPC is still "thinking".</summary>
    public string? Answer { get; set; }

    /// <summary>The answer spoken in the NPC's voice, when a Voice provider is set up.</summary>
    public string? AudioUrl { get; set; }
}

public sealed class HintEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public int Act { get; set; }
    public Guid SeatId { get; set; }

    /// <summary>Null while the Inspector is still thinking.</summary>
    public string? Text { get; set; }
}
