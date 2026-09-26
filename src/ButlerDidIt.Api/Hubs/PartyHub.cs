using System.Security.Claims;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ButlerDidIt.Api.Hubs;

/// <summary>
/// The real-time channel between the server and every screen at a party.
///
/// SignalR "groups" are like chat rooms: the stage screen joins stage:{party}
/// and each phone joins seat:{seat}. The server pushes a fresh view to a group
/// whenever the game changes (see PartyService.BroadcastAsync).
///
/// Methods fall into two kinds:
///   * Player actions use the caller's seat token, so a phone can only act as itself.
///   * Host controls take the party code and check the signed-in user owns the party.
/// </summary>
[Authorize(Policy = AuthPolicies.PartyMember)]
public sealed class PartyHub(PartyService parties, AppDbContext db, AiGameService aiGame) : Hub
{
    public static string StageGroup(Guid partyId) => $"stage:{partyId}";
    public static string SeatGroup(Guid seatId) => $"seat:{seatId}";

    // ------------------------------------------------------------------ subscribe

    /// <summary>Start receiving the public stage view. Allowed for the host and any seated guest.</summary>
    public async Task<StageView> WatchParty(string code)
    {
        var party = await parties.FindByCodeAsync(code) ?? throw new HubException("Party not found.");
        var isHost = IsHostOf(party);
        var isGuest = Context.User!.PartyId() == party.Id;
        if (!isHost && !isGuest) throw new HubException("You are not part of this party.");

        await Groups.AddToGroupAsync(Context.ConnectionId, StageGroup(party.Id));
        var s = await parties.LoadAsync(party.Id);
        return ViewProjector.Stage(s.State, s.Scenario, parties.Now);
    }

    /// <summary>Start receiving this seat's private view (the dossier).</summary>
    public async Task<PlayerView> JoinSeat()
    {
        var (seatId, partyId) = RequireSeat();
        await Groups.AddToGroupAsync(Context.ConnectionId, SeatGroup(seatId));
        await db.Seats.Where(s => s.Id == seatId).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, parties.Now));
        var s = await parties.LoadAsync(partyId);
        return ViewProjector.Player(s.State, s.Scenario, seatId, parties.Now);
    }

    // ------------------------------------------------------------------ player actions

    public Task ChooseCharacter(string? characterId) => AsSeat((seat, now) => new ChooseCharacter(now, seat, characterId));
    public Task SetReady(bool ready) => AsSeat((seat, now) => new SetReady(now, seat, ready));
    public Task RevealSecret(string secretId) => AsSeat((seat, now) => new RevealSecret(now, seat, secretId));
    public Task ShareClue(string clueId) => AsSeat((seat, now) => new ShareClue(now, seat, clueId));
    public Task SolvePuzzle(string clueId, string answer) => AsSeat((seat, now) => new SolvePuzzle(now, seat, clueId, answer));

    public Task SubmitAccusation(string suspectId, string motiveId, string methodId) =>
        AsSeat((seat, now) => new SubmitAccusation(now, seat, suspectId, motiveId, methodId));

    /// <summary>Question a character nobody is playing. The answer arrives for everyone through the normal stage update.</summary>
    public Task AskNpc(string characterId, string question)
    {
        var (seatId, partyId) = RequireSeat();
        return aiGame.AskNpcAsync(partyId, seatId, characterId, question, Context.ConnectionAborted);
    }

    /// <summary>Ask the Inspector for a private hint. It appears only on this seat's phone.</summary>
    public Task RequestHint()
    {
        var (seatId, partyId) = RequireSeat();
        return aiGame.RequestHintAsync(partyId, seatId, Context.ConnectionAborted);
    }

    public Task CastAwardVote(string awardId, Guid nomineeSeatId) =>
        AsSeat((seat, now) => new CastAwardVote(now, seat, awardId, nomineeSeatId));

    public async Task<string> GetNotes()
    {
        var (seatId, _) = RequireSeat();
        return (await db.PlayerNotes.FindAsync(seatId))?.Text ?? "";
    }

    public async Task SaveNotes(string text)
    {
        var (seatId, _) = RequireSeat();
        if (text.Length > 10_000) throw new HubException("Notes are limited to 10,000 characters.");
        var note = await db.PlayerNotes.FindAsync(seatId);
        if (note is null)
        {
            note = new PlayerNote { SeatId = seatId };
            db.PlayerNotes.Add(note);
        }
        note.Text = text;
        note.UpdatedAt = parties.Now;
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ host controls

    public Task StartGame(string code) => AsHost(code, (_, now) => new StartGame(now));
    public async Task Advance(string code)
    {
        var snapshot = await AsHost(code, (_, now) => new Advance(now));
        // Entering the reveal: start writing the Inspector's verdicts in the background.
        aiGame.QueueVerdicts(snapshot);
    }
    public Task AutoAssign(string code) => AsHost(code, (_, now) => new AutoAssignCharacters(now));
    public Task DropNextClue(string code) => AsHost(code, (_, now) => new DropNextClue(now));
    public Task PauseTimer(string code) => AsHost(code, (_, now) => new PauseTimer(now));
    public Task ResumeTimer(string code) => AsHost(code, (_, now) => new ResumeTimer(now));
    public Task ExtendTimer(string code, int minutes) => AsHost(code, (_, now) => new ExtendTimer(now, minutes));
    public Task AssignCharacter(string code, Guid seatId, string? characterId) => AsHost(code, (_, now) => new ChooseCharacter(now, seatId, characterId));
    public Task Spotlight(string code, Guid? seatId) => AsHost(code, (_, now) => new SetSpotlight(now, seatId));
    public Task ConvertToNpc(string code, Guid seatId) => AsHost(code, (_, now) => new ConvertToNpc(now, seatId));

    public async Task RemoveSeat(string code, Guid seatId)
    {
        // Removing the Seat row in the same SaveChanges as the new state means the
        // token stops working at exactly the moment the player leaves the game.
        await AsHost(code, (_, now) => new RemovePlayer(now, seatId), beforeSave: s =>
        {
            var seat = db.Seats.Local.FirstOrDefault(x => x.Id == seatId)
                ?? db.Seats.FirstOrDefault(x => x.Id == seatId && x.PartyId == s.Party.Id);
            if (seat is not null) db.Seats.Remove(seat);
        });
        await parties.NotifySeatRemovedAsync(seatId);
    }

    // ------------------------------------------------------------------ helpers

    private (Guid SeatId, Guid PartyId) RequireSeat()
    {
        var user = Context.User!;
        if (user.SeatId() is { } seat && user.PartyId() is { } party) return (seat, party);
        throw new HubException("Join the party first.");
    }

    private async Task AsSeat(Func<Guid, DateTimeOffset, Command> make)
    {
        var (seatId, partyId) = RequireSeat();
        await parties.ExecuteAsync(partyId, (_, now) => make(seatId, now));
    }

    private async Task<PartySnapshot> AsHost(string code, Func<PartySnapshot, DateTimeOffset, Command> make, Action<PartySnapshot>? beforeSave = null)
    {
        var party = await parties.FindByCodeAsync(code) ?? throw new HubException("Party not found.");
        if (!IsHostOf(party)) throw new HubException("Only the host can do that.");
        return await parties.ExecuteAsync(party.Id, make, beforeSave);
    }

    private bool IsHostOf(Party party) =>
        Context.User!.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId && userId == party.HostUserId;
}

/// <summary>
/// Turns game rule errors into HubExceptions. SignalR hides ordinary exception
/// messages from clients for security, but HubException messages are passed
/// through, so players see "Pick a motive." instead of a generic error.
/// </summary>
public sealed class GameRuleHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(context);
        }
        catch (GameRuleException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (AiException ex)
        {
            // Budget reached, AI not configured, provider down: all have player-friendly messages.
            throw new HubException(ex.Message);
        }
    }
}
