using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Engine;

/// <summary>
/// Turns the state into what each screen may see. Deliberately never copied: puzzle answers,
/// hints nobody has paid for, other players' clue pieces, and puzzles in stages not yet reached.
/// </summary>
public static class EscapeProjector
{
    public static EscapeStageView Stage(EscapeState s, EscapeRoom room, DateTimeOffset now)
    {
        var playing = s.Phase != EscapePhase.Lobby;
        var stage = playing && s.Phase == EscapePhase.Playing ? room.Stages[s.StageIndex] : null;
        var puzzles = stage is null ? [] : stage.Puzzles.Select(id => Puzzle(s, room, room.FindPuzzle(id)!)).ToList();
        var endText = s.Phase switch
        {
            EscapePhase.Escaped => room.EscapedText,
            EscapePhase.Failed => room.FailedText,
            _ => null,
        };

        return new EscapeStageView(
            s.Version, s.Phase, room.Id, room.Title, room.Synopsis, room.Theme, room.Intro,
            room.TimeLimitMinutes, room.HintPenaltySeconds,
            StageNumber: s.Phase == EscapePhase.Escaped ? room.Stages.Count : (playing ? s.StageIndex + 1 : 0),
            StageCount: room.Stages.Count,
            stage is null ? null : new EscapeStageInfo(stage.Id, stage.Title, stage.Description),
            puzzles,
            s.Inventory.Select(id => room.FindItem(id)!).Select(i => new EscapeItemView(i.Id, i.Name, i.Description)).ToList(),
            s.Players.Select(p => new EscapePlayerSummary(p.SeatId, p.Name, p.IsHost, p.PhotoUrl)).ToList(),
            s.StartedAt, s.Deadline, s.EndedAt, now,
            s.Feed,
            s.Solved.Count, room.Puzzles.Count, s.HintsUsed, s.WrongAttempts,
            endText);
    }

    public static EscapePlayerView Player(EscapeState s, EscapeRoom room, Guid seatId, DateTimeOffset now)
    {
        var me = s.FindPlayer(seatId) ?? throw new ButlerDidIt.Game.Engine.GameRuleException("You're not in this game.");
        var stage = Stage(s, room, now);
        // Only pieces for the stage in front of them: earlier ones are done, later ones would give the room away.
        var open = stage.Puzzles.Where(p => !p.Solved).Select(p => p.Id).ToHashSet();
        var pieces = s.Pieces
            .Where(p => p.SeatId == seatId && open.Contains(p.PuzzleId))
            .Select(p => (Piece: p, Puzzle: room.FindPuzzle(p.PuzzleId)!))
            .Select(x => new EscapePieceView(x.Puzzle.Id, x.Puzzle.Title, x.Puzzle.Pieces[x.Piece.Index]))
            .ToList();
        return new EscapePlayerView(s.Version, stage, me.SeatId, me.Name, me.IsHost, pieces);
    }

    private static EscapePuzzleView Puzzle(EscapeState s, EscapeRoom room, EscapePuzzle p)
    {
        var solved = s.Solved.FirstOrDefault(x => x.PuzzleId == p.Id);
        var shown = s.HintsShown.GetValueOrDefault(p.Id);
        return new EscapePuzzleView(
            p.Id, p.Title, p.Kind, p.Prompt,
            Solved: solved is not null,
            SolvedBy: solved?.SolvedBy,
            SolvedText: solved is null ? null : p.SolvedText,
            Needs: solved is null ? p.Requires.Where(i => !s.Inventory.Contains(i)).Select(i => room.FindItem(i)!.Name).ToList() : [],
            Hints: p.Hints.Take(shown).ToList(),
            HintsLeft: p.Hints.Count - shown,
            LockedUntil: s.LockedUntil.TryGetValue(p.Id, out var until) ? until : null,
            PieceCount: s.Pieces.Where(x => x.PuzzleId == p.Id).Select(x => x.SeatId).Distinct().Count());
    }
}
