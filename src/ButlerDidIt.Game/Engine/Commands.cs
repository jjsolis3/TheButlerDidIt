namespace ButlerDidIt.Game.Engine;

// Every change to a game is a Command. The API turns hub calls into commands and
// hands them to GameEngine.Apply. Who is allowed to send which command (host vs
// player) is checked by the API before it gets here; the engine checks the game
// rules (right phase, valid character, and so on).
//
// Commands carry `Now` instead of the engine reading the clock itself. That keeps
// the engine deterministic: the same state + command always gives the same result,
// which makes the tests simple and repeatable.

public abstract record Command(DateTimeOffset Now);

// ---- Lobby ----
public sealed record AddPlayer(DateTimeOffset Now, Guid SeatId, string Name, bool IsHost, bool IsLocal) : Command(Now);
public sealed record RemovePlayer(DateTimeOffset Now, Guid SeatId) : Command(Now);
public sealed record ChooseCharacter(DateTimeOffset Now, Guid SeatId, string? CharacterId) : Command(Now);
public sealed record SetReady(DateTimeOffset Now, Guid SeatId, bool Ready) : Command(Now);
public sealed record AutoAssignCharacters(DateTimeOffset Now) : Command(Now);
public sealed record StartGame(DateTimeOffset Now) : Command(Now);

// ---- Host controls during play ----
public sealed record Advance(DateTimeOffset Now) : Command(Now);
public sealed record DropNextClue(DateTimeOffset Now) : Command(Now);
public sealed record PauseTimer(DateTimeOffset Now) : Command(Now);
public sealed record ResumeTimer(DateTimeOffset Now) : Command(Now);
public sealed record ExtendTimer(DateTimeOffset Now, int Minutes) : Command(Now);

/// <summary>A guest had to leave mid-game; the narrator takes over their character.</summary>
public sealed record ConvertToNpc(DateTimeOffset Now, Guid SeatId) : Command(Now);

/// <summary>Sent by the server's background ticker so midway clues drop on time.</summary>
public sealed record Tick(DateTimeOffset Now) : Command(Now);

// ---- Player actions ----
public sealed record RevealSecret(DateTimeOffset Now, Guid SeatId, string SecretId) : Command(Now);
public sealed record ShareClue(DateTimeOffset Now, Guid SeatId, string ClueId) : Command(Now);
public sealed record SolvePuzzle(DateTimeOffset Now, Guid SeatId, string ClueId, string Answer) : Command(Now);
public sealed record SubmitAccusation(DateTimeOffset Now, Guid SeatId, string SuspectId, string MotiveId, string MethodId) : Command(Now);
public sealed record CastAwardVote(DateTimeOffset Now, Guid SeatId, string AwardId, Guid NomineeSeatId) : Command(Now);

/// <summary>Thrown when a command breaks a game rule. The message is safe to show to players.</summary>
public sealed class GameRuleException(string message) : Exception(message);

// ---- AI-assisted actions ----
// AI calls are slow and can fail, and the engine must stay pure, so each one is
// split into steps: Begin reserves a slot (checking the rules and limits),
// the server calls the AI outside the engine, then Complete stores the answer or
// Cancel gives the slot back.

public sealed record SetAiFeatures(DateTimeOffset Now, AiFeatures Features) : Command(Now);
public sealed record BeginNpcQuestion(DateTimeOffset Now, Guid Id, Guid SeatId, string CharacterId, string Question) : Command(Now);
public sealed record CompleteNpcQuestion(DateTimeOffset Now, Guid Id, string Answer) : Command(Now);
public sealed record CancelNpcQuestion(DateTimeOffset Now, Guid Id) : Command(Now);
public sealed record BeginHint(DateTimeOffset Now, Guid Id, Guid SeatId) : Command(Now);
public sealed record CompleteHint(DateTimeOffset Now, Guid Id, string Text) : Command(Now);
public sealed record CancelHint(DateTimeOffset Now, Guid Id) : Command(Now);
/// <summary>
/// "Begin the evening" found no version whose killer is a guest, so the AI writes one. The
/// cast is frozen meanwhile; the AI version then starts the game with StartGame, or Cancel
/// unfreezes the lobby so a hand-written version can start instead.
/// </summary>
public sealed record BeginTailoring(DateTimeOffset Now) : Command(Now);
public sealed record CancelTailoring(DateTimeOffset Now) : Command(Now);
public sealed record SetVerdicts(DateTimeOffset Now, IReadOnlyDictionary<Guid, string> Verdicts) : Command(Now);

// ---- Media ----
public sealed record SetPartyOptions(DateTimeOffset Now, PartyOptions Options) : Command(Now);

/// <summary>Set or clear (null) a guest's costume selfie.</summary>
/// <summary>Host puts one guest "in the spotlight" (their turn to speak), or clears it with null.</summary>
public sealed record SetSpotlight(DateTimeOffset Now, Guid? SeatId) : Command(Now);

public sealed record SetPlayerPhoto(DateTimeOffset Now, Guid SeatId, string? PhotoUrl) : Command(Now);

public sealed record SetInterrogationAudio(DateTimeOffset Now, Guid Id, string AudioUrl) : Command(Now);
