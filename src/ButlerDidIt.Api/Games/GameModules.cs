using ButlerDidIt.Api.Data;
using ButlerDidIt.Game.Engine;

namespace ButlerDidIt.Api.Games;

/// <summary>
/// One party's game, loaded and ready to play: the rules engine's state plus whatever
/// content it needs (a mystery's scenario, later an escape room's puzzles).
///
/// The platform (joining, seats, selfies, the ticker, saving, broadcasting) only ever
/// talks to a party through this class, so it works the same for every kind of game.
/// Anything specific to one game (accusations, puzzles…) stays in that game's own code.
///
/// Sessions are immutable, like the engines' states: every command returns a new session,
/// or this same instance when nothing changed, which tells the runtime there is nothing to save.
/// </summary>
public abstract class GameSession
{
    /// <summary>Every seat in the game, for sending each phone its own view.</summary>
    public abstract IReadOnlyList<Guid> SeatIds { get; }

    public abstract PartyStatus Status { get; }

    /// <summary>When the ticker should next wake this party (a timed event), or null.</summary>
    public abstract DateTimeOffset? NextDueAt { get; }

    /// <summary>What the party plays now (Party.ScenarioId). A mystery can switch to another version of its story when it starts.</summary>
    public abstract string ContentId { get; }

    /// <summary>The engine's state as JSON, stored in Party.State.</summary>
    public abstract string Serialize();

    /// <summary>What the TV may show. Must never contain anything private.</summary>
    public abstract object StageView(DateTimeOffset now);

    /// <summary>What one seat's phone may show.</summary>
    public abstract object PlayerView(Guid seatId, DateTimeOffset now);

    /// <summary>The join page's summary of the party.</summary>
    public abstract GameSummary Describe(bool isHost);

    public abstract string? PhotoUrl(Guid seatId);

    // ---- Commands every game supports, because the platform itself needs them.
    public abstract GameSession AddPlayer(DateTimeOffset now, Guid seatId, string name, bool isHost, bool isLocal);
    public abstract GameSession RemovePlayer(DateTimeOffset now, Guid seatId);
    public abstract GameSession SetPlayerPhoto(DateTimeOffset now, Guid seatId, string? url);
    public abstract GameSession Tick(DateTimeOffset now);
}

/// <param name="ContentId">The id to show. A mystery shows guests the shared story's id, never the version's, which would hint at the killer.</param>
public sealed record GameSummary(string ContentId, string Title, string ThemeSlug, int PlayerCount, int MaxPlayers);

/// <summary>A kind of game: knows how to load a party's session from its database row.</summary>
public interface IGameModule
{
    GameKind Kind { get; }
    Task<GameSession> LoadAsync(AppDbContext db, Party party, CancellationToken ct);
}

/// <summary>Finds the module for a party's kind. Every module is registered as an IGameModule.</summary>
public sealed class GameModules(IEnumerable<IGameModule> modules)
{
    private readonly Dictionary<GameKind, IGameModule> _byKind = modules.ToDictionary(m => m.Kind);

    /// <summary>The module for <paramref name="kind"/>. A kind this server can't play (yet) is a normal game message, not a crash.</summary>
    public IGameModule For(GameKind kind) =>
        _byKind.TryGetValue(kind, out var module) ? module : throw new GameRuleException($"This server can't run {Describe(kind)} games yet.");

    public static string Describe(GameKind kind) => kind switch
    {
        GameKind.Mystery => "murder-mystery",
        GameKind.EscapeRoom => "escape-room",
        _ => kind.ToString(),
    };
}
