namespace ButlerDidIt.Escape.Engine;

/// <summary>
/// Names for a room's generated pictures. The pictures are made and stored outside the engine
/// (the API's media pipeline); the projector only picks which one the TV shows, by these keys.
/// </summary>
public static class EscapeArt
{
    public const string Cover = "cover";
    public static string Stage(string stageId) => $"stage:{stageId}";
}
