using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// Cuts a room down to one game. A puzzle with <see cref="EscapePuzzle.MinMinutes"/> above the
/// game's length, or <see cref="EscapePuzzle.MinDifficulty"/> above its difficulty, is left out (with
/// it, any stage left empty), and the clock is set to the length. <see cref="EscapeRoomValidator"/>
/// plays every length and difficulty through, so a cut can never leave a puzzle waiting for a key
/// that only a left-out puzzle gave.
/// </summary>
public static class RoomLengths
{
    // Deterministic, so caching is safe: one cut room per (built room, length, difficulty).
    private static readonly ConditionalWeakTable<EscapeRoom, ConcurrentDictionary<(int, EscapeDifficulty), EscapeRoom>> Cache = [];

    /// <param name="minutes">The game's length; null is the room's own time limit (a puzzle kept for longer games is still cut).</param>
    public static EscapeRoom Cut(EscapeRoom room, int? minutes, EscapeDifficulty difficulty = EscapeDifficulty.Normal)
    {
        var length = minutes ?? room.TimeLimitMinutes;
        if (length == room.TimeLimitMinutes && room.Puzzles.All(p => Plays(p, length, difficulty))) return room; // nothing to cut
        return Cache.GetOrCreateValue(room).GetOrAdd((length, difficulty), key =>
        {
            var kept = room.Puzzles.Where(p => Plays(p, key.Item1, key.Item2)).ToList();
            var ids = kept.Select(p => p.Id).ToHashSet();
            return room with
            {
                TimeLimitMinutes = key.Item1,
                Puzzles = kept,
                Stages = room.Stages.Select(s => s with { Puzzles = s.Puzzles.Where(ids.Contains).ToList() }).Where(s => s.Puzzles.Count > 0).ToList(),
            };
        });
    }

    public static bool Plays(EscapePuzzle puzzle, int minutes, EscapeDifficulty difficulty = EscapeDifficulty.Normal) =>
        (puzzle.MinMinutes is not { } min || min <= minutes) && (puzzle.MinDifficulty is not { } level || level <= difficulty);
}
