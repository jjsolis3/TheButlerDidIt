using System.Threading.Channels;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Prompts;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game.Engine;
using Microsoft.Extensions.AI;

namespace ButlerDidIt.Api.Ai;

/// <summary>
/// The AI actions during a party. Each follows the same pattern:
///
///   1. Begin*: the pure engine checks the rules and reserves a slot. The whole
///      room sees "Hargrove is thinking…" at once, and the per-act limit can't be
///      beaten by tapping twice.
///   2. Call the AI outside the party lock, so the game never freezes while it thinks.
///   3. Complete* stores the answer, or Cancel* returns the slot if the AI failed.
/// </summary>
public sealed class AiGameService(PartyService parties, AiGateway ai, VerdictQueue verdicts, ButlerDidIt.Api.Media.MediaService media, ILogger<AiGameService> log)
{
    public async Task AskNpcAsync(Guid partyId, Guid seatId, string characterId, string question, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var snapshot = await parties.ExecuteAsync(partyId, (_, now) => new BeginNpcQuestion(now, id, seatId, characterId, question), ct: ct);
        var completed = false;
        try
        {
            var asker = snapshot.State.FindPlayer(seatId)?.Name ?? "A guest";
            var (system, conversation) = NpcPrompt.Build(snapshot.Scenario, snapshot.State, characterId, asker, question.Trim(), snapshot.Party.ContentLevel, snapshot.State.Options.Tone);
            var answer = await ai.CompleteAsync(AiRole.Actor, system, conversation,
                new AiCallContext(snapshot.Party.HostUserId, partyId, Purpose: "npc-answer"), maxOutputTokens: 400, ct: ct);
            var answered = await parties.ExecuteAsync(partyId, (_, now) => new CompleteNpcQuestion(now, id, answer), ct: ct);
            completed = true;
            if (answered.State.Ai.Voices) await VoiceAnswerAsync(answered, id, characterId, answer, ct);
        }
        catch when (!completed)
        {
            await parties.ExecuteAsync(partyId, (_, now) => new CancelNpcQuestion(now, id), ct: CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Speaks the answer in the NPC's voice. The text is already on every screen, so
    /// if this fails, the stage simply falls back to the browser's voice.
    /// </summary>
    private async Task VoiceAnswerAsync(PartySnapshot snapshot, Guid interrogationId, string characterId, string answer, CancellationToken ct)
    {
        try
        {
            var npc = snapshot.Scenario.FindCharacter(characterId)!;
            var assetId = await media.SpeechAsync(answer, ButlerDidIt.Ai.Media.VoiceCasting.For(npc.Id, npc.Voice),
                new AiCallContext(snapshot.Party.HostUserId, snapshot.Party.Id, Purpose: "npc-voice"), ct);
            await parties.ExecuteAsync(snapshot.Party.Id, (_, now) => new SetInterrogationAudio(now, interrogationId, ButlerDidIt.Api.Media.MediaStore.Url(assetId)), ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not voice the NPC answer {Id}", interrogationId);
        }
    }

    public async Task RequestHintAsync(Guid partyId, Guid seatId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var snapshot = await parties.ExecuteAsync(partyId, (_, now) => new BeginHint(now, id, seatId), ct: ct);
        try
        {
            var scenario = snapshot.Scenario;
            // Built from the player's own view, so the prompt holds nothing they can't already see (apart from the confidential solution).
            var view = ViewProjector.Player(snapshot.State, scenario, seatId, parties.Now);
            var system = InspectorPrompts.Hint(scenario, view, snapshot.Party.ContentLevel, snapshot.State.Options.Tone);
            var context = new AiCallContext(snapshot.Party.HostUserId, partyId, Purpose: "hint");
            var murderer = scenario.FindCharacter(scenario.Solution.MurdererId)!;
            var playerIsMurderer = view.Dossier?.Character.CharacterId == murderer.Id;

            var text = await ai.CompleteAsync(AiRole.Inspector, system, [new ChatMessage(ChatRole.User, "I'm stuck. Can I have a hint?")], context, 300, ct: ct);
            if (!playerIsMurderer && HintSafety.MentionsCharacter(text, murderer))
            {
                // Belt and braces: the model named the killer despite instructions. Ask once more, then fall back.
                text = await ai.CompleteAsync(AiRole.Inspector, system,
                    [new ChatMessage(ChatRole.User, "I'm stuck. Give me a hint that does NOT mention any suspect by name.")], context, 300, ct: ct);
                if (HintSafety.MentionsCharacter(text, murderer)) text = InspectorPrompts.HintFallback;
            }
            await parties.ExecuteAsync(partyId, (_, now) => new CompleteHint(now, id, text), ct: ct);
        }
        catch
        {
            await parties.ExecuteAsync(partyId, (_, now) => new CancelHint(now, id), ct: CancellationToken.None);
            throw;
        }
    }

    /// <summary>Called when the reveal starts. Verdicts are written in the background so the host never waits.</summary>
    public void QueueVerdicts(PartySnapshot snapshot)
    {
        if (snapshot.State is { Phase: Phase.Reveal, RevealStep: 0, Ai.Verdicts: true } && snapshot.State.Verdicts.Count == 0)
            verdicts.Enqueue(snapshot.Party.Id);
    }

    public async Task GenerateVerdictsAsync(Guid partyId, CancellationToken ct)
    {
        var snapshot = await parties.LoadAsync(partyId, ct);
        if (!snapshot.State.Ai.Verdicts || snapshot.State.Verdicts.Count > 0 || snapshot.State.Players.Count == 0) return;

        var (system, seats) = InspectorPrompts.Verdicts(snapshot.Scenario, snapshot.State, snapshot.Party.ContentLevel, snapshot.State.Options.Tone);
        var reply = await ai.CompleteJsonAsync<InspectorPrompts.VerdictReply>(AiRole.Inspector, system,
            [new ChatMessage(ChatRole.User, "Deliver your verdicts.")], new AiCallContext(snapshot.Party.HostUserId, partyId, Purpose: "verdicts"), 2_000, ct);

        var map = reply.Verdicts
            .Where(v => v.Player >= 1 && v.Player <= seats.Count && !string.IsNullOrWhiteSpace(v.Text))
            .GroupBy(v => seats[v.Player - 1])
            .ToDictionary(g => g.Key, g => g.First().Text);
        if (map.Count == 0) return;

        await parties.ExecuteAsync(partyId, (_, now) => new SetVerdicts(now, map), ct: ct);
        log.LogInformation("Verdicts ready for party {PartyId}", partyId);
    }
}

/// <summary>A small in-memory queue of parties waiting for verdicts, drained by <see cref="VerdictWorker"/>.</summary>
public sealed class VerdictQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public void Enqueue(Guid partyId) => _channel.Writer.TryWrite(partyId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class VerdictWorker(VerdictQueue queue, IServiceScopeFactory scopes, ILogger<VerdictWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var partyId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AiGameService>().GenerateVerdictsAsync(partyId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Verdicts are a bonus: if they fail, the reveal simply shows without them.
                log.LogWarning(ex, "Could not generate verdicts for party {PartyId}", partyId);
            }
        }
    }
}
