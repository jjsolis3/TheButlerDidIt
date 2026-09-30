using System.Collections.Concurrent;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Games;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Escape;

/// <summary>
/// Every escape room this server can play.
///
/// The hand-written rooms in content/escape are read once, checked by <see cref="EscapeRoomValidator"/>
/// (a broken room stops the app starting, like a broken mystery), and kept in memory: they're small,
/// and every server reads the same files.
///
/// Rooms written by AI live in the database (<see cref="EscapeRoomEntity"/>), one host's each. They
/// were validated before they were saved and never change, so each is parsed once and cached.
///
/// For the end-to-end tests only, Escape:TestRoomsRoot adds the rooms in another folder (the test-only
/// Laboratory, which uses every kind of puzzle). It's ignored unless Escape:ExposeAnswersForTests is on,
/// which is never the case in production.
/// </summary>
public sealed class EscapeCatalog(IOptions<ContentOptions> options, IWebHostEnvironment env, IConfiguration config)
{
    private readonly Lazy<IReadOnlyList<EscapeRoom>> _rooms = new(() =>
    {
        var root = Path.Combine(env.ContentRootPath, options.Value.Root, "escape");
        var rooms = EscapeLibrary.Load(root);
        if (config.GetValue<bool>("Escape:ExposeAnswersForTests") && config["Escape:TestRoomsRoot"] is { Length: > 0 } testRoot)
            rooms.AddRange(EscapeLibrary.Load(testRoot));
        var problems = rooms.SelectMany(r => EscapeRoomValidator.Validate(r).Select(e => $"{r.Id}: {e}")).ToList();
        if (problems.Count > 0) throw new InvalidOperationException("Invalid escape rooms:\n" + string.Join("\n", problems));
        return rooms;
    });

    // Keeping the same instance per id also keeps RoomVariants' cache of built puzzle sets warm.
    private readonly ConcurrentDictionary<string, EscapeRoom> _generated = new();

    /// <summary>The hand-written rooms, on every host's shelf.</summary>
    public IReadOnlyList<EscapeRoom> Rooms => _rooms.Value;

    /// <summary>A hand-written room.</summary>
    public EscapeRoom? Find(string id) => Rooms.FirstOrDefault(r => r.Id == id);

    /// <summary>Any room, hand-written or written by AI: for loading a party that's already playing it.</summary>
    public async Task<EscapeRoom?> FindAsync(AppDbContext db, string id, CancellationToken ct)
    {
        if (Find(id) is { } room) return room;
        if (_generated.TryGetValue(id, out var cached)) return cached;
        var document = await db.EscapeRooms.AsNoTracking().Where(r => r.Id == id).Select(r => r.Document).FirstOrDefaultAsync(ct);
        return document is null ? null : Parse(id, document);
    }

    /// <summary>
    /// A room this host may start a party with: a hand-written one, or one written for them.
    /// Always asks the database for an AI room, so a deleted one can't be started from another server's cache.
    /// </summary>
    public async Task<EscapeRoom?> FindForHostAsync(AppDbContext db, string id, string hostUserId, CancellationToken ct)
    {
        if (Find(id) is { } room) return room;
        var document = await db.EscapeRooms.AsNoTracking().Where(r => r.Id == id && r.OwnerUserId == hostUserId).Select(r => r.Document).FirstOrDefaultAsync(ct);
        return document is null ? null : Parse(id, document);
    }

    /// <summary>The rooms written for this host, newest first.</summary>
    public async Task<IReadOnlyList<EscapeRoom>> OwnedAsync(AppDbContext db, string hostUserId, CancellationToken ct)
    {
        var rows = await db.EscapeRooms.AsNoTracking().Where(r => r.OwnerUserId == hostUserId).OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.Document }).ToListAsync(ct);
        return rows.Select(r => Parse(r.Id, r.Document)).ToList();
    }

    /// <summary>Drops a deleted room from this server's cache.</summary>
    public void Forget(string id) => _generated.TryRemove(id, out _);

    // Pictures appear while parties play (the media job runs in the background), possibly on another
    // server, so they're cached only briefly: a party's commands would otherwise each ask the database.
    private static readonly TimeSpan ArtFreshFor = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyDictionary<string, string> Art)> _art = new();

    /// <summary>A room's generated pictures (URLs by <see cref="EscapeArt"/> key). Empty until some are made.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ArtAsync(AppDbContext db, string roomId, DateTimeOffset now, CancellationToken ct)
    {
        if (_art.TryGetValue(roomId, out var cached) && now - cached.At < ArtFreshFor) return cached.Art;
        var jobId = EscapeMedia.JobId(roomId);
        var art = await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == jobId)
            .ToDictionaryAsync(m => m.Key, m => ButlerDidIt.Api.Media.MediaStore.Url(m.AssetId), ct);
        _art[roomId] = (now, art);
        return art;
    }

    /// <summary>New pictures were made for this room: read them again on the next load.</summary>
    public void ForgetArt(string roomId) => _art.TryRemove(roomId, out _);

    private EscapeRoom Parse(string id, string document) => _generated.GetOrAdd(id, _ => GameJson.Deserialize<EscapeRoom>(document));
}

/// <summary>What the create-party page shows for each room, with the best escape so far (score in seconds, or null).</summary>
public sealed record EscapeRoomSummary(
    string Id, string Title, string Synopsis, ButlerDidIt.Game.Scenarios.ContentRating ContentRating, string Theme,
    int MinPlayers, int MaxPlayers, int TimeLimitMinutes, int StageCount, int PuzzleCount, int HintPenaltySeconds, int? BestScore, string GameMaster,
    /// <summary>Written by AI for this host: only they see it, and they can delete it.</summary>
    bool Generated,
    /// <summary>The lengths a host can pick, shortest first, with how many puzzles each plays.</summary>
    IReadOnlyList<EscapeLength> Lengths,
    /// <summary>Seasonal shelves the room is on ("halloween").</summary>
    IReadOnlyList<string> Seasons)
{
    /// <param name="bestScore">The best escape at the room's own length.</param>
    public static EscapeRoomSummary For(EscapeRoom r, int? bestScore, bool generated = false)
    {
        // Counted on the room as played at its own length, so the card matches the game the host gets by default.
        var standard = RoomLengths.Cut(r, null);
        return new(r.Id, r.Title, r.Synopsis, r.ContentRating, r.Theme, r.MinPlayers, r.MaxPlayers, r.TimeLimitMinutes, standard.Stages.Count, standard.Puzzles.Count,
            r.HintPenaltySeconds, bestScore, r.Host.Name, generated,
            r.PlayableLengths.Select(m => new EscapeLength(m, r.Puzzles.Count(p => RoomLengths.Plays(p, m)))).ToList(), r.Seasons);
    }
}

public sealed record EscapeLength(int Minutes, int PuzzleCount);

public static class EscapeResults
{
    /// <summary>A room's results at one length. Results from before lengths existed count as the room's own length.</summary>
    public static IQueryable<EscapeResult> AtLength(this IQueryable<EscapeResult> results, EscapeRoom room, int minutes) =>
        minutes == room.TimeLimitMinutes ? results.Where(r => r.Minutes == minutes || r.Minutes == null) : results.Where(r => r.Minutes == minutes);

    /// <summary>A room's results on its current edition. Results from before editions count as edition 1.</summary>
    public static IQueryable<EscapeResult> AtEdition(this IQueryable<EscapeResult> results, EscapeRoom room) =>
        room.Edition == 1 ? results.Where(r => r.Edition == 1 || r.Edition == null) : results.Where(r => r.Edition == room.Edition);

    /// <summary>A room's results at one difficulty. Results from before difficulties existed count as Normal.</summary>
    public static IQueryable<EscapeResult> AtDifficulty(this IQueryable<EscapeResult> results, EscapeDifficulty difficulty) =>
        difficulty == EscapeDifficulty.Normal
            ? results.Where(r => r.Difficulty == EscapeDifficulty.Normal || r.Difficulty == null)
            : results.Where(r => r.Difficulty == difficulty);
}

/// <summary>Escape rooms as a game module: the escape engine behind the platform's interface.</summary>
/// <summary>
/// An escape room's pictures go through the mystery media pipeline (MediaWorker, MediaService's cache).
/// Its jobs and stored pictures use "escape:{room id}" where a mystery uses its scenario id, so the two never mix.
/// </summary>
public static class EscapeMedia
{
    private const string Prefix = "escape:";
    public static string JobId(string roomId) => Prefix + roomId;
    public static string? RoomId(string jobId) => jobId.StartsWith(Prefix, StringComparison.Ordinal) ? jobId[Prefix.Length..] : null;
}

public sealed class EscapeModule(EscapeCatalog catalog, TimeProvider clock) : IGameModule
{
    public GameKind Kind => GameKind.EscapeRoom;

    public async Task<GameSession> LoadAsync(AppDbContext db, Party party, CancellationToken ct)
    {
        var room = await catalog.FindAsync(db, party.ScenarioId, ct) ?? throw new GameRuleException("This escape room is no longer available.");
        var art = await catalog.ArtAsync(db, room.Id, clock.GetUtcNow(), ct);
        return new EscapeSession(GameJson.Deserialize<EscapeState>(party.State), room, art);
    }
}

/// <param name="art">The room's generated pictures, by <see cref="EscapeArt"/> key.</param>
public sealed class EscapeSession(EscapeState state, EscapeRoom room, IReadOnlyDictionary<string, string>? art = null) : GameSession
{
    public EscapeState State { get; } = state;
    public EscapeRoom Room { get; } = room;
    public IReadOnlyDictionary<string, string>? Art { get; } = art;

    /// <summary>The session after a command. The same instance when the engine changed nothing (an idle tick).</summary>
    public EscapeSession Apply(EscapeCommand command)
    {
        var next = EscapeEngine.Apply(State, Room, command);
        return ReferenceEquals(next, State) ? this : new EscapeSession(next, Room, Art);
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
    public override object StageView(DateTimeOffset now) => EscapeProjector.Stage(State, Room, now, Art);
    public override object PlayerView(Guid seatId, DateTimeOffset now) => EscapeProjector.Player(State, Room, seatId, now, Art);
    public override GameSummary Describe(bool isHost) => new(Room.Id, Room.Title, Room.Theme, State.Players.Count, Room.MaxPlayers);
    public override string? PhotoUrl(Guid seatId) => State.FindPlayer(seatId)?.PhotoUrl;

    public override GameSession AddPlayer(DateTimeOffset now, Guid seatId, string name, bool isHost, bool isLocal) =>
        Apply(new AddEscapePlayer(now, seatId, name, isHost, isLocal));
    public override GameSession RemovePlayer(DateTimeOffset now, Guid seatId) => Apply(new RemoveEscapePlayer(now, seatId));
    public override GameSession SetPlayerPhoto(DateTimeOffset now, Guid seatId, string? url) => Apply(new SetEscapePlayerPhoto(now, seatId, url));
    public override GameSession Tick(DateTimeOffset now) => Apply(new EscapeTick(now));

    /// <summary>The moment the game ends (escaped, or out of time), record the result for the leaderboards.</summary>
    public override void OnSaving(AppDbContext db, Party party, GameSession previous)
    {
        if (previous is not EscapeSession { State.Phase: EscapePhase.Playing }) return;
        if (State.Phase is not (EscapePhase.Escaped or EscapePhase.Failed)) return;
        var elapsed = (int)Math.Round((State.EndedAt!.Value - State.StartedAt!.Value).TotalSeconds);
        var team = string.Join(", ", State.Players.Select(p => p.Name));
        db.EscapeResults.Add(new EscapeResult
        {
            Id = Guid.NewGuid(),
            RoomId = Room.Id,
            PartyId = party.Id,
            HostUserId = party.HostUserId,
            Seed = State.Seed,
            Daily = State.Daily,
            Minutes = State.Minutes ?? Room.TimeLimitMinutes,
            Difficulty = State.Level,
            Edition = Room.Edition,
            Escaped = State.Phase == EscapePhase.Escaped,
            ElapsedSeconds = elapsed,
            HintsUsed = State.HintsUsed,
            WrongAttempts = State.WrongAttempts,
            // The penalty as played: Easy halves it.
            Score = elapsed + State.HintsUsed * EscapeEngine.RoomFor(State, Room).HintPenaltySeconds,
            PlayerCount = State.Players.Count,
            Team = team.Length <= 400 ? team : team[..400],
            FinishedAt = State.EndedAt!.Value,
        });
    }
}

/// <summary>Which puzzles a new escape party plays.</summary>
public enum PuzzleChoice
{
    /// <summary>A new puzzle set nobody has seen: the default.</summary>
    Fresh,

    /// <summary>Today's challenge: every group gets the same set today and shares a leaderboard.</summary>
    Daily,

    /// <summary>A puzzle set someone shared (its number is shown at the end of every game).</summary>
    Replay,
}

public static class PuzzleSets
{
    public const long MaxPuzzleSet = 999_999;

    /// <summary>The seed for a new party. Chosen by the server (never the engine, which stays free of randomness).</summary>
    public static (long Seed, bool Daily) For(PuzzleChoice choice, long? replay, DateTimeOffset now) => choice switch
    {
        PuzzleChoice.Daily => (long.Parse(now.UtcDateTime.ToString("yyyyMMdd")), true),
        PuzzleChoice.Replay when replay is >= 0 and <= MaxPuzzleSet => (replay.Value, false),
        PuzzleChoice.Replay => throw new GameRuleException($"A puzzle set is a number from 0 to {MaxPuzzleSet}."),
        _ => (System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, (int)MaxPuzzleSet + 1), false),
    };
}

/// <summary>Runs escape commands through the platform's <see cref="PartyRuntime"/>, refusing parties of another kind.</summary>
public sealed class EscapeService(PartyRuntime runtime)
{
    public DateTimeOffset Now => runtime.Now;

    public async Task<EscapeSession> ExecuteAsync(Guid partyId, Func<EscapeSession, DateTimeOffset, EscapeCommand> command, CancellationToken ct = default) =>
        (await RunAsync(partyId, command, ct)).Session;

    /// <summary>Like <see cref="ExecuteAsync"/>, also returning the party row (for its host, when an AI call is billed).</summary>
    public async Task<(Party Party, EscapeSession Session)> RunAsync(Guid partyId, Func<EscapeSession, DateTimeOffset, EscapeCommand> command, CancellationToken ct = default)
    {
        var (party, session) = await runtime.ExecuteAsync(partyId, (s, now) => Escape(s).Apply(command(Escape(s), now)), ct: ct);
        return (party, (EscapeSession)session);
    }

    public async Task<(Party Party, EscapeSession Session)> LoadAsync(Guid partyId, CancellationToken ct = default)
    {
        var (party, session) = await runtime.LoadAsync(partyId, ct);
        return (party, Escape(session));
    }

    private static EscapeSession Escape(GameSession s) => s as EscapeSession ?? throw new GameRuleException("That isn't part of this party's game.");
}
