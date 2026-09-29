using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Api.Games;

/// <summary>The murder mystery as a game module: the existing engine (GameEngine, ViewProjector) behind the platform's interface.</summary>
public sealed class MysteryModule(ContentCatalog catalog) : IGameModule
{
    public GameKind Kind => GameKind.Mystery;

    public async Task<GameSession> LoadAsync(AppDbContext db, Party party, CancellationToken ct) =>
        new MysterySession(GameJson.Deserialize<GameState>(party.State), await catalog.GetScenarioAsync(db, party.ScenarioId, ct));
}

public sealed class MysterySession(GameState state, Scenario scenario) : GameSession
{
    public GameState State { get; } = state;
    public Scenario Scenario { get; } = scenario;

    /// <summary>The session after a mystery change. The same instance when the engine changed nothing (e.g. an idle tick).</summary>
    public MysterySession With(GameState next, Scenario scenario) =>
        ReferenceEquals(next, State) ? this : new MysterySession(next, scenario);

    private MysterySession Apply(Command command) => With(GameEngine.Apply(State, Scenario, command), Scenario);

    public override IReadOnlyList<Guid> SeatIds => State.Players.Select(p => p.SeatId).ToList();

    public override PartyStatus Status => State.Phase switch
    {
        Phase.Lobby => PartyStatus.Lobby,
        Phase.Finished => PartyStatus.Finished,
        _ => PartyStatus.InProgress,
    };

    public override DateTimeOffset? NextDueAt => GameEngine.NextDueAt(State);
    public override string ContentId => Scenario.Id;
    public override string Serialize() => GameJson.Serialize(State);
    public override object StageView(DateTimeOffset now) => ViewProjector.Stage(State, Scenario, now);
    public override object PlayerView(Guid seatId, DateTimeOffset now) => ViewProjector.Player(State, Scenario, seatId, now);

    // Guests see the shared story's id: a version's id would hint at which killer they're facing.
    public override GameSummary Describe(bool isHost) =>
        new(isHost ? Scenario.Id : Scenario.VariantOf ?? Scenario.Id, Scenario.Title, Scenario.ThemeSlug, State.Players.Count, Scenario.MaxPlayers);

    public override string? PhotoUrl(Guid seatId) => State.FindPlayer(seatId)?.PhotoUrl;

    public override GameSession AddPlayer(DateTimeOffset now, Guid seatId, string name, bool isHost, bool isLocal) =>
        Apply(new AddPlayer(now, seatId, name, isHost, isLocal));
    public override GameSession RemovePlayer(DateTimeOffset now, Guid seatId) => Apply(new RemovePlayer(now, seatId));
    public override GameSession SetPlayerPhoto(DateTimeOffset now, Guid seatId, string? url) => Apply(new SetPlayerPhoto(now, seatId, url));
    public override GameSession Tick(DateTimeOffset now) => Apply(new Tick(now));
}
