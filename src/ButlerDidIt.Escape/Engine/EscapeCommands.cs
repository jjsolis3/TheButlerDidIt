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
