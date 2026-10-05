using ButlerDidIt.Escape.Rooms;
using Soundscape = ButlerDidIt.Game.Scenarios.Soundscape;

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
            Notebook: s.Notebook.Select(n => new EscapeNoteView(n.Source, n.Text, n.At)).ToList(),
            IntroVideoUrl: art?.GetValueOrDefault(EscapeArt.IntroVideo),
            // Like the pictures: only the stage in front of the group, so a later stage's video can't give it away.
            StageVideoUrl: stage is null ? null : art?.GetValueOrDefault(EscapeArt.StageVideo(stage.Id)),
            AmbienceUrl: (stage is null ? null : art?.GetValueOrDefault(EscapeArt.StageAmbience(stage.Id))) ?? art?.GetValueOrDefault(EscapeArt.Ambience),
            IntroVoiceUrl: art?.GetValueOrDefault(EscapeArt.IntroVoice),
            // Like the stage's picture and video: a later stage's description stays unheard until it opens.
            StageVoiceUrl: stage is null ? null : art?.GetValueOrDefault(EscapeArt.StageVoice(stage.Id)));
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
            Cipher: p.Decoder is { } d ? Cipher(s, room, p, d) : null,
            Deduction: p.Lineup.Count > 0 ? new EscapeDeductionView(p.Lineup, p.Lineup.Count) : null);
    }

    private static EscapeCipherView Cipher(EscapeState s, EscapeRoom room, EscapePuzzle p, CipherDecoder d)
    {
        var unlocked = EscapeEngine.KeyFound(s, p);
        // In the room's order, not with the real one first: the list mustn't say which key is real.
        var found = unlocked
            ? (d.Candidates ?? [new KeyCandidate(p.KeyAt.FirstOrDefault() ?? "", d.Key, true, "", false)])
                .Where(c => EscapeEngine.PlaceSeen(s, room, c.Place))
                .Select(c => new EscapeFoundKey(
                    PlaceLabel(room, c.Place),
                    d.Type == CipherType.Shift && int.TryParse(c.Key, out var n) ? n : null,
                    d.Type is CipherType.Symbols or CipherType.Morse ? PuzzleGenerators.KeyTable(d.Type, c.Key).Select(e => new EscapeKeyEntry(e.Code, e.Letter)).ToList() : null))
                .ToList()
            : [];
        return new EscapeCipherView(d.Type, unlocked, found);
    }

    /// <summary>
    /// The recap of a finished game (#111). It's made only from what the group saw or did: the stages they
    /// reached and the titles of the puzzles in them (the TV listed those), who opened what and when.
    /// Answers, prompts, hints, solved texts and clue pieces are never copied, so a shared recap doesn't
    /// spoil the room for the next group.
    /// </summary>
    public static EscapeRecapView Recap(EscapeState s, EscapeRoom template, IReadOnlyDictionary<string, string>? art = null)
    {
        if (s.Phase is not (EscapePhase.Escaped or EscapePhase.Failed) || s.StartedAt is not { } start || s.EndedAt is not { } end)
            throw new ButlerDidIt.Game.Engine.GameRuleException("The recap appears once the game is over.");
        var room = EscapeEngine.RoomFor(s, template);
        int At(DateTimeOffset t) => (int)Math.Round((t - start).TotalSeconds);
        var solved = s.Solved.ToDictionary(x => x.PuzzleId);

        // Stage by stage, up to the one the group was in at the end (the last one, if they escaped).
        var stages = new List<EscapeRecapStage>();
        var openedAt = 0;
        for (var i = 0; i <= s.StageIndex && i < room.Stages.Count; i++)
        {
            var stage = room.Stages[i];
            var puzzles = stage.Puzzles.Select(id => room.FindPuzzle(id)!).Select(p =>
            {
                var hints = s.HintsShown.GetValueOrDefault(p.Id);
                return solved.TryGetValue(p.Id, out var x)
                    ? new EscapeRecapPuzzle(p.Title, p.Kind, x.SolvedBy, At(x.At), hints)
                    : new EscapeRecapPuzzle(p.Title, p.Kind, null, null, hints);
            }).ToList();
            // Cleared when its last puzzle opened; the next stage opened at that moment.
            int? clearedAt = puzzles.All(p => p.SolvedAt is not null) ? puzzles.Select(p => p.SolvedAt!.Value).DefaultIfEmpty(openedAt).Max() : null;
            stages.Add(new EscapeRecapStage(i + 1, stage.Title, openedAt, clearedAt, puzzles.Sum(p => p.Hints), puzzles));
            if (clearedAt is { } cleared) openedAt = cleared;
        }

        var escaped = s.Phase == EscapePhase.Escaped;
        var elapsed = EscapeEngine.ElapsedSeconds(s);
        var left = s.Deadline is { } deadline && escaped ? Math.Max(0, (int)Math.Round((deadline - end).TotalSeconds)) : 0;
        var team = s.Players.Select(p => new EscapeRecapPlayer(p.Name, p.PhotoUrl, s.Solved.Count(x => x.SolvedBy == p.Name))).ToList();
        var lines = s.Cues.Where(c => c.Text is not null).OrderBy(c => c.At).Select(c => c.Text!).ToList();

        return new EscapeRecapView(
            room.Id, room.Title, room.Synopsis, room.Theme, room.ContentRating,
            CoverUrl: art?.GetValueOrDefault(EscapeArt.Cover),
            Escaped: escaped,
            EndText: escaped ? room.EscapedText : room.FailedText,
            StartedAt: start,
            ElapsedSeconds: elapsed,
            SecondsLeft: left,
            TimeLimitMinutes: room.TimeLimitMinutes,
            Difficulty: s.Level,
            HintsUsed: s.HintsUsed,
            HintPenaltySeconds: room.HintPenaltySeconds,
            WrongAttempts: s.WrongAttempts,
            Score: EscapeEngine.Score(s, template),
            SolvedCount: s.Solved.Count,
            PuzzleCount: room.Puzzles.Count,
            StageCount: room.Stages.Count,
            Daily: s.Daily,
            PuzzleSet: s.Seed,
            Team: team,
            Stages: stages,
            Highlights: Highlights(s, escaped, left, team, stages),
            GameMasterName: lines.Count > 0 ? room.Host.Name : null,
            GameMasterLines: lines);
    }

    /// <summary>A few moments worth a cheer, worked out from the timeline. Only things the group saw happen.</summary>
    private static List<EscapeRecapHighlight> Highlights(EscapeState s, bool escaped, int secondsLeft, List<EscapeRecapPlayer> team, List<EscapeRecapStage> stages)
    {
        var list = new List<EscapeRecapHighlight>();

        var most = team.Select(p => p.Solved).DefaultIfEmpty(0).Max();
        if (most > 0)
        {
            var names = team.Where(p => p.Solved == most).Select(p => p.Name).ToList();
            var detail = names.Count == 1 ? $"{names[0]} ({most})" : $"{JoinNames(names)} ({most} each)";
            list.Add(new("🧠", "Most puzzles opened", detail));
        }

        var first = stages.SelectMany(st => st.Puzzles).Where(p => p.SolvedAt is not null).OrderBy(p => p.SolvedAt).FirstOrDefault();
        if (first is not null) list.Add(new("🔓", "First breakthrough", $"{first.SolvedBy} opened {first.Title} at {Clock(first.SolvedAt!.Value)}"));

        var cleared = stages.Where(st => st.ClearedAt is not null).ToList();
        if (cleared.Count >= 2)
        {
            var fastest = cleared.MinBy(st => st.ClearedAt!.Value - st.OpenedAt)!;
            list.Add(new("⚡", "Fastest stage", $"{fastest.Title} in {Clock(fastest.ClearedAt!.Value - fastest.OpenedAt)}"));
        }

        if (escaped && s.HintsUsed == 0) list.Add(new("🦉", "No hints needed", "Escaped without a single hint."));
        else if (cleared.Where(st => st.Hints == 0).Select(st => st.Title).ToList() is { Count: > 0 } clean)
            list.Add(new("🦉", "No hints needed", JoinNames(clean)));

        if (escaped && secondsLeft < 60) list.Add(new("⏱️", "Photo finish", $"Out with {secondsLeft} {(secondsLeft == 1 ? "second" : "seconds")} to spare."));
        return list;
    }

    private static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    private static string JoinNames(List<string> names) => names.Count switch
    {
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    /// <summary>What the phones call a place a key was found: the spot, item or puzzle's own public name.</summary>
    private static string PlaceLabel(EscapeRoom room, string place)
    {
        var split = place.IndexOf(':');
        if (split < 0) return "";
        var id = place[(split + 1)..];
        return place[..split] switch
        {
            "object" => room.SceneObjects.FirstOrDefault(o => o.Id == id)?.Label ?? "",
            "item" or "inspect" => room.FindItem(id)?.Name ?? "",
            _ => room.FindPuzzle(id)?.Title ?? "",
        };
    }
}
