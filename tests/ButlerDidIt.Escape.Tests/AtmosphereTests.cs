using System.Reflection;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;

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
            s = EscapeEngine.Apply(s, template, EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, template), Ada, T0));
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
    public void Uploaded_videos_and_sounds_play_for_the_stage_in_front_of_the_group()
    {
        var room = Workshop;
        var (first, second) = (room.Stages[0].Id, room.Stages[1].Id);
        var media = new Dictionary<string, string>
        {
            [EscapeArt.IntroVideo] = "/media/intro",
            [EscapeArt.Ambience] = "/media/room-sound",
            [EscapeArt.StageVideo(first)] = "/media/stage-1-video",
            [EscapeArt.StageAmbience(second)] = "/media/stage-2-sound",
        };
        var lobby = EscapeProjector.Stage(EscapeEngine.NewGame(7), room, T0, media);
        Assert.Equal("/media/intro", lobby.IntroVideoUrl); // public, like the intro itself
        Assert.Null(lobby.StageVideoUrl);
        Assert.Equal("/media/room-sound", lobby.AmbienceUrl);

        var s = Started(room);
        var one = EscapeProjector.Stage(s, room, T0, media);
        Assert.Equal("/media/stage-1-video", one.StageVideoUrl);
        Assert.Equal("/media/room-sound", one.AmbienceUrl); // the first stage has no sound of its own: the room's plays

        var two = EscapeProjector.Stage(ToStage(s, room, 1), room, T0, media);
        Assert.Null(two.StageVideoUrl);
        Assert.Equal("/media/stage-2-sound", two.AmbienceUrl);
        // Nothing uploaded: the made-up sound, and no videos.
        var plain = EscapeProjector.Stage(s, room, T0);
        Assert.Null(plain.IntroVideoUrl);
        Assert.Null(plain.AmbienceUrl);
    }

    [Fact]
    public void A_built_puzzle_set_keeps_everything_about_the_room_except_its_puzzles()
    {
        // RoomVariants copies a room's settings by hand, so a new setting (like Soundscape) could be forgotten there.
        foreach (var template in Rooms.Library)
        {
            var built = RoomVariants.Build(template, 42);
            foreach (var property in typeof(EscapeRoom).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         // Settings, not computed ones. Stages and items are rebuilt when cipher keys are written into their text; checked below.
                         .Where(p => p.Name is not (nameof(EscapeRoom.Puzzles) or nameof(EscapeRoom.Stages) or nameof(EscapeRoom.Items)) && p.SetMethod is not null))
            {
                Assert.True(Equals(property.GetValue(template), property.GetValue(built)), $"{template.Id}: {property.Name} was lost building a puzzle set.");
            }
            Assert.Equal(template.Stages.Select(s => (s.Id, s.Title, s.Description, s.Soundscape, string.Join(",", s.Puzzles))),
                built.Stages.Select(s => (s.Id, s.Title, s.Description, s.Soundscape, string.Join(",", s.Puzzles))));
            Assert.Equal(template.SceneObjects.Select(o => (o.Id, o.Prop, o.X, o.Y, o.W, o.H, o.Gives, o.HidesPieces)),
                built.SceneObjects.Select(o => (o.Id, o.Prop, o.X, o.Y, o.W, o.H, o.Gives, o.HidesPieces)));
            Assert.Equal(template.Items.Select(i => (i.Id, i.Name, i.InspectGives)), built.Items.Select(i => (i.Id, i.Name, i.InspectGives)));
        }
    }
}
