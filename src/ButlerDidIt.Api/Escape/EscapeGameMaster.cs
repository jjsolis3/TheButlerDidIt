using System.Threading.Channels;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Media;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Api.Scale;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using Microsoft.Extensions.AI;

namespace ButlerDidIt.Api.Escape;

/// <summary>
/// The escape room's AI game master. Like the mystery's AI actions, the engine reserves what's
/// needed first (the hint is paid for at once), the AI is called outside the party's lock so the
/// room never freezes, and the result comes back as a command the engine checks.
/// </summary>
public sealed class EscapeGameMaster(EscapeService escape, AiGateway ai, MediaService media, ClusterLock cluster, ILogger<EscapeGameMaster> log)
{
    /// <summary>A moment older than this isn't worth narrating any more: the group has moved on.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A hint for <paramref name="puzzleId"/>. Without AI hints it's simply the room's next written hint.
    /// With them, the AI writes one for where the group is stuck; if it fails, or its hint gives the
    /// answer away, the written hint is shown instead. Either way the time is spent once.
    /// </summary>
    public async Task RequestHintAsync(Guid partyId, Guid? seatId, string puzzleId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var (party, session) = await escape.RunAsync(partyId, (s, now) =>
            s.State.Ai.Hints ? new BeginEscapeHint(now, id, seatId, puzzleId) : new RequestEscapeHint(now, seatId, puzzleId), ct);
        if (session.State.AiHints.FirstOrDefault(h => h.Id == id) is not { } hint) return; // a written hint, or the hint ended the game

        try
        {
            var system = EscapePrompts.Hint(session.Room, session.State, puzzleId, hint.Index, escape.Now);
            var puzzle = EscapeEngine.RoomFor(session.State, session.Room).FindPuzzle(puzzleId)!;
            var context = new AiCallContext(party.HostUserId, partyId, Purpose: "escape-hint");

            var text = await ai.CompleteAsync(AiRole.Inspector, system, [new ChatMessage(ChatRole.User, "We're stuck. A hint, please.")], context, 200, ct: ct);
            if (EscapeHintGuard.Rejects(text, puzzle))
            {
                // The model gave too much away. Ask once more; the engine checks again and falls back if it still does.
                text = await ai.CompleteAsync(AiRole.Inspector, system,
                    [new ChatMessage(ChatRole.User, "We're stuck. Give us a nudge that contains no numbers, codes or answers at all.")], context, 200, ct: ct);
            }
            await escape.ExecuteAsync(partyId, (_, now) => new CompleteEscapeHint(now, id, text), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The written hint shows instead, so the group still gets what it paid for.
            log.LogWarning(ex, "The AI hint for party {PartyId} failed; showing the written hint", partyId);
            await escape.ExecuteAsync(partyId, (_, now) => new CancelEscapeHint(now, id), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await escape.ExecuteAsync(partyId, (_, now) => new CancelEscapeHint(now, id), CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Gives the newest moment waiting in this party its line (and recording), and passes over the
    /// ones before it. Repeats until nothing is waiting, since the group may do more while it speaks.
    /// Narration is a bonus: if the AI fails, the moment is passed over and the ticker still says what happened.
    /// </summary>
    public async Task NarrateAsync(Guid partyId, CancellationToken ct)
    {
        // With several servers, only one speaks for a party at a time.
        await using var turn = await cluster.TryAcquireAsync($"narration:{partyId}", ct);
        if (turn is null) return;

        for (var round = 0; round < 5; round++)
        {
            var (party, session) = await escape.LoadAsync(partyId, ct);
            var now = escape.Now;
            if (!session.State.Ai.GameMaster) return;
            var cue = session.State.Cues.LastOrDefault(c => c.Text is null && !c.Skipped);
            if (cue is null || now - cue.At > StaleAfter) return;

            await escape.ExecuteAsync(partyId, (_, t) => new SkipCues(t, cue.Id), ct);
            string text;
            try
            {
                text = await ai.CompleteAsync(AiRole.Actor, EscapePrompts.Narration(session.Room, session.State, cue, now),
                    [new ChatMessage(ChatRole.User, "Say your line.")], new AiCallContext(party.HostUserId, partyId, Purpose: "escape-narration"), 150, ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "The game master's line for party {PartyId} failed", partyId);
                await escape.ExecuteAsync(partyId, (_, t) => new SkipCues(t, cue.Id + 1), ct);
                continue;
            }
            var spoken = await escape.ExecuteAsync(partyId, (_, t) => new SetCueNarration(t, cue.Id, text), ct);
            if (spoken.State.Ai.Voice) await VoiceAsync(party, spoken, cue.Id, ct);
        }
    }

    /// <summary>Records the line in the game master's voice. The text is already on the TV, so on failure the browser reads it.</summary>
    private async Task VoiceAsync(Party party, EscapeSession session, int cueId, CancellationToken ct)
    {
        try
        {
            var line = session.State.Cues.First(c => c.Id == cueId).Text!;
            var host = session.Room.Host;
            var assetId = await media.SpeechAsync(line, VoiceCasting.For($"game-master:{session.Room.Id}", host.Voice),
                new AiCallContext(party.HostUserId, party.Id, Purpose: "escape-voice"), ct);
            await escape.ExecuteAsync(party.Id, (_, now) => new SetCueAudio(now, cueId, MediaStore.Url(assetId)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not voice the game master's line for party {PartyId}", party.Id);
        }
    }
}

/// <summary>Parties with a moment waiting for the game master, drained by <see cref="NarrationWorker"/>.</summary>
public sealed class NarrationQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public void Enqueue(Guid partyId) => _channel.Writer.TryWrite(partyId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Notices, after every saved change, when an escape room has a new moment for its game master.
/// It runs after the commit, so the narrator never reads a change that might still roll back,
/// and it hears the ticker's changes (five minutes left, time's up) as well as the players'.
/// </summary>
public sealed class NarrationTrigger(NarrationQueue queue) : IPartySavedHandler
{
    public void OnSaved(Party party, Games.GameSession previous, Games.GameSession next)
    {
        if (next is not EscapeSession { State: { Ai.GameMaster: true } state }) return;
        var before = (previous as EscapeSession)?.State.NextCueId ?? 0;
        if (state.NextCueId > before) queue.Enqueue(party.Id);
    }
}

/// <summary>
/// Speaks for escape rooms in the background, one party at a time, so a slow AI never holds up
/// a player's move. The queue lives in memory: after a restart, waiting moments are simply stale.
/// </summary>
public sealed class NarrationWorker(NarrationQueue queue, IServiceScopeFactory scopes, ILogger<NarrationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var partyId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EscapeGameMaster>().NarrateAsync(partyId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The party may have been deleted meanwhile, or the AI is down: the game carries on without a line.
                log.LogWarning(ex, "The game master couldn't speak for party {PartyId}", partyId);
            }
        }
    }
}
