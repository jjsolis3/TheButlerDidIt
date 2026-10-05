using ButlerDidIt.Ai.Media;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Ai.Tests;

/// <summary>
/// The pictures an escape room asks for (a cover and one per stage) and the game master's readings (the intro and each
/// stage), made only from what the TV already shows.
/// </summary>
public class EscapeMediaPlanTests
{
    private static readonly Lazy<List<EscapeRoom>> Library = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "content"))) dir = dir.Parent;
        return EscapeLibrary.Load(Path.Combine(dir!.FullName, "content", "escape"));
    });

    public static TheoryData<string> RoomIds() => new(Library.Value.Select(r => r.Id));

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void A_room_gets_a_cover_and_a_picture_per_stage(string roomId)
    {
        var room = Library.Value.Single(r => r.Id == roomId);
        var plan = EscapeMediaPlan.For(room, voices: false);

        Assert.Equal([EscapeArt.Cover, .. room.Stages.Select(s => EscapeArt.Stage(s.Id))], plan.Select(i => i.Key));
        Assert.All(plan, i => Assert.Equal(AiRole.Illustrator, i.Role));
        Assert.All(plan, i => Assert.Contains(room.ArtStyle, i.Text));
        // The same room always asks for exactly the same pictures, so the media cache pays for each only once.
        Assert.Equal(plan.Select(i => i.Text), EscapeMediaPlan.For(room, voices: false).Select(i => i.Text));
    }

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void The_game_master_reads_the_intro_and_each_stage_in_one_voice(string roomId)
    {
        var room = Library.Value.Single(r => r.Id == roomId);
        var plan = EscapeMediaPlan.For(room, voices: true, images: false);

        Assert.Equal([EscapeArt.IntroVoice, .. room.Stages.Select(s => EscapeArt.StageVoice(s.Id))], plan.Select(i => i.Key));
        Assert.All(plan, i => Assert.Equal(AiRole.Voice, i.Role));
        // Word for word what the TV prints, so the reading and the subtitles match.
        Assert.Equal([room.Intro, .. room.Stages.Select(s => s.Description)], plan.Select(i => i.Text));
        // The voice the game master speaks its live lines in, every time.
        Assert.Single(plan.Select(i => i.Voice).Distinct());
        Assert.Equal(VoiceCasting.For($"game-master:{room.Id}", room.Host.Voice), plan[0].Voice);

        Assert.Empty(EscapeMediaPlan.For(room, voices: false, images: false)); // neither set up: nothing to make
        Assert.Equal(plan.Count + room.Stages.Count + 1, EscapeMediaPlan.For(room).Count); // both: readings and pictures
    }

    [Theory]
    [MemberData(nameof(RoomIds))]
    public void No_picture_prompt_or_reading_holds_a_puzzle_clue_or_answer(string roomId)
    {
        var template = Library.Value.Single(r => r.Id == roomId);
        var prompts = string.Join("\n", EscapeMediaPlan.For(template).Select(i => i.Text));
        // What the TV shows anyway. A room may hide an answer in plain sight (the Workshop's clock on the wall),
        // so a prompt may only mention an answer if this public text already does.
        var shown = string.Join("\n", template.Stages.SelectMany(s => new[] { s.Title, s.Description }).Prepend(template.Intro).Prepend(template.Synopsis).Prepend(template.Title));
        // Every puzzle set the room can be played with: a picture is made once per room, whatever the codes are that night.
        for (var seed = 0L; seed < 50; seed++)
        {
            foreach (var puzzle in RoomVariants.Build(template, seed).Puzzles)
            {
                Assert.False(EscapeHintGuard.Leaks(prompts, puzzle) && !EscapeHintGuard.Leaks(shown, puzzle),
                    $"{roomId}, puzzle set {seed}: a picture prompt or a reading gives away '{puzzle.Id}'.");
                Assert.DoesNotContain(puzzle.Prompt, prompts);
                Assert.All(puzzle.Pieces.Concat(puzzle.Hints), t => Assert.DoesNotContain(t, prompts));
            }
        }
    }
}
