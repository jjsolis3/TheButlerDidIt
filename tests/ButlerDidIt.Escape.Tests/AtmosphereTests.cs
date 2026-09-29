using System.Reflection;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;

namespace ButlerDidIt.Escape.Tests;

/// <summary>What the TV needs for its atmosphere: the right background sound and picture for the stage in front of the group.</summary>
public class AtmosphereTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ada = Guid.NewGuid();
    private static EscapeRoom Workshop => Rooms.Get("the-workshop");

    private static EscapeState Started(EscapeRoom room)
    {
        var s = EscapeEngine.NewGame(7);
        s = EscapeEngine.Apply(s, room, new AddEscapePlayer(T0, Ada, "Ada", true, false));
        return EscapeEngine.Apply(s, room, new StartEscape(T0));
    }

    private static EscapeState ToStage(EscapeState s, EscapeRoom template, int stageIndex)
    {
        while (s.StageIndex < stageIndex && s.Phase == EscapePhase.Playing)
        {
            var room = EscapeEngine.RoomFor(s, template);
            var p = room.Stages[s.StageIndex].Puzzles.Select(id => room.FindPuzzle(id)!).First(p => !s.IsSolved(p.Id) && p.Requires.All(s.Inventory.Contains));
            s = EscapeEngine.Apply(s, template, p.Kind == PuzzleKind.Use ? new UseItems(T0, Ada, p.Id) : new SubmitAnswer(T0, Ada, p.Id, p.Answers[0]));
        }
        return s;
    }

    [Fact]
    public void A_stage_can_change_the_background_sound_and_the_room_sets_the_rest()
    {
        var room = Workshop;
        Assert.Equal(Soundscape.Workshop, EscapeProjector.Stage(EscapeEngine.NewGame(7), room, T0).Soundscape); // the lobby
        var s = Started(room);
        Assert.Equal(Soundscape.Workshop, EscapeProjector.Stage(s, room, T0).Soundscape);

        var door = room.Stages.FindIndex(st => st.Id == "door");
        Assert.Equal(Soundscape.Drone, room.Stages[door].Soundscape);
        Assert.Equal(Soundscape.Drone, EscapeProjector.Stage(ToStage(s, room, door), room, T0).Soundscape);
    }

    [Fact]
    public void The_tv_shows_the_current_stage_picture_or_else_the_cover()
    {
        var room = Workshop;
        var art = new Dictionary<string, string>
        {
            [EscapeArt.Cover] = "/media/cover",
            [EscapeArt.Stage(room.Stages[0].Id)] = "/media/stage-1",
        };
        Assert.Equal("/media/cover", EscapeProjector.Stage(EscapeEngine.NewGame(7), room, T0, art).ArtUrl);

        var s = Started(room);
        Assert.Equal("/media/stage-1", EscapeProjector.Stage(s, room, T0, art).ArtUrl);
        // The second stage has no picture yet: the cover stands in.
        Assert.Equal("/media/cover", EscapeProjector.Stage(ToStage(s, room, 1), room, T0, art).ArtUrl);
        // Phones get the same public view inside theirs.
        Assert.Equal("/media/stage-1", EscapeProjector.Player(s, room, Ada, T0, art).Stage.ArtUrl);
        // No pictures made: nothing to show.
        Assert.Null(EscapeProjector.Stage(s, room, T0).ArtUrl);
    }

    [Fact]
    public void A_built_puzzle_set_keeps_everything_about_the_room_except_its_puzzles()
    {
        // RoomVariants copies a room's settings by hand, so a new setting (like Soundscape) could be forgotten there.
        foreach (var template in Rooms.Library)
        {
            var built = RoomVariants.Build(template, 42);
            foreach (var property in typeof(EscapeRoom).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.Name != nameof(EscapeRoom.Puzzles) && p.GetIndexParameters().Length == 0))
            {
                Assert.True(Equals(property.GetValue(template), property.GetValue(built)), $"{template.Id}: {property.Name} was lost building a puzzle set.");
            }
        }
    }
}
