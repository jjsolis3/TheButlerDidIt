using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Games;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Escape;

/// <summary>
/// The hand-written escape rooms in content/escape. They are read once, checked by
/// <see cref="EscapeRoomValidator"/> (a broken room stops the app starting, like a broken
/// mystery), and kept in memory: they're small, and every server reads the same files.
/// </summary>
public sealed class EscapeCatalog(IOptions<ContentOptions> options, IWebHostEnvironment env)
{
    private readonly Lazy<IReadOnlyList<EscapeRoom>> _rooms = new(() =>
    {
        var root = Path.Combine(env.ContentRootPath, options.Value.Root, "escape");
        var rooms = EscapeLibrary.Load(root);
        var problems = rooms.SelectMany(r => EscapeRoomValidator.Validate(r).Select(e => $"{r.Id}: {e}")).ToList();
        if (problems.Count > 0) throw new InvalidOperationException("Invalid escape rooms:\n" + string.Join("\n", problems));
        return rooms;
    });

    public IReadOnlyList<EscapeRoom> Rooms => _rooms.Value;
    public EscapeRoom? Find(string id) => Rooms.FirstOrDefault(r => r.Id == id);
}

/// <summary>What the create-party page shows for each room.</summary>
public sealed record EscapeRoomSummary(
    string Id, string Title, string Synopsis, ButlerDidIt.Game.Scenarios.ContentRating ContentRating, string Theme,
    int MinPlayers, int MaxPlayers, int TimeLimitMinutes, int StageCount, int PuzzleCount)
{
    public static EscapeRoomSummary For(EscapeRoom r) =>
        new(r.Id, r.Title, r.Synopsis, r.ContentRating, r.Theme, r.MinPlayers, r.MaxPlayers, r.TimeLimitMinutes, r.Stages.Count, r.Puzzles.Count);
}

/// <summary>Escape rooms as a game module: the escape engine behind the platform's interface.</summary>
public sealed class EscapeModule(EscapeCatalog catalog) : IGameModule
{
    public GameKind Kind => GameKind.EscapeRoom;

    public Task<GameSession> LoadAsync(AppDbContext db, Party party, CancellationToken ct)
    {
        var room = catalog.Find(party.ScenarioId) ?? throw new GameRuleException("This escape room is no longer available.");
        return Task.FromResult<GameSession>(new EscapeSession(GameJson.Deserialize<EscapeState>(party.State), room));
    }
}

public sealed class EscapeSession(EscapeState state, EscapeRoom room) : GameSession
{
    public EscapeState State { get; } = state;
    public EscapeRoom Room { get; } = room;

    /// <summary>The session after a command. The same instance when the engine changed nothing (an idle tick).</summary>
    public EscapeSession Apply(EscapeCommand command)
    {
        var next = EscapeEngine.Apply(State, Room, command);
        return ReferenceEquals(next, State) ? this : new EscapeSession(next, Room);
    }

    public override IReadOnlyList<Guid> SeatIds => State.Players.Select(p => p.SeatId).ToList();

    public override PartyStatus Status => State.Phase switch
    {
        EscapePhase.Lobby => PartyStatus.Lobby,
        EscapePhase.Playing => PartyStatus.InProgress,
        _ => PartyStatus.Finished,
    };

    public override DateTimeOffset? NextDueAt => EscapeEngine.NextDueAt(State);
    public override string ContentId => Room.Id;
    public override string Serialize() => GameJson.Serialize(State);
    public override object StageView(DateTimeOffset now) => EscapeProjector.Stage(State, Room, now);
    public override object PlayerView(Guid seatId, DateTimeOffset now) => EscapeProjector.Player(State, Room, seatId, now);
    public override GameSummary Describe(bool isHost) => new(Room.Id, Room.Title, Room.Theme, State.Players.Count, Room.MaxPlayers);
    public override string? PhotoUrl(Guid seatId) => State.FindPlayer(seatId)?.PhotoUrl;

    public override GameSession AddPlayer(DateTimeOffset now, Guid seatId, string name, bool isHost, bool isLocal) =>
        Apply(new AddEscapePlayer(now, seatId, name, isHost, isLocal));
    public override GameSession RemovePlayer(DateTimeOffset now, Guid seatId) => Apply(new RemoveEscapePlayer(now, seatId));
    public override GameSession SetPlayerPhoto(DateTimeOffset now, Guid seatId, string? url) => Apply(new SetEscapePlayerPhoto(now, seatId, url));
    public override GameSession Tick(DateTimeOffset now) => Apply(new EscapeTick(now));
}

/// <summary>Runs escape commands through the platform's <see cref="PartyRuntime"/>, refusing parties of another kind.</summary>
public sealed class EscapeService(PartyRuntime runtime)
{
    public async Task<EscapeSession> ExecuteAsync(Guid partyId, Func<EscapeSession, DateTimeOffset, EscapeCommand> command, CancellationToken ct = default)
    {
        var (_, session) = await runtime.ExecuteAsync(partyId, (s, now) =>
        {
            var escape = s as EscapeSession ?? throw new GameRuleException("That isn't part of this party's game.");
            return escape.Apply(command(escape, now));
        }, ct: ct);
        return (EscapeSession)session;
    }
}
