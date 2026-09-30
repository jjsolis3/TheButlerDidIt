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
    private const int CueLength = 10;
    private const int RecentWrongLength = 5;
    private const int WrongStreakCue = 3;

    /// <summary>The game master warns the group this long before the clock runs out.</summary>
    public static readonly TimeSpan LowTimeWarning = TimeSpan.FromMinutes(5);

    /// <summary>Longest AI hint or line kept. Longer ones are cut (lines) or refused (hints).</summary>
    public const int MaxAiText = 400;

    /// <summary>How long an AI hint may take before the written one is shown instead.</summary>
    public static readonly TimeSpan AiHintTimeout = TimeSpan.FromSeconds(60);

    /// <summary>On Hard, searching a spot with nothing in it costs this much time.</summary>
    public const int DecoyPenaltySeconds = 10;

    /// <param name="minutes">The game's length, one of the room's lengths; null plays the room's own time limit.</param>
    /// <param name="difficulty">Null plays Normal.</param>
    public static EscapeState NewGame(long seed = 0, bool daily = false, EscapeAiFeatures? ai = null, int? minutes = null, EscapeDifficulty? difficulty = null) =>
        new() { Seed = seed, Daily = daily, Ai = ai ?? new EscapeAiFeatures(), Minutes = minutes, Difficulty = difficulty };

    /// <summary>
    /// The room as this attempt plays it: its variants and generated codes picked from the state's seed
    /// at its difficulty, then cut to the game's length.
    /// </summary>
    public static EscapeRoom RoomFor(EscapeState s, EscapeRoom room) =>
        RoomLengths.Cut(RoomVariants.Build(room, s.Seed, s.Level), s.Minutes, s.Level);

    /// <summary>When the ticker should look again: the deadline, or the "five minutes left" warning before it.</summary>
    public static DateTimeOffset? NextDueAt(EscapeState s)
    {
        if (s.Phase != EscapePhase.Playing || s.Deadline is not { } deadline) return null;
        return s.LowTimeCued || !s.Ai.GameMaster ? deadline : deadline - LowTimeWarning;
    }

    public static EscapeState Apply(EscapeState state, EscapeRoom template, EscapeCommand command)
    {
        if (command is EscapeTick tick) return Tick(state, tick.Now);
        // AI results that arrive after their hint or moment is gone (cancelled, or scrolled out) change nothing.
        if (IsStale(state, command)) return state;
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
            case ExamineSpot c: Examine(s, room, c); break;
            case InspectItem c: Inspect(s, room, c); break;
            case CombineItems c: Combine(s, room, c); break;
            case PressSwitch c: Press(s, room, c); break;
            case RequestEscapeHint c: Hint(s, room, c.Now, c.SeatId, c.PuzzleId); break;
            case BeginEscapeHint c: BeginAiHint(s, room, c); break;
            case CompleteEscapeHint c: CompleteAiHint(s, room, c); break;
            case CancelEscapeHint c: s.AiHints.RemoveAll(h => h.Id == c.HintId); break;
            case SetCueNarration c: FindCue(s, c.CueId)!.Text = Cut(c.Text); break;
            case SetCueAudio c: FindCue(s, c.CueId)!.AudioUrl = c.Url; break;
            case SkipCues c: foreach (var cue in s.Cues.Where(x => x.Id < c.BeforeCueId && x.Text is null)) cue.Skipped = true; break;
            default: throw new GameRuleException($"Unknown command {command.GetType().Name}.");
        }
        NoteKeys(s, room);
        s.Version++;
        return s;
    }

    private static EscapeState Tick(EscapeState state, DateTimeOffset now)
    {
        if (state.Phase != EscapePhase.Playing || state.Deadline is not { } deadline) return state;
        if (now >= deadline)
        {
            var ended = Clone(state);
            End(ended, EscapePhase.Failed, deadline, "⏰ Time's up.");
            ended.Version++;
            return ended;
        }
        if (state.LowTimeCued || !state.Ai.GameMaster || now < deadline - LowTimeWarning) return state;
        var s = Clone(state);
        s.LowTimeCued = true;
        Cue(s, CueKind.LowTime, now);
        s.Version++;
        return s;
    }

    private static bool IsStale(EscapeState s, EscapeCommand command) => command switch
    {
        CompleteEscapeHint c => !s.AiHints.Any(h => h.Id == c.HintId && h.Text is null),
        CancelEscapeHint c => !s.AiHints.Any(h => h.Id == c.HintId && h.Text is null),
        SetCueNarration c => FindCue(s, c.CueId) is not { Text: null },
        SetCueAudio c => FindCue(s, c.CueId) is not { Text: not null, AudioUrl: null },
        SkipCues c => !s.Cues.Any(x => x.Id < c.BeforeCueId && x.Text is null && !x.Skipped),
        _ => false,
    };

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
        // Pieces still hidden in the room stay where they are.
        if (s.Players.Count == 0) { s.Pieces.RemoveAll(p => !p.IsHidden); return; }
        s.Pieces = s.Pieces.Select((p, i) => p.SeatId == c.SeatId ? p with { SeatId = s.Players[i % s.Players.Count].SeatId } : p).ToList();
    }

    private static void Start(EscapeState s, EscapeRoom room, DateTimeOffset now)
    {
        if (s.Phase != EscapePhase.Lobby) throw new GameRuleException("The clock is already running.");
        if (s.Players.Count == 0) throw new GameRuleException("Someone has to join before the clock can start.");

        Deal(s, room);

        s.Phase = EscapePhase.Playing;
        s.StartedAt = now;
        s.Deadline = now.AddMinutes(room.TimeLimitMinutes);
        s.StageIndex = 0;
        // A warning only makes sense when there's real time before it.
        s.LowTimeCued = !s.Ai.GameMaster || room.TimeLimitMinutes < 2 * LowTimeWarning.TotalMinutes;
        Log(s, now, $"🔒 The clock is running: {room.TimeLimitMinutes} minutes.");
        Cue(s, CueKind.Start, now, stage: room.Stages[0].Title);
    }

    /// <summary>
    /// Deals each puzzle's pieces round the table, starting one seat further along each time, so
    /// everyone ends up holding something and nobody holds a whole puzzle. When a puzzle has more
    /// pieces than there are players and its part of the room has hiding spots, the pieces nobody
    /// would have held go into those spots instead of doubling up on a phone: a solo player has to
    /// search for them. Which spots is picked from the seed, one piece per spot.
    /// </summary>
    private static void Deal(EscapeState s, EscapeRoom room)
    {
        var free = room.Stages.ToDictionary(
            st => st.Id,
            st => new Queue<string>(new SeededRandom(s.Seed ^ SeededRandom.StableHash(st.Id)).Pick(
                (st.Scene?.Objects ?? []).Where(o => o.HidesPieces).Select(o => o.Id).ToList(), int.MaxValue)));
        var offset = 0;
        foreach (var puzzle in room.Puzzles)
        {
            var spots = room.StageOf(puzzle.Id) is { } stage ? free[stage.Id] : new Queue<string>();
            for (var i = 0; i < puzzle.Pieces.Count; i++)
            {
                s.Pieces.Add(i >= s.Players.Count && spots.TryDequeue(out var spot)
                    ? new PieceHolder(puzzle.Id, i, null, spot)
                    : new PieceHolder(puzzle.Id, i, s.Players[(offset + i) % s.Players.Count].SeatId));
            }
            offset += puzzle.Pieces.Count;
        }
    }

    private static void Answer(EscapeState s, EscapeRoom room, SubmitAnswer c)
    {
        var (player, puzzle) = Attempt(s, room, c.SeatId, c.PuzzleId);
        if (puzzle.Kind != PuzzleKind.Code && puzzle.Kind != PuzzleKind.Text) throw new GameRuleException(puzzle.Kind switch
        {
            PuzzleKind.Use => "This one isn't opened with an answer: use the items it needs.",
            PuzzleKind.Search => "This one isn't opened with an answer: search the room.",
            _ => "This one isn't opened with an answer: work the switches.",
        });
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

        var tries = s.RecentWrong.TryGetValue(puzzle.Id, out var list) ? list : s.RecentWrong[puzzle.Id] = [];
        tries.Add(Trim(c.Answer));
        if (tries.Count > RecentWrongLength) tries.RemoveRange(0, tries.Count - RecentWrongLength);
        if (++s.WrongStreak >= WrongStreakCue)
        {
            s.WrongStreak = 0;
            Cue(s, CueKind.WrongStreak, c.Now, puzzle.Title, player.Name);
        }
    }

    private static void Use(EscapeState s, EscapeRoom room, UseItems c)
    {
        var (player, puzzle) = Attempt(s, room, c.SeatId, c.PuzzleId);
        if (puzzle.Kind != PuzzleKind.Use) throw new GameRuleException(puzzle.Kind switch
        {
            PuzzleKind.Search => "This one opens once you've searched the right places.",
            PuzzleKind.Switches => "This one needs the switches worked.",
            _ => "This one needs an answer.",
        });
        Solve(s, room, puzzle, player.Name, c.Now);
    }

    /// <summary>
    /// Searching a spot: the searcher sees what's there, the group gets whatever it holds (an item, a clue
    /// for the notebook, hidden clue pieces for the searcher's phone), and Search puzzles finish themselves.
    /// A spot that needs a tool (the UV lamp) shows nothing without it and can be searched again later.
    /// </summary>
    private static void Examine(EscapeState s, EscapeRoom room, ExamineSpot c)
    {
        var player = Playing(s, c.SeatId);
        var spot = room.Stages[s.StageIndex].Scene?.Objects.FirstOrDefault(o => o.Id == c.ObjectId)
            ?? throw new GameRuleException("That isn't in this part of the room.");
        if (s.Examined.Contains(spot.Id)) throw new GameRuleException($"Someone has already searched the {spot.Label}.");
        if (spot.Requires is { } tool && !s.Inventory.Contains(tool))
            throw new GameRuleException(spot.LockedText ?? $"You can't make anything out at the {spot.Label}. Not yet, anyway.");

        s.Examined.Add(spot.Id);
        var found = false;
        if (spot.Gives is { } item && !s.Inventory.Contains(item))
        {
            s.Inventory.Add(item);
            Log(s, c.Now, $"🔎 {player.Name} searched the {spot.Label} and found {room.FindItem(item)!.Name}.");
            found = true;
        }
        if (spot.Clue is { } clue)
        {
            s.Notebook.Add(new NotebookEntry(c.Now, Capitalise(spot.Label), clue));
            found = true;
        }
        var hidden = s.Pieces.Where(p => p.SpotId == spot.Id && p.IsHidden).ToList();
        foreach (var piece in hidden)
            s.Pieces[s.Pieces.IndexOf(piece)] = piece with { SeatId = player.SeatId };
        if (hidden.Count > 0)
        {
            Log(s, c.Now, $"🧩 {player.Name} found a clue piece in the {spot.Label}.");
            found = true;
        }
        if (!found)
        {
            if (s.Level == EscapeDifficulty.Hard && IsDecoy(room, spot))
            {
                s.Deadline = s.Deadline!.Value.AddSeconds(-DecoyPenaltySeconds);
                Log(s, c.Now, $"🔎 {player.Name} searched the {spot.Label}. Nothing there (−{FormatPenalty(DecoyPenaltySeconds)}).");
                if (s.Deadline <= c.Now) { End(s, EscapePhase.Failed, c.Now, "⏰ That search cost the last of your time."); return; }
                Cue(s, CueKind.Decoy, c.Now, by: player.Name, thing: spot.Label);
            }
            else
            {
                Log(s, c.Now, $"🔎 {player.Name} searched the {spot.Label}.");
            }
        }

        if (!SolveSearches(s, room, player.Name, c.Now) && found)
            Cue(s, CueKind.Found, c.Now, by: player.Name, thing: spot.Label);
    }

    /// <summary>A spot with nothing to find, and no part in any puzzle.</summary>
    private static bool IsDecoy(EscapeRoom room, SceneObject spot) =>
        spot is { Gives: null, Clue: null, HidesPieces: false }
        && !room.Puzzles.Any(p => p.Finds.Contains(spot.Id) || p.KeyAt.Contains($"object:{spot.Id}"));

    /// <summary>Finishes every Search puzzle in the current stage whose spots have all been searched. True if any was.</summary>
    private static bool SolveSearches(EscapeState s, EscapeRoom room, string by, DateTimeOffset now)
    {
        var any = false;
        while (s.Phase == EscapePhase.Playing
            && room.Stages[s.StageIndex].Puzzles.Select(id => room.FindPuzzle(id)!)
                .FirstOrDefault(p => p.Kind == PuzzleKind.Search && !s.IsSolved(p.Id) && p.Finds.All(s.Examined.Contains)) is { } done)
        {
            Solve(s, room, done, by, now);
            any = true;
        }
        return any;
    }

    /// <summary>A closer look at an item: its hidden detail goes into the notebook, and it may give up another item.</summary>
    private static void Inspect(EscapeState s, EscapeRoom room, InspectItem c)
    {
        var player = Playing(s, c.SeatId);
        if (!s.Inventory.Contains(c.ItemId) || room.FindItem(c.ItemId) is not { } item) throw new GameRuleException("The group isn't holding that.");
        if (item.Inspect is null) throw new GameRuleException($"There's nothing more to see on {item.Name}.");
        if (s.Inspected.Contains(item.Id)) throw new GameRuleException($"Someone has already had a close look at {item.Name}.");
        if (item.InspectRequires is { } tool && !s.Inventory.Contains(tool))
            throw new GameRuleException($"You'd need {room.FindItem(tool)!.Name} to see more.");

        s.Inspected.Add(item.Id);
        s.Notebook.Add(new NotebookEntry(c.Now, item.Name, item.Inspect));
        if (item.InspectGives is { } gives && !s.Inventory.Contains(gives))
        {
            s.Inventory.Add(gives);
            Log(s, c.Now, $"🔍 {player.Name} looked closely at {item.Name} and found {room.FindItem(gives)!.Name}.");
        }
        else
        {
            Log(s, c.Now, $"🔍 {player.Name} looked closely at {item.Name}.");
        }
        Cue(s, CueKind.Found, c.Now, by: player.Name, thing: item.Name);
    }

    /// <summary>Two items tried together. A pair that fits makes a new item (both are used up); any other pair is just logged.</summary>
    private static void Combine(EscapeState s, EscapeRoom room, CombineItems c)
    {
        var player = Playing(s, c.SeatId);
        if (c.First == c.Second) throw new GameRuleException("Pick two different items.");
        if (!s.Inventory.Contains(c.First) || !s.Inventory.Contains(c.Second)) throw new GameRuleException("The group isn't holding both of those.");
        string first = room.FindItem(c.First)!.Name, second = room.FindItem(c.Second)!.Name;

        var recipe = room.Recipes.FirstOrDefault(r => r.Items.Count == 2 && r.Items.Contains(c.First) && r.Items.Contains(c.Second));
        if (recipe is null)
        {
            Log(s, c.Now, $"🔧 {player.Name} tried {first} with {second}. They don't fit together.");
            return;
        }
        s.Inventory.RemoveAll(recipe.Items.Contains);
        if (!s.Inventory.Contains(recipe.Makes)) s.Inventory.Add(recipe.Makes);
        var made = room.FindItem(recipe.Makes)!.Name;
        if (recipe.Text.Length > 0) s.Notebook.Add(new NotebookEntry(c.Now, made, recipe.Text));
        Log(s, c.Now, $"🔧 {player.Name} put {first} and {second} together: {made}.");
        Cue(s, CueKind.Found, c.Now, by: player.Name, thing: made);
    }

    /// <summary>A press flips that light and the ones next to it. All on solves the puzzle.</summary>
    private static void Press(EscapeState s, EscapeRoom room, PressSwitch c)
    {
        var (player, puzzle) = Attempt(s, room, c.SeatId, c.PuzzleId);
        if (puzzle is not { Kind: PuzzleKind.Switches, Grid: { } grid }) throw new GameRuleException("There are no switches on this one.");
        if (c.Cell < 0 || c.Cell >= grid.Size * grid.Size) throw new GameRuleException("There's no switch there.");

        var lit = PuzzleGenerators.Press(grid.Size, LitNow(s, puzzle), c.Cell);
        s.Switches[puzzle.Id] = lit;
        if (lit.Count == grid.Size * grid.Size) Solve(s, room, puzzle, player.Name, c.Now);
    }

    /// <summary>
    /// Records every cipher key the group can now read: on a spot it has searched, an item it holds or has
    /// looked at, or a puzzle in front of it or behind it (the validator's rule). Once found, a key stays found.
    /// </summary>
    private static void NoteKeys(EscapeState s, EscapeRoom room)
    {
        if (s.Phase == EscapePhase.Lobby) return;
        foreach (var p in room.Puzzles.Where(p => p.Decoder is not null && p.KeyAt.Count > 0 && !s.KeysFound.Contains(p.Id)))
        {
            if (p.KeyAt.Any(place => Seen(s, room, place))) s.KeysFound.Add(p.Id);
        }
    }

    /// <summary>True once the group can read what's written at a key place (see <see cref="RoomVariants.KeyPlaces"/>).</summary>
    public static bool PlaceSeen(EscapeState s, EscapeRoom room, string place) => place.Contains(':') && Seen(s, room, place);

    private static bool Seen(EscapeState s, EscapeRoom room, string place)
    {
        var split = place.IndexOf(':');
        var (kind, id) = (place[..split], place[(split + 1)..]);
        return kind switch
        {
            "object" => s.Examined.Contains(id),
            "item" => s.Inventory.Contains(id),
            "inspect" => s.Inspected.Contains(id),
            _ => room.Stages.FindIndex(st => st.Puzzles.Contains(id)) is var at and >= 0 && at <= s.StageIndex,
        };
    }

    /// <summary>True once the group can read this cipher's key (or it needs none).</summary>
    public static bool KeyFound(EscapeState s, EscapePuzzle puzzle) => puzzle.KeyAt.Count == 0 || s.KeysFound.Contains(puzzle.Id);

    /// <summary>The lights on in a Switches puzzle right now.</summary>
    public static IReadOnlyList<int> LitNow(EscapeState s, EscapePuzzle puzzle) =>
        s.Switches.TryGetValue(puzzle.Id, out var lit) ? lit : puzzle.Grid?.Lit ?? [];

    private static EscapePlayer Playing(EscapeState s, Guid seatId)
    {
        if (s.Phase != EscapePhase.Playing) throw new GameRuleException(s.Phase == EscapePhase.Lobby ? "The clock hasn't started yet." : "The game is over.");
        return RequirePlayer(s, seatId);
    }

    /// <summary>Pays for the puzzle's next hint step and returns its index. The written hint for it is the fallback for an AI one.</summary>
    private static int Hint(EscapeState s, EscapeRoom room, DateTimeOffset now, Guid? seatId, string puzzleId)
    {
        if (s.Phase != EscapePhase.Playing) throw new GameRuleException("Hints are only for while the clock runs.");
        if (seatId is { } seat) RequirePlayer(s, seat);
        var puzzle = OpenPuzzle(s, room, puzzleId);
        var shown = s.HintsShown.GetValueOrDefault(puzzle.Id);
        if (shown >= puzzle.Hints.Count) throw new GameRuleException("There are no more hints for this one.");
        if (s.AiHints.Any(h => h.PuzzleId == puzzle.Id && h.IsPending(now))) throw new GameRuleException("A hint for this one is on its way.");

        s.HintsShown[puzzle.Id] = shown + 1;
        s.Deadline = s.Deadline!.Value.AddSeconds(-room.HintPenaltySeconds);
        Log(s, now, $"💡 A hint for {puzzle.Title} (−{FormatPenalty(room.HintPenaltySeconds)}).");
        if (s.Deadline <= now) End(s, EscapePhase.Failed, now, "⏰ That hint cost the last of your time.");
        return shown;
    }

    private static void BeginAiHint(EscapeState s, EscapeRoom room, BeginEscapeHint c)
    {
        if (!s.Ai.Hints) throw new GameRuleException("This party doesn't use AI hints.");
        var index = Hint(s, room, c.Now, c.SeatId, c.PuzzleId);
        if (s.Phase == EscapePhase.Playing) s.AiHints.Add(new AiHint { Id = c.HintId, PuzzleId = c.PuzzleId, Index = index, At = c.Now });
    }

    /// <summary>
    /// The engine checks the AI's hint itself, against the answers of the puzzle set being played:
    /// a hint that gives the answer away is dropped, and the room's written hint shows instead.
    /// </summary>
    private static void CompleteAiHint(EscapeState s, EscapeRoom room, CompleteEscapeHint c)
    {
        var hint = s.AiHints.First(h => h.Id == c.HintId);
        var puzzle = room.FindPuzzle(hint.PuzzleId)!;
        if (EscapeHintGuard.Rejects(c.Text, puzzle)) s.AiHints.Remove(hint);
        else hint.Text = c.Text.Trim();
    }

    /// <summary>The checks every attempt shares: the game is on, the person is playing, the puzzle is open and its items are in hand.</summary>
    private static (EscapePlayer Player, EscapePuzzle Puzzle) Attempt(EscapeState s, EscapeRoom room, Guid seatId, string puzzleId)
    {
        var player = Playing(s, seatId);
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
        s.WrongStreak = 0;
        Log(s, now, $"✓ {by} solved {puzzle.Title}.");

        // One cue per moment: opening a stage or escaping says more than the solve that caused it.
        var stage = room.Stages[s.StageIndex];
        if (!stage.Puzzles.All(s.IsSolved))
        {
            Cue(s, CueKind.Solved, now, puzzle.Title, by);
            return;
        }
        if (s.StageIndex + 1 < room.Stages.Count)
        {
            s.StageIndex++;
            Log(s, now, $"🚪 {room.Stages[s.StageIndex].Title}");
            Cue(s, CueKind.StageOpened, now, puzzle.Title, by, room.Stages[s.StageIndex].Title);
        }
        else
        {
            End(s, EscapePhase.Escaped, now, "🏁 You escaped!", puzzle.Title, by);
        }
    }

    private static void End(EscapeState s, EscapePhase phase, DateTimeOffset at, string message, string? puzzle = null, string? by = null)
    {
        s.Phase = phase;
        s.EndedAt = at;
        s.LockedUntil.Clear();
        // Hints still being written can't be shown any more: drop them rather than leave them "on their way".
        s.AiHints.RemoveAll(h => h.Text is null);
        Log(s, at, message);
        Cue(s, phase == EscapePhase.Escaped ? CueKind.Escaped : CueKind.Failed, at, puzzle, by);
    }

    /// <summary>Records a moment for the game master, when it's on. The engine only notes it; the AI writes the line later.</summary>
    private static void Cue(EscapeState s, CueKind kind, DateTimeOffset at, string? puzzle = null, string? by = null, string? stage = null, string? thing = null)
    {
        if (!s.Ai.GameMaster) return;
        s.Cues.Add(new EscapeCue { Id = s.NextCueId++, Kind = kind, At = at, PuzzleTitle = puzzle, PlayerName = by, StageTitle = stage, Thing = thing });
        if (s.Cues.Count > CueLength) s.Cues.RemoveRange(0, s.Cues.Count - CueLength);
    }

    private static EscapeCue? FindCue(EscapeState s, int id) => s.Cues.FirstOrDefault(c => c.Id == id);

    private static string Cut(string text)
    {
        var t = text.Trim();
        return t.Length <= MaxAiText ? t : t[..MaxAiText].TrimEnd() + "…";
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

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string FormatPenalty(int seconds) => seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";

    private static EscapeState Clone(EscapeState s) => GameJson.Deserialize<EscapeState>(GameJson.Serialize(s));
}
