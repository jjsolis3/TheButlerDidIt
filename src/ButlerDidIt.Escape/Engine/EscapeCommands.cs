namespace ButlerDidIt.Escape.Engine;

/// <summary>Something a person (or the clock) does. Every command carries the time it happened.</summary>
public abstract record EscapeCommand(DateTimeOffset Now);

public sealed record AddEscapePlayer(DateTimeOffset Now, Guid SeatId, string Name, bool IsHost, bool IsLocal) : EscapeCommand(Now);
public sealed record RemoveEscapePlayer(DateTimeOffset Now, Guid SeatId) : EscapeCommand(Now);
public sealed record SetEscapePlayerPhoto(DateTimeOffset Now, Guid SeatId, string? PhotoUrl) : EscapeCommand(Now);

/// <summary>The clock: ends the game when time runs out.</summary>
public sealed record EscapeTick(DateTimeOffset Now) : EscapeCommand(Now);

/// <summary>The host starts the clock. Clue pieces are dealt to the phones.</summary>
public sealed record StartEscape(DateTimeOffset Now) : EscapeCommand(Now);

/// <summary>A player types a code or a word for a puzzle.</summary>
public sealed record SubmitAnswer(DateTimeOffset Now, Guid SeatId, string PuzzleId, string Answer) : EscapeCommand(Now);

/// <summary>A player uses the items a Use puzzle needs (the key in the lock).</summary>
public sealed record UseItems(DateTimeOffset Now, Guid SeatId, string PuzzleId) : EscapeCommand(Now);

/// <summary>Reveal a puzzle's next hint, at the cost of time. <paramref name="SeatId"/> is null when the host asks from the TV.</summary>
public sealed record RequestEscapeHint(DateTimeOffset Now, Guid? SeatId, string PuzzleId) : EscapeCommand(Now);

/// <summary>An AI hint: charges the time and reserves the hint step at once, while the AI writes it outside the lock.</summary>
public sealed record BeginEscapeHint(DateTimeOffset Now, Guid HintId, Guid? SeatId, string PuzzleId) : EscapeCommand(Now);

/// <summary>The AI's hint. If it gives the answer away, the room's written hint is shown instead.</summary>
public sealed record CompleteEscapeHint(DateTimeOffset Now, Guid HintId, string Text) : EscapeCommand(Now);

/// <summary>The AI failed: the room's written hint is shown instead (the time was still spent).</summary>
public sealed record CancelEscapeHint(DateTimeOffset Now, Guid HintId) : EscapeCommand(Now);

/// <summary>The game master's line for a moment, written by the AI.</summary>
public sealed record SetCueNarration(DateTimeOffset Now, int CueId, string Text) : EscapeCommand(Now);

/// <summary>A recording of that line.</summary>
public sealed record SetCueAudio(DateTimeOffset Now, int CueId, string Url) : EscapeCommand(Now);

/// <summary>Several moments piled up: only the newest gets a line, the ones before it are passed over.</summary>
public sealed record SkipCues(DateTimeOffset Now, int BeforeCueId) : EscapeCommand(Now);
