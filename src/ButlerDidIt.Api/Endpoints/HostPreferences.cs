using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Api.Endpoints;

/// <summary>
/// A host's usual party settings (#102), so the host page starts from them instead of the same defaults every time.
/// Kept as one jsonb document on the host (<see cref="AppUser.Preferences"/>): it's always read and written whole and
/// never searched, so a new setting is a new property with a default, with no migration. An old document simply lacks
/// it and gets the default.
/// </summary>
public sealed record HostPreferences
{
    /// <summary>Which game the host page opens on.</summary>
    public GameKind Game { get; init; } = GameKind.Mystery;
    public MysteryDefaults Mystery { get; init; } = new();
    public EscapeDefaults Escape { get; init; } = new();

    /// <summary>The host's settings, or the defaults when they've saved none (or what's stored no longer reads).</summary>
    public static HostPreferences For(AppUser user)
    {
        if (string.IsNullOrWhiteSpace(user.Preferences)) return new();
        try
        {
            return GameJson.Deserialize<HostPreferences>(user.Preferences) ?? new();
        }
        catch (System.Text.Json.JsonException)
        {
            return new(); // a setting renamed since: start over rather than break the host page
        }
    }

    /// <summary>What's wrong with these settings, or null. The page only offers valid ones; this guards the API.</summary>
    public string? Problem()
    {
        if (!Enum.IsDefined(Game) || !Enum.IsDefined(Mystery.Mode) || !Enum.IsDefined(Escape.Mode)) return "Choose a game and a kind of party.";
        if (!Enum.IsDefined(Mystery.Shelf) || !Enum.IsDefined(Escape.Shelf)) return "Choose Adults or Family.";
        if (Escape.Mode == PartyMode.PassAndPlay) return "Escape rooms are played together or on a video call.";
        if (!Enum.IsDefined(Mystery.Tone)) return "Choose a tone.";
        // The tones each shelf offers (TONES in src/web/src/lib/partyOptions.ts): "Funny" is for Family parties, "Normal" for Adults in mixed company.
        if (Mystery.Tone == Tone.Playful && Mystery.Shelf != ContentRating.Family) return "Funny is a tone for Family mysteries.";
        if (Mystery.Tone == Tone.Clean && Mystery.Shelf != ContentRating.Mature) return "Mixed company is a tone for Adult mysteries.";
        if (!Enum.IsDefined(Escape.Difficulty)) return "Choose easy, normal or hard.";
        if (!Enum.IsDefined(Escape.Answering)) return "Choose who answers the puzzles.";
        if (Escape.Puzzles is not (PuzzleChoice.Fresh or PuzzleChoice.Daily)) return "Choose fresh puzzles or today's challenge.";
        if (Escape.Minutes is { } m && m is not (30 or 45 or 60)) return "Choose 30, 45 or 60 minutes, or each room's own length.";
        return null;
    }
}

/// <summary>How a new murder mystery party starts.</summary>
public sealed record MysteryDefaults
{
    public PartyMode Mode { get; init; } = PartyMode.SharedScreen;

    /// <summary>The Adults or Family shelf, which is also the party's content level.</summary>
    public ContentRating Shelf { get; init; } = ContentRating.Mature;
    public Tone Tone { get; init; } = Tone.Standard;
    public bool DrinkingPrompts { get; init; }
    public bool UseAi { get; init; } = true;

    /// <summary>Let the AI rewrite "Surprise me" so one of tonight's guests is the killer, when no version fits.</summary>
    public bool Tailor { get; init; } = true;
}

/// <summary>How a new escape room party starts.</summary>
public sealed record EscapeDefaults
{
    public PartyMode Mode { get; init; } = PartyMode.SharedScreen;
    public ContentRating Shelf { get; init; } = ContentRating.Mature;

    /// <summary>The game's length, when the chosen room offers it; null plays each room at its own length.</summary>
    public int? Minutes { get; init; }
    public EscapeDifficulty Difficulty { get; init; } = EscapeDifficulty.Normal;

    /// <summary>Fresh puzzles or today's challenge. (A replay is of one shared set, so it's never a default.)</summary>
    public PuzzleChoice Puzzles { get; init; } = PuzzleChoice.Fresh;
    public bool UseAi { get; init; } = true;

    /// <summary>Who answers the puzzles (#132). Taking turns is the default: it shares the room out at a family table.</summary>
    public ButlerDidIt.Escape.Engine.AnswerRule Answering { get; init; } = ButlerDidIt.Escape.Engine.AnswerRule.TakeIt;
}
