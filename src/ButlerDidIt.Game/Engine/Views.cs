using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Game.Engine;

// Views are what the server sends to browsers. They are deliberately separate
// types from Scenario/GameState: a view can only contain what we explicitly copy
// into it, so there is no way to leak the solution by accident when someone adds
// a new field to the scenario later.

/// <summary>The public picture of the party, shown on the TV / shared screen and embedded in every player view.</summary>
public sealed record StageView(
    int Version,
    Phase Phase,
    ScenarioSummary Scenario,
    int ActNumber,
    int ActCount,
    string? ActTitle,
    ActStep ActStep,
    TimerView? Timer,
    IReadOnlyList<CueView> Cues,
    IReadOnlyList<string> Prompts,
    IReadOnlyList<CastMember> Cast,
    IReadOnlyList<PlayerSummary> Players,
    IReadOnlyList<ClueView> Clues,
    IReadOnlyList<SecretView> RevealedSecrets,
    int PendingClues,
    IReadOnlyList<FeedItem> Feed,
    AccusationProgress? Accusation,
    RevealView? Reveal,
    AwardsView? Awards,
    AiFeatures Ai,
    IReadOnlyList<InterrogationView> Interrogations,
    PartyOptions Options);

/// <summary>A question to an NPC and its answer. Public: the whole room hears the interrogation.</summary>
public sealed record InterrogationView(
    Guid Id,
    int Act,
    string AskerName,
    string CharacterId,
    string CharacterName,
    string Question,
    string? Answer,
    VoiceProfile? Voice,
    string? AudioUrl);

public sealed record HintView(Guid Id, int Act, string? Text);

public sealed record ScenarioSummary(
    string Id,
    string ThemeSlug,
    string Title,
    string Synopsis,
    string Place,
    string Era,
    string SettingDescription,
    string? SettingImage,
    string VictimName,
    string VictimDescription,
    string? VictimPortrait,
    int MinPlayers,
    int MaxPlayers);

/// <summary>
/// ServerNow lets each browser work out how far its own clock is off from the
/// server's, so every phone shows the same countdown even if one clock is wrong.
/// </summary>
public sealed record TimerView(DateTimeOffset ServerNow, DateTimeOffset? EndsAt, bool Paused, int? PausedRemainingSeconds);

public sealed record CueView(CueType Type, string? Text, string? Src, string? Speaker, string? SpeakerName, string? Effect, VoiceProfile? Voice, string? Alternative);

public sealed record CastMember(
    string CharacterId,
    string Name,
    string Title,
    string Pronouns,
    string PublicBio,
    string CostumeTips,
    string? Portrait,
    bool Required,
    string? PlayedBy,
    bool IsNpc,
    VoiceProfile Voice);

public sealed record PlayerSummary(Guid SeatId, string Name, string? CharacterId, bool IsHost, bool IsLocal, bool Ready, bool HasAccused, string? PhotoUrl);

public sealed record ClueView(
    string Id,
    string Title,
    string Text,
    string? Image,
    int Act,
    bool IsPrivate,
    bool SharedPublicly,
    string? FoundAmong,
    PuzzleView? Puzzle);

public sealed record PuzzleView(string Prompt, string Hint, bool Solved, string? SolvedBy, string? SolvedText);

public sealed record SecretView(string CharacterId, string CharacterName, string Text);

public sealed record AccusationProgress(int Submitted, int Total);

public sealed record RevealView(
    int Step,
    int StepCount,
    IReadOnlyList<GuessView> Guesses,
    string? MurdererId,
    string? MurdererName,
    string? Motive,
    string? Method,
    IReadOnlyList<string> Explanation,
    IReadOnlyList<TimelineEntry> Timeline,
    IReadOnlyList<ScoreLine> Scores);

public sealed record GuessView(string PlayerName, string? CharacterName, string? SuspectName, string? Motive, string? Method, bool? Correct, string? Verdict);

public sealed record AwardsView(IReadOnlyList<AwardOption> Awards, int VotesCast, int Voters, IReadOnlyList<AwardResult>? Results, ScoreLine? BestDetective);

public sealed record AwardOption(string Id, string Title);

/// <summary>One player's private screen. Contains the public stage view plus only this seat's secrets.</summary>
public sealed record PlayerView(
    int Version,
    Guid SeatId,
    string Name,
    bool IsHost,
    bool IsLocal,
    bool Ready,
    Dossier? Dossier,
    IReadOnlyList<ClueView> MyClues,
    AccusationForm? AccusationForm,
    AwardBallot? AwardBallot,
    int QuestionsLeft,
    int HintsLeft,
    IReadOnlyList<HintView> MyHints,
    StageView Stage);

public sealed record Dossier(
    CastMember Character,
    bool Unlocked,
    bool IsMurderer,
    string? Backstory,
    string? Alibi,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> Knows,
    IReadOnlyList<DossierSecret> Secrets,
    int LockedSecrets,
    IReadOnlyList<string> LinesThisAct);

public sealed record DossierSecret(string Id, string Text, bool Revealed);

public sealed record AccusationForm(
    IReadOnlyList<Option> Suspects,
    IReadOnlyList<Option> Motives,
    IReadOnlyList<Option> Methods,
    AccusationEntry? Current);

public sealed record AwardBallot(IReadOnlyList<AwardOption> Awards, IReadOnlyList<Option> Nominees, IReadOnlyDictionary<string, Guid> MyVotes);

/// <summary>
/// The after-party page, for a finished game only. It deliberately shows what the
/// game kept hidden (the solution, everyone's secrets) but still leaves out anything
/// private to one guest: their notes and hints.
/// </summary>
public sealed record RecapView(
    ScenarioSummary Scenario,
    IReadOnlyList<RecapCharacter> Cast,
    RevealView Reveal,
    AwardsView Awards,
    IReadOnlyList<InterrogationView> Interrogations,
    IReadOnlyList<SecretView> SecretsRevealedDuringPlay);

public sealed record RecapCharacter(
    string CharacterId,
    string Name,
    string Title,
    string? Portrait,
    string? PlayedBy,
    string? PhotoUrl,
    bool IsNpc,
    bool IsMurderer,
    IReadOnlyList<string> Secrets);

