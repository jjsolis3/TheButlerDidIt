using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;

namespace ButlerDidIt.Escape.Engine;

/// <summary>
/// The escape-room rules as one pure function: (state, room, command) → new state.
/// No database, no clock, no randomness, so every rule is easy to test. A command that breaks
/// a rule throws <see cref="GameRuleException"/> with a message players can read. A command
/// that changes nothing returns the same state instance.
/// </summary>
public static class EscapeEngine
{
    /// <summary>A wrong answer locks that puzzle for this long.</summary>
    public static readonly TimeSpan WrongAnswerCooldown = TimeSpan.FromSeconds(3);
    private const int FeedLength = 12;

    public static EscapeState NewGame(long seed = 0, bool daily = false) => new() { Seed = seed, Daily = daily };

    /// <summary>The room as this attempt plays it: its variants and generated codes picked from the state's seed.</summary>
    public static EscapeRoom RoomFor(EscapeState s, EscapeRoom room) => RoomVariants.Build(room, s.Seed);

    public static DateTimeOffset? NextDueAt(EscapeState s) => s.Phase == EscapePhase.Playing ? s.Deadline : null;

    public static EscapeState Apply(EscapeState state, EscapeRoom template, EscapeCommand command)
    {
        if (command is EscapeTick tick) return Tick(state, tick.Now);
        var room = RoomFor(state, template);

        var s = Clone(state);
        switch (command)
        {
            case AddEscapePlayer c: AddPlayer(s, room, c); break;
            case RemoveEscapePlayer c: RemovePlayer(s, c); break;
            case SetEscapePlayerPhoto c: RequirePlayer(s, c.SeatId).PhotoUrl = c.PhotoUrl; break;
            case StartEscape c: Start(s, room, c.Now); break;
            case SubmitAnswer c: Answer(s, room, c); break;
            case UseItems c: Use(s, room, c); break;
            case RequestEscapeHint c: Hint(s, room, c); break;
            default: throw new GameRuleException($"Unknown command {command.GetType().Name}.");
        }
        s.Version++;
        return s;
    }

    private static EscapeState Tick(EscapeState state, DateTimeOffset now)
    {
        if (state.Phase != EscapePhase.Playing || state.Deadline is not { } deadline || now < deadline) return state;
        var s = Clone(state);
        End(s, EscapePhase.Failed, deadline, "⏰ Time's up.");
        s.Version++;
        return s;
    }

    private static void AddPlayer(EscapeState s, EscapeRoom room, AddEscapePlayer c)
    {
        if (s.Phase != EscapePhase.Lobby) throw new GameRuleException("This escape has already started.");
        if (s.Players.Count >= room.MaxPlayers) throw new GameRuleException($"This room is for at most {room.MaxPlayers} players.");
        var name = c.Name.Trim();
        if (name.Length is 0 or > 40) throw new GameRuleException("Pick a name of 1–40 characters.");
        s.Players.Add(new EscapePlayer { SeatId = c.SeatId, Name = name, IsHost = c.IsHost, IsLocal = c.IsLocal });
    }

    private static void RemovePlayer(EscapeState s, RemoveEscapePlayer c)
    {
        s.Players.RemoveAll(p => p.SeatId == c.SeatId);
        // Their clue pieces go to someone still playing, so no puzzle becomes unsolvable.
        if (s.Players.Count == 0) { s.Pieces.Clear(); return; }
        s.Pieces = s.Pieces.Select((p, i) => p.SeatId == c.SeatId ? p with { SeatId = s.Players[i % s.Players.Count].SeatId } : p).ToList();
    }

    private static void Start(EscapeState s, EscapeRoom room, DateTimeOffset now)
    {
        if (s.Phase != EscapePhase.Lobby) throw new GameRuleException("The clock is already running.");
        if (s.Players.Count == 0) throw new GameRuleException("Someone has to join before the clock can start.");

        // Deal each puzzle's pieces round the table, starting one seat further along each time,
        // so everyone ends up holding something and nobody holds a whole puzzle.
        var offset = 0;
        foreach (var puzzle in room.Puzzles)
        {
            for (var i = 0; i < puzzle.Pieces.Count; i++)
                s.Pieces.Add(new PieceHolder(puzzle.Id, i, s.Players[(offset + i) % s.Players.Count].SeatId));
            offset += puzzle.Pieces.Count;
        }

        s.Phase = EscapePhase.Playing;
        s.StartedAt = now;
        s.Deadline = now.AddMinutes(room.TimeLimitMinutes);
        s.StageIndex = 0;
        Log(s, now, $"🔒 The clock is running: {room.TimeLimitMinutes} minutes.");
    }

    private static void Answer(EscapeState s, EscapeRoom room, SubmitAnswer c)
    {
        var (player, puzzle) = Attempt(s, room, c.SeatId, c.PuzzleId);
        if (puzzle.Kind == PuzzleKind.Use) throw new GameRuleException("This one isn't opened with an answer: use the items it needs.");
        if (s.LockedUntil.TryGetValue(puzzle.Id, out var until) && c.Now < until)
            throw new GameRuleException("The lock is resetting. Try again in a moment.");

        if (Answers.Matches(puzzle, c.Answer))
        {
            Solve(s, room, puzzle, player.Name, c.Now);
            return;
        }
        s.WrongAttempts++;
        s.LockedUntil[puzzle.Id] = c.Now + WrongAnswerCooldown;
        Log(s, c.Now, $"✗ {player.Name} tried “{Trim(c.Answer)}” on {puzzle.Title}. Nothing.");
    }

    private static void Use(EscapeState s, EscapeRoom room, UseItems c)
    {
        var (player, puzzle) = Attempt(s, room, c.SeatId, c.PuzzleId);
        if (puzzle.Kind != PuzzleKind.Use) throw new GameRuleException("This one needs an answer.");
        Solve(s, room, puzzle, player.Name, c.Now);
    }

    private static void Hint(EscapeState s, EscapeRoom room, RequestEscapeHint c)
    {
        if (s.Phase != EscapePhase.Playing) throw new GameRuleException("Hints are only for while the clock runs.");
        if (c.SeatId is { } seat) RequirePlayer(s, seat);
        var puzzle = OpenPuzzle(s, room, c.PuzzleId);
        var shown = s.HintsShown.GetValueOrDefault(puzzle.Id);
        if (shown >= puzzle.Hints.Count) throw new GameRuleException("There are no more hints for this one.");

        s.HintsShown[puzzle.Id] = shown + 1;
        s.Deadline = s.Deadline!.Value.AddSeconds(-room.HintPenaltySeconds);
        Log(s, c.Now, $"💡 A hint for {puzzle.Title} (−{FormatPenalty(room.HintPenaltySeconds)}).");
        if (s.Deadline <= c.Now) End(s, EscapePhase.Failed, c.Now, "⏰ That hint cost the last of your time.");
    }

    /// <summary>The checks every attempt shares: the game is on, the person is playing, the puzzle is open and its items are in hand.</summary>
    private static (EscapePlayer Player, EscapePuzzle Puzzle) Attempt(EscapeState s, EscapeRoom room, Guid seatId, string puzzleId)
    {
        if (s.Phase != EscapePhase.Playing) throw new GameRuleException(s.Phase == EscapePhase.Lobby ? "The clock hasn't started yet." : "The game is over.");
        var player = RequirePlayer(s, seatId);
        var puzzle = OpenPuzzle(s, room, puzzleId);
        var missing = puzzle.Requires.Where(i => !s.Inventory.Contains(i)).Select(i => room.FindItem(i)?.Name ?? i).ToList();
        if (missing.Count > 0) throw new GameRuleException($"You need {string.Join(" and ", missing)} first.");
        return (player, puzzle);
    }

    /// <summary>A puzzle in the current stage that hasn't been solved yet.</summary>
    private static EscapePuzzle OpenPuzzle(EscapeState s, EscapeRoom room, string puzzleId)
    {
        var stage = room.Stages[s.StageIndex];
        if (!stage.Puzzles.Contains(puzzleId) || room.FindPuzzle(puzzleId) is not { } puzzle)
            throw new GameRuleException("That isn't in this part of the room.");
        if (s.IsSolved(puzzleId)) throw new GameRuleException("That's already solved.");
        return puzzle;
    }

    private static void Solve(EscapeState s, EscapeRoom room, EscapePuzzle puzzle, string by, DateTimeOffset now)
    {
        s.Solved.Add(new SolvedPuzzle(puzzle.Id, by, now));
        s.Inventory.RemoveAll(puzzle.Requires.Contains); // used up: the key stays in its lock
        s.Inventory.AddRange(puzzle.Rewards.Where(r => !s.Inventory.Contains(r)));
        s.LockedUntil.Remove(puzzle.Id);
        Log(s, now, $"✓ {by} solved {puzzle.Title}.");

        var stage = room.Stages[s.StageIndex];
        if (!stage.Puzzles.All(s.IsSolved)) return;
        if (s.StageIndex + 1 < room.Stages.Count)
        {
            s.StageIndex++;
            Log(s, now, $"🚪 {room.Stages[s.StageIndex].Title}");
        }
        else
        {
            End(s, EscapePhase.Escaped, now, "🏁 You escaped!");
        }
    }

    private static void End(EscapeState s, EscapePhase phase, DateTimeOffset at, string message)
    {
        s.Phase = phase;
        s.EndedAt = at;
        s.LockedUntil.Clear();
        Log(s, at, message);
    }

    private static EscapePlayer RequirePlayer(EscapeState s, Guid seatId) =>
        s.FindPlayer(seatId) ?? throw new GameRuleException("You're not in this game.");

    private static void Log(EscapeState s, DateTimeOffset at, string text)
    {
        s.Feed.Add(new EscapeFeedEntry(at, text));
        if (s.Feed.Count > FeedLength) s.Feed.RemoveRange(0, s.Feed.Count - FeedLength);
    }

    private static string Trim(string text)
    {
        var t = text.Trim();
        return t.Length <= 24 ? t : t[..24] + "…";
    }

    public static string FormatPenalty(int seconds) => seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";

    private static EscapeState Clone(EscapeState s) => GameJson.Deserialize<EscapeState>(GameJson.Serialize(s));
}
