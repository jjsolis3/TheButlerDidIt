using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Engine;

/// <summary>
/// Turns the state into what each screen may see. Deliberately never copied: puzzle answers,
/// hints nobody has paid for, other players' clue pieces, and puzzles in stages not yet reached;
/// what's in a spot nobody has searched (and where the hidden pieces are); an item's detail nobody
/// has looked at closely; the recipes; and the cipher keys, except where the group has found them.
/// </summary>
public static class EscapeProjector
{
    /// <param name="art">The room's generated pictures by <see cref="EscapeArt"/> key, if any have been made.</param>
    public static EscapeStageView Stage(EscapeState s, EscapeRoom template, DateTimeOffset now, IReadOnlyDictionary<string, string>? art = null)
    {
        var room = EscapeEngine.RoomFor(s, template);
        var playing = s.Phase != EscapePhase.Lobby;
        var stage = playing && s.Phase == EscapePhase.Playing ? room.Stages[s.StageIndex] : null;
        var puzzles = stage is null ? [] : stage.Puzzles.Select(id => Puzzle(s, room, room.FindPuzzle(id)!, now)).ToList();
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
            s.Inventory.Select(id => room.FindItem(id)!).Select(i => Item(s, i)).ToList(),
            s.Players.Select(p => new EscapePlayerSummary(p.SeatId, p.Name, p.IsHost, p.PhotoUrl)).ToList(),
            s.StartedAt, s.Deadline, s.EndedAt, now,
            s.Feed,
            s.Solved.Count, room.Puzzles.Count, s.HintsUsed, s.WrongAttempts,
            endText,
            s.Daily,
            // The puzzle set number only once it's over, so a group can replay it or challenge friends.
            PuzzleSet: endText is null ? null : s.Seed,
            GameMaster: s.Ai is { GameMaster: false, Hints: false } ? null
                : new EscapeGameMasterView(room.Host.Name, room.Host.Voice, s.Ai.GameMaster, s.Ai.Hints, s.Ai.Voice),
            // Only the lines themselves: what the moment was, and who it was about, stays in the state.
            Narration: s.Cues.Where(c => c.Text is not null).TakeLast(3).Select(c => new EscapeNarrationView(c.Id, c.Text!, c.AudioUrl, c.At)).ToList(),
            Soundscape: stage?.Soundscape ?? room.Soundscape,
            // The stage's own picture while playing, falling back to the cover; the cover before and after.
            ArtUrl: (stage is null ? null : art?.GetValueOrDefault(EscapeArt.Stage(stage.Id))) ?? art?.GetValueOrDefault(EscapeArt.Cover),
            Difficulty: s.Level,
            Scene: stage?.Scene is { } scene ? Scene(s, scene) : null,
            Notebook: s.Notebook.Select(n => new EscapeNoteView(n.Source, n.Text, n.At)).ToList());
    }

    private static EscapeSceneView Scene(EscapeState s, EscapeScene scene) => new(
        scene.Width, scene.Height, scene.Backdrop,
        scene.Objects.Select(o =>
        {
            var examined = s.Examined.Contains(o.Id);
            return new EscapeSpotView(o.Id, o.Prop, o.X, o.Y, o.W, o.H, o.Label, examined, examined ? o.Look : null);
        }).ToList());

    private static EscapeItemView Item(EscapeState s, EscapeItem i)
    {
        var inspected = s.Inspected.Contains(i.Id);
        return new EscapeItemView(i.Id, i.Name, i.Description, Inspectable: i.Inspect is not null && !inspected, InspectText: inspected ? i.Inspect : null);
    }

    public static EscapePlayerView Player(EscapeState s, EscapeRoom template, Guid seatId, DateTimeOffset now, IReadOnlyDictionary<string, string>? art = null)
    {
        var room = EscapeEngine.RoomFor(s, template);
        var me = s.FindPlayer(seatId) ?? throw new ButlerDidIt.Game.Engine.GameRuleException("You're not in this game.");
        var stage = Stage(s, template, now, art);
        // Only pieces for the stage in front of them: earlier ones are done, later ones would give the room away.
        var open = stage.Puzzles.Where(p => !p.Solved).Select(p => p.Id).ToHashSet();
        var pieces = s.Pieces
            .Where(p => p.SeatId == seatId && open.Contains(p.PuzzleId))
            .Select(p => (Piece: p, Puzzle: room.FindPuzzle(p.PuzzleId)!))
            .Select(x => new EscapePieceView(x.Puzzle.Id, x.Puzzle.Title, x.Puzzle.Pieces[x.Piece.Index], x.Piece.SpotId is { } spot ? room.FindObject(spot)?.Label : null))
            .ToList();
        return new EscapePlayerView(s.Version, stage, me.SeatId, me.Name, me.IsHost, pieces);
    }

    private static EscapePuzzleView Puzzle(EscapeState s, EscapeRoom room, EscapePuzzle p, DateTimeOffset now)
    {
        var solved = s.Solved.FirstOrDefault(x => x.PuzzleId == p.Id);
        var shown = s.HintsShown.GetValueOrDefault(p.Id);
        // Each paid step shows the AI's hint if it wrote one, otherwise the room's written hint.
        // A step the AI is still writing is left out until it arrives (or times out).
        var ai = s.AiHints.Where(h => h.PuzzleId == p.Id).ToDictionary(h => h.Index);
        var hints = Enumerable.Range(0, shown)
            .Where(i => !(ai.TryGetValue(i, out var h) && h.IsPending(now)))
            .Select(i => ai.TryGetValue(i, out var h) && h.Text is { } text ? text : p.Hints[i])
            .ToList();
        return new EscapePuzzleView(
            p.Id, p.Title, p.Kind, p.Prompt,
            Solved: solved is not null,
            SolvedBy: solved?.SolvedBy,
            SolvedText: solved is null ? null : p.SolvedText,
            Needs: solved is null ? p.Requires.Where(i => !s.Inventory.Contains(i)).Select(i => room.FindItem(i)!.Name).ToList() : [],
            Hints: hints,
            HintsLeft: p.Hints.Count - shown,
            HintPending: hints.Count < shown,
            LockedUntil: s.LockedUntil.TryGetValue(p.Id, out var until) ? until : null,
            PieceCount: s.Pieces.Where(x => x.PuzzleId == p.Id && !x.IsHidden).Select(x => x.SeatId).Distinct().Count(),
            PiecesHidden: s.Pieces.Count(x => x.PuzzleId == p.Id && x.IsHidden),
            Finds: p.Kind == PuzzleKind.Search ? new EscapeFindsView(p.Finds.Count(s.Examined.Contains), p.Finds.Count) : null,
            Switches: p.Grid is { } grid ? new EscapeSwitchesView(grid.Size, EscapeEngine.LitNow(s, p)) : null,
            Cipher: p.Decoder is { } d ? Cipher(s, p, d) : null,
            Deduction: p.Lineup.Count > 0 ? new EscapeDeductionView(p.Lineup, p.Lineup.Count) : null);
    }

    private static EscapeCipherView Cipher(EscapeState s, EscapePuzzle p, CipherDecoder d)
    {
        var unlocked = EscapeEngine.KeyFound(s, p);
        var table = unlocked && d.Type is CipherType.Symbols or CipherType.Morse
            ? PuzzleGenerators.KeyTable(d.Type, d.Key).Select(e => new EscapeKeyEntry(e.Code, e.Letter)).ToList()
            : null;
        return new EscapeCipherView(d.Type, unlocked, table);
    }
}
