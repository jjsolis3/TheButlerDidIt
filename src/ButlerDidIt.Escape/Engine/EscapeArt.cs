namespace ButlerDidIt.Escape.Engine;

/// <summary>
/// Names for a room's pictures, videos and sounds: the ones the AI paints, and the ones a host uploads. The files are
/// made and stored outside the engine (the API's media pipeline); the projector only picks which ones a screen gets,
/// by these keys, and never a stage the group hasn't reached.
/// </summary>
public static class EscapeArt
{
    public const string Cover = "cover";
    public static string Stage(string stageId) => $"stage:{stageId}";

    /// <summary>A video that plays when the clock starts, instead of the cover and the intro read out.</summary>
    public const string IntroVideo = "intro-video";

    /// <summary>A video that plays when a stage opens.</summary>
    public static string StageVideo(string stageId) => $"stage-video:{stageId}";

    /// <summary>The game master reading the room's intro aloud as the clock starts (the AI's Voice role, made once per room).</summary>
    public const string IntroVoice = "intro-voice";

    /// <summary>The game master reading a stage's description aloud as it opens.</summary>
    public static string StageVoice(string stageId) => $"stage-voice:{stageId}";

    /// <summary>A recorded background sound for the whole room, looped instead of the made-up one.</summary>
    public const string Ambience = "ambience";

    /// <summary>A recorded background sound for one stage, played instead of the room's.</summary>
    public static string StageAmbience(string stageId) => $"ambience:{stageId}";
}
