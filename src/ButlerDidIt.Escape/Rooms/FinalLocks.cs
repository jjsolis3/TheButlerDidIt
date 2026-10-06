using System.Text.RegularExpressions;

namespace ButlerDidIt.Escape.Rooms;

/// <summary>
/// A stage's final lock (#134): it comes into sight once every other puzzle in its stage is solved, and its code is
/// built from them. Each one leaves a mark and a digit when it opens ("⚓ = 7"), and the room writes the order to read
/// them in ({order:&lt;final id&gt;}) somewhere the group has to find it.
///
/// It's made in two steps. <see cref="RoomVariants"/> picks every mark, digit and the order from the seed, for all the
/// stage's other puzzles. Which of those a game plays depends on its length and difficulty, which <see cref="RoomLengths.Cut"/>
/// decides afterwards, so the cut calls <see cref="Assemble"/> to finish the lock from the puzzles it kept: the code,
/// the order written in the room, and the {count} and {answer} in the lock's own text.
/// </summary>
public static partial class FinalLocks
{
    /// <summary>The marks a final lock uses when the room doesn't name its own.</summary>
    public static readonly IReadOnlyList<string> DefaultMarks = ["⭐", "🌙", "☀️", "❤️", "🍀", "⚡", "🔔", "💎"];

    /// <summary>A final lock needs at least this many other puzzles in its stage, so its code can't simply be guessed.</summary>
    public const int MinParts = 3;

    [GeneratedRegex(@"\{order:([^}\s]+)\}")]
    public static partial Regex OrderPlaceholder();

    /// <summary>The marks a Final generator chooses from.</summary>
    public static IReadOnlyList<string> MarksOf(PuzzleGenerator g) => g.Marks.Count > 0 ? g.Marks : DefaultMarks;

    /// <summary>The order as the room writes it: "🐚, then ⚓, then 🦜".</summary>
    public static string OrderText(IEnumerable<FinalPart> parts) => string.Join(", then ", parts.Select(p => p.Mark));

    /// <summary>The code: each part's digit, in order.</summary>
    public static string Code(IEnumerable<FinalPart> parts) => string.Concat(parts.Select(p => p.Digit));

    /// <summary>What <paramref name="puzzleId"/> leaves for its stage's final lock when it's solved, if the stage has one.</summary>
    public static FinalPart? PartFor(EscapeRoom room, string puzzleId) =>
        room.StageOf(puzzleId) is { } stage
            ? stage.Puzzles.Select(room.FindPuzzle).FirstOrDefault(p => p?.Final is not null)?.Final!.Parts.FirstOrDefault(x => x.Puzzle == puzzleId)
            : null;

    /// <summary>
    /// The first step, from <see cref="RoomVariants"/>: a mark and a digit for every other puzzle in each final lock's
    /// stage, and the order the code reads them in, all from the seed. The lock's text keeps its placeholders for <see cref="Assemble"/>.
    /// </summary>
    internal static EscapeRoom Make(EscapeRoom room, long seed, Dictionary<string, List<string>> orderPlaces)
    {
        if (!room.Puzzles.Any(p => p.Generator?.Type == GeneratorType.Final)) return room;
        return room with
        {
            Puzzles = room.Puzzles.Select(p =>
            {
                if (p.Generator is not { Type: GeneratorType.Final } g) return p;
                var others = room.StageOf(p.Id)?.Puzzles.Where(id => id != p.Id).ToList() ?? [];
                var rng = new SeededRandom(seed ^ SeededRandom.StableHash(p.Id + "#final"));
                // The validator makes sure there are enough marks; a room short of them reuses the last rather than failing to load.
                var pool = MarksOf(g);
                var marks = rng.Pick(pool, others.Count);
                var parts = others.Select((id, i) => new FinalPart(id, marks.ElementAtOrDefault(i) ?? pool[^1], rng.Next(10))).ToList();
                return p with { Generator = null, Final = new FinalLock(rng.Pick(parts, parts.Count), orderPlaces.GetValueOrDefault(p.Id) ?? []) };
            }).ToList(),
        };
    }

    /// <summary>
    /// The second step, from <see cref="RoomLengths.Cut"/>: each final lock keeps the parts of the puzzles this game plays,
    /// its answer is their digits in order, and the room's {order:&lt;id&gt;} placeholders become their marks in that order.
    /// Running it again changes nothing.
    /// </summary>
    public static EscapeRoom Assemble(EscapeRoom room)
    {
        if (!room.Puzzles.Any(p => p.Final is not null)) return room;
        var kept = room.Puzzles.Select(p => p.Id).ToHashSet();
        var parts = room.Puzzles.Where(p => p.Final is not null)
            .ToDictionary(p => p.Id, p => (IReadOnlyList<FinalPart>)p.Final!.Parts.Where(x => kept.Contains(x.Puzzle)).ToList());

        string Order(string text) => OrderPlaceholder().Replace(text, m => parts.TryGetValue(m.Groups[1].Value, out var list) ? OrderText(list) : m.Value);
        string? OrderN(string? text) => text is null ? null : Order(text);

        return room with
        {
            Puzzles = room.Puzzles.Select(p =>
            {
                p = p with { Prompt = Order(p.Prompt), Pieces = p.Pieces.Select(Order).ToList(), Hints = p.Hints.Select(Order).ToList(), SolvedText = Order(p.SolvedText) };
                if (p.Final is null) return p;
                var mine = parts[p.Id];
                var code = Code(mine);
                string Own(string text) => text.Replace("{count}", RoomVariants.CountWord(mine.Count)).Replace("{answer}", code);
                return p with
                {
                    Final = p.Final with { Parts = mine },
                    Answers = [code],
                    Prompt = Own(p.Prompt),
                    Hints = p.Hints.Select(Own).ToList(),
                    SolvedText = Own(p.SolvedText),
                };
            }).ToList(),
            Stages = room.Stages.Select(s => s.Scene is null ? s : s with
            {
                Scene = s.Scene with
                {
                    Objects = s.Scene.Objects.Select(o => o with { Look = Order(o.Look), Clue = OrderN(o.Clue), LockedText = OrderN(o.LockedText) }).ToList(),
                },
            }).ToList(),
            Items = room.Items.Select(i => i with { Description = Order(i.Description), Inspect = OrderN(i.Inspect) }).ToList(),
        };
    }
}
