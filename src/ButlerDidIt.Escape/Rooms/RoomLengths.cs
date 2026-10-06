using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Cuts a room down to one game. A puzzle with <see cref="EscapePuzzle.MinMinutes"/> above the
/// game's length, or <see cref="EscapePuzzle.MinDifficulty"/> above its difficulty, is left out (with
/// it, any stage left empty), as is any scene spot above its <see cref="SceneObject.MinDifficulty"/>;
/// and the clock is set to the length. A puzzle that only a left-out spot or puzzle would have brought into
/// sight is in sight from the start, and each final lock is built from the puzzles kept (<see cref="FinalLocks.Assemble"/>).
/// <see cref="EscapeRoomValidator"/> plays every length and difficulty through, so a cut can never leave a puzzle
/// waiting for a key that only a left-out puzzle gave.
/// </summary>
public static class RoomLengths
{
    // Deterministic, so caching is safe: one cut room per (built room, length, difficulty).
    private static readonly ConditionalWeakTable<EscapeRoom, ConcurrentDictionary<(int, EscapeDifficulty), EscapeRoom>> Cache = [];

    /// <param name="minutes">The game's length; null is the room's own time limit (a puzzle kept for longer games is still cut).</param>
    public static EscapeRoom Cut(EscapeRoom room, int? minutes, EscapeDifficulty difficulty = EscapeDifficulty.Normal)
    {
        var length = minutes ?? room.TimeLimitMinutes;
        if (length == room.TimeLimitMinutes && room.Puzzles.All(p => Plays(p, length, difficulty) && p.Final is null) && room.SceneObjects.All(o => Shows(o, difficulty)))
            return room; // nothing to cut, and no final lock to finish
        return Cache.GetOrCreateValue(room).GetOrAdd((length, difficulty), key =>
        {
            var kept = room.Puzzles.Where(p => Plays(p, key.Item1, key.Item2)).ToList();
            var ids = kept.Select(p => p.Id).ToHashSet();
            var spots = room.SceneObjects.Where(o => Shows(o, key.Item2)).Select(o => o.Id).ToHashSet();
            return FinalLocks.Assemble(room with
            {
                TimeLimitMinutes = key.Item1,
                Puzzles = kept.Select(p => p.RevealedBy is { } by && LeftOut(by, ids, spots) ? p with { RevealedBy = null } : p).ToList(),
                Stages = room.Stages
                    .Select(s => s with
                    {
                        Puzzles = s.Puzzles.Where(ids.Contains).ToList(),
                        Scene = s.Scene is null ? null : s.Scene with { Objects = s.Scene.Objects.Where(o => spots.Contains(o.Id)).ToList() },
                    })
                    .Where(s => s.Puzzles.Count > 0).ToList(),
            });
        });
    }

    /// <summary>A "puzzle:" or "spot:" this game doesn't have. (An item can come from anywhere: the validator checks it's reachable.)</summary>
    private static bool LeftOut(string revealedBy, HashSet<string> puzzles, HashSet<string> spots) => Reveals.Parse(revealedBy) switch
    {
        (Reveals.Puzzle, var id) => !puzzles.Contains(id),
        (Reveals.Spot, var id) => !spots.Contains(id),
        _ => false,
    };

    public static bool Shows(SceneObject spot, EscapeDifficulty difficulty) => spot.MinDifficulty is not { } level || level <= difficulty;

    public static bool Plays(EscapePuzzle puzzle, int minutes, EscapeDifficulty difficulty = EscapeDifficulty.Normal) =>
        (puzzle.MinMinutes is not { } min || min <= minutes) && (puzzle.MinDifficulty is not { } level || level <= difficulty);
}

/// <summary>The three kinds of <see cref="EscapePuzzle.RevealedBy"/>: "spot:&lt;id&gt;", "puzzle:&lt;id&gt;" and "item:&lt;id&gt;".</summary>
public static class Reveals
{
    public const string Spot = "spot", Puzzle = "puzzle", Item = "item";

    /// <summary>The kind and the id. Anything else (no colon, an unknown kind) comes back with an empty kind, for the validator to report.</summary>
    public static (string Kind, string Id) Parse(string revealedBy)
    {
        var split = revealedBy.IndexOf(':');
        if (split < 0) return ("", revealedBy);
        var kind = revealedBy[..split];
        return (kind is Spot or Puzzle or Item ? kind : "", revealedBy[(split + 1)..]);
    }
}
