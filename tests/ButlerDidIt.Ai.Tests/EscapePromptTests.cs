using System.Text.RegularExpressions;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using ButlerDidIt.Game;

namespace ButlerDidIt.Ai.Tests;

/// <summary>
/// The game master's prompts hold only what the group can already see. Every room is played through
/// over many puzzle sets, and at every step each prompt is checked for answers the group hasn't got.
/// </summary>
public class EscapePromptTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ada = Guid.NewGuid(), Ben = Guid.NewGuid(), Cy = Guid.NewGuid();

    private static readonly Lazy<List<EscapeRoom>> Library = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content"))) dir = dir.Parent;
        return EscapeLibrary.Load(Path.Combine(dir!.FullName, "content", "escape"));
    });

    public static TheoryData<string> RoomIds() => new(Library.Value.Select(r => r.Id));

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void No_prompt_ever_holds_an_answer_the_group_cannot_already_see(string roomId)
    {
        var template = Library.Value.Single(r => r.Id == roomId);
        for (var seed = 0L; seed < 200; seed++)
        {
            var s = EscapeEngine.NewGame(seed, ai: new EscapeAiFeatures { GameMaster = true, Hints = true });
            foreach (var (seat, name) in new[] { (Ada, "Ada"), (Ben, "Ben"), (Cy, "Cy") })
                s = EscapeEngine.Apply(s, template, new AddEscapePlayer(T0, seat, name, seat == Ada, false));
            s = EscapeEngine.Apply(s, template, new StartEscape(T0));

            for (var step = 1; s.Phase == EscapePhase.Playing; step++)
            {
                var now = T0.AddMinutes(step);
                var room = EscapeEngine.RoomFor(s, template);
                var open = room.Stages[s.StageIndex].Puzzles.Select(id => room.FindPuzzle(id)!).Where(p => !s.IsSolved(p.Id)).ToList();
                var secret = Secrets(s, template, room, now);

                foreach (var puzzle in open.Where(p => p.Hints.Count > 0))
                    AssertClean(EscapePrompts.Hint(template, s, puzzle.Id, 0, now), secret, $"{roomId} seed {seed}: hint for {puzzle.Id}");
                AssertClean(EscapePrompts.Narration(template, s, s.Cues[^1], now), secret, $"{roomId} seed {seed}: narration after step {step}");

                s = EscapeEngine.Apply(s, template, EscapeBot.NextMove(s, room, Ben, now));
            }
            AssertClean(EscapePrompts.Narration(template, s, s.Cues[^1], T0.AddHours(1)), [], $"{roomId} seed {seed}: the ending");
        }
    }

    [Fact]
    public void Prompts_start_with_their_task_line_and_speak_as_the_rooms_game_master()
    {
        var room = Library.Value.Single(r => r.Id == "the-workshop");
        var s = EscapeEngine.NewGame(1, ai: new EscapeAiFeatures { GameMaster = true, Hints = true });
        s = EscapeEngine.Apply(s, room, new AddEscapePlayer(T0, Ada, "Ada", true, false));
        s = EscapeEngine.Apply(s, room, new StartEscape(T0));
        var puzzle = room.Stages[0].Puzzles[0];

        var narration = EscapePrompts.Narration(room, s, s.Cues[^1], T0);
        var hint = EscapePrompts.Hint(room, s, puzzle, 0, T0);
        Assert.StartsWith($"TASK: {EscapePrompts.NarrationTask}", narration);
        Assert.StartsWith($"TASK: {EscapePrompts.HintTask}", hint);
        Assert.Contains("The Tinkerer", narration);
        Assert.Contains("MOMENT: Start", narration);
        Assert.Contains(EscapeEngine.RoomFor(s, room).FindPuzzle(puzzle)!.Hints[0], hint);
    }

    /// <summary>The answers of every puzzle whose answer the group can't already read on the TV or their phones.</summary>
    private static List<string> Secrets(EscapeState s, EscapeRoom template, EscapeRoom room, DateTimeOffset now)
    {
        var visible = GameJson.Serialize(EscapeProjector.Stage(s, template, now)) + " " +
            string.Join(" ", s.Pieces.Select(p => room.FindPuzzle(p.PuzzleId)!.Pieces[p.Index]));
        return room.Puzzles.Where(p => !s.IsSolved(p.Id)).SelectMany(p => p.Answers).Where(a => !Mentions(visible, a)).ToList();
    }

    private static void AssertClean(string prompt, List<string> secrets, string where)
    {
        foreach (var answer in secrets) Assert.False(Mentions(prompt, answer), $"{where} contains the answer \"{answer}\".");
    }

    private static bool Mentions(string text, string answer) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(answer)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
}
