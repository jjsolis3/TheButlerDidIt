using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Testing;

/// <summary>
/// Plays an escape room the way a thorough group would, one move at a time: search every spot it can,
/// look closely at everything, put together what fits, then solve the next open puzzle with its answer.
/// Shared by the engine, AI and API tests (linked into each project), so every room, whatever its
/// puzzles, is played through the same way everywhere.
/// </summary>
public static class EscapeBot
{
    /// <summary>The next move for <paramref name="seat"/>, from the room as this attempt plays it (<see cref="EscapeEngine.RoomFor"/>).</summary>
    public static EscapeCommand NextMove(EscapeState s, EscapeRoom room, Guid seat, DateTimeOffset now)
    {
        var stage = room.Stages[s.StageIndex];
        // Only what the group can see (#134): searching and solving brings the rest into sight.
        var open = EscapeEngine.InSight(s, room).Where(p => !s.IsSolved(p.Id)).ToList();
        // While puzzles go to people (#132), a spot with a key written on it is only readable by that puzzle's holder:
        // the bot searches it as them once someone has taken the puzzle, and leaves it until then.
        Guid? Reader(SceneObject o) => open.FirstOrDefault(p => p.KeyAt.Contains($"object:{o.Id}")) is not { } keyed || !s.TakesTurns()
            ? seat
            : s.Holds.TryGetValue(keyed.Id, out var h) ? h.SeatId : null;
        if (stage.Scene?.Objects.FirstOrDefault(o => !s.Examined.Contains(o.Id) && (o.Requires is null || s.Inventory.Contains(o.Requires)) && Reader(o) is not null) is { } spot)
            return new ExamineSpot(now, Reader(spot)!.Value, spot.Id);
        if (s.Inventory.Select(room.FindItem).FirstOrDefault(i => i!.Inspect is not null && !s.Inspected.Contains(i.Id)
                && (i.InspectRequires is null || s.Inventory.Contains(i.InspectRequires))) is { } item)
            return new InspectItem(now, seat, item.Id);
        if (room.Recipes.FirstOrDefault(r => r.Items.All(s.Inventory.Contains)) is { } recipe)
            return new CombineItems(now, seat, recipe.Items[1], recipe.Items[0]); // either order works
        var ready = open.Where(p => p.Kind != PuzzleKind.Search && p.Requires.All(s.Inventory.Contains)).ToList();
        var puzzle = s.TakesTurns()
            // A puzzle someone holds first, then one nobody does. A free puzzle may be the only way to a key spot.
            ? ready.FirstOrDefault(p => s.Holds.ContainsKey(p.Id)) ?? ready.FirstOrDefault()
              ?? open.First(p => EscapeEngine.Holdable(p) && !s.Holds.ContainsKey(p.Id))
            : ready.First();
        if (s.TakesTurns() && EscapeEngine.Holdable(puzzle))
        {
            if (!s.Holds.TryGetValue(puzzle.Id, out var hold)) return Take(s, puzzle, seat, now);
            seat = hold.SeatId; // the group plays: whoever holds it answers it
        }
        return puzzle.Kind switch
        {
            PuzzleKind.Use => new UseItems(now, seat, puzzle.Id),
            PuzzleKind.Switches => new PressSwitch(now, seat, puzzle.Id, SolveLights(puzzle.Grid!.Size, EscapeEngine.LitNow(s, puzzle))[0]),
            _ => new SubmitAnswer(now, seat, puzzle.Id, puzzle.Answers[0]),
        };
    }

    /// <summary>
    /// Someone takes <paramref name="puzzle"/>: this seat if it may, else anyone not already working on one (Take it),
    /// else a holder hands back a puzzle that's waiting for items, to take this one instead.
    /// </summary>
    private static EscapeCommand Take(EscapeState s, EscapePuzzle puzzle, Guid seat, DateTimeOffset now)
    {
        bool Busy(Guid who) => s.Answering == AnswerRule.TakeIt && s.Holds.Any(h => h.Value.SeatId == who && !s.IsSolved(h.Key));
        if (!Busy(seat)) return new TakePuzzle(now, seat, puzzle.Id);
        if (s.Players.FirstOrDefault(p => !Busy(p.SeatId)) is { } free) return new TakePuzzle(now, free.SeatId, puzzle.Id);
        // NextMove answers a held puzzle that's ready before taking another, so every puzzle held now is waiting.
        var waiting = s.Holds.First(h => h.Key != puzzle.Id);
        return new ReleasePuzzle(now, waiting.Value.SeatId, waiting.Key);
    }

    /// <summary>Plays to the end through the engine, taking turns on <paramref name="seats"/>. <paramref name="check"/> runs before every move and at the end.</summary>
    public static EscapeState PlayToEnd(EscapeState s, EscapeRoom template, IReadOnlyList<Guid> seats, DateTimeOffset start, Action<EscapeState>? check = null) =>
        PlayToEnd(s, template, seats, start, check, out _);

    public static EscapeState PlayToEnd(EscapeState s, EscapeRoom template, IReadOnlyList<Guid> seats, DateTimeOffset start, Action<EscapeState>? check, out int moves)
    {
        var t = start;
        for (moves = 0; s.Phase == EscapePhase.Playing; moves++)
        {
            if (moves >= 1000) throw new InvalidOperationException("The game should end.");
            check?.Invoke(s);
            t = t.AddSeconds(5);
            s = EscapeEngine.Apply(s, template, NextMove(s, EscapeEngine.RoomFor(s, template), seats[moves % seats.Count], t));
        }
        check?.Invoke(s);
        return s;
    }

    /// <summary>The fewest presses that turn every light on, by trying every set of presses.</summary>
    public static List<int> SolveLights(int size, IEnumerable<int> lit)
    {
        var cells = size * size;
        var start = lit.Aggregate(0, (m, c) => m | 1 << c);
        var masks = Enumerable.Range(0, cells).Select(c => PuzzleGenerators.Press(size, [], c).Aggregate(0, (m, x) => m | 1 << x)).ToArray();
        var all = (1 << cells) - 1;
        List<int>? best = null;
        for (var set = 0; set <= all; set++)
        {
            var count = System.Numerics.BitOperations.PopCount((uint)set);
            if (best is not null && count >= best.Count) continue;
            var state = start;
            for (var c = 0; c < cells; c++) if ((set >> c & 1) == 1) state ^= masks[c];
            if (state == all) best = Enumerable.Range(0, cells).Where(c => (set >> c & 1) == 1).ToList();
        }
        return best ?? throw new InvalidOperationException("unsolvable");
    }
}
