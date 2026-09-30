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
        if (stage.Scene?.Objects.FirstOrDefault(o => !s.Examined.Contains(o.Id) && (o.Requires is null || s.Inventory.Contains(o.Requires))) is { } spot)
            return new ExamineSpot(now, seat, spot.Id);
        if (s.Inventory.Select(room.FindItem).FirstOrDefault(i => i!.Inspect is not null && !s.Inspected.Contains(i.Id)
                && (i.InspectRequires is null || s.Inventory.Contains(i.InspectRequires))) is { } item)
            return new InspectItem(now, seat, item.Id);
        if (room.Recipes.FirstOrDefault(r => r.Items.All(s.Inventory.Contains)) is { } recipe)
            return new CombineItems(now, seat, recipe.Items[1], recipe.Items[0]); // either order works
        var puzzle = stage.Puzzles.Select(id => room.FindPuzzle(id)!)
            .First(p => !s.IsSolved(p.Id) && p.Kind != PuzzleKind.Search && p.Requires.All(s.Inventory.Contains));
        return puzzle.Kind switch
        {
            PuzzleKind.Use => new UseItems(now, seat, puzzle.Id),
            PuzzleKind.Switches => new PressSwitch(now, seat, puzzle.Id, SolveLights(puzzle.Grid!.Size, EscapeEngine.LitNow(s, puzzle))[0]),
            _ => new SubmitAnswer(now, seat, puzzle.Id, puzzle.Answers[0]),
        };
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
