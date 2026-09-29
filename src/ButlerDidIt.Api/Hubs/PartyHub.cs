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
public sealed class PartyHub(PartyService parties, PartyRuntime runtime, PartyDealer dealer, AppDbContext db, AiGameService aiGame) : Hub
{
    public static string StageGroup(Guid partyId) => $"stage:{partyId}";
    public static string SeatGroup(Guid seatId) => $"seat:{seatId}";
    public static string UserGroup(string userId) => $"user:{userId}";

    // ------------------------------------------------------------------ subscribe

    /// <summary>
    /// Start receiving the public stage view. Allowed for the host and any seated guest.
    /// Works for every kind of game: the view is whatever the party's game module projects.
    /// </summary>
    public async Task<object> WatchParty(string code)
    {
        var party = await parties.FindByCodeAsync(code) ?? throw new HubException("Party not found.");
        var isHost = IsHostOf(party);
        var isGuest = Context.User!.PartyId() == party.Id;
        if (!isHost && !isGuest) throw new HubException("You are not part of this party.");

        await Groups.AddToGroupAsync(Context.ConnectionId, StageGroup(party.Id));
        var (_, session) = await runtime.LoadAsync(party.Id);
        return session.StageView(runtime.Now);
    }

    /// <summary>Start receiving this seat's private view (the dossier).</summary>
    public async Task<object> JoinSeat()
    {
        var (seatId, partyId) = RequireSeat();
        await Groups.AddToGroupAsync(Context.ConnectionId, SeatGroup(seatId));
        await db.Seats.Where(s => s.Id == seatId).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, parties.Now));
        var (_, session) = await runtime.LoadAsync(partyId);
        return session.PlayerView(seatId, runtime.Now);
    }

    /// <summary>
    /// Host only: hear a "jobs" signal whenever one of your mystery or media jobs makes progress,
    /// so the page can refresh at once instead of polling. The signal carries no data; the page
    /// fetches the job through the normal, access-checked endpoint.
    /// </summary>
    public async Task WatchMyJobs()
    {
        var userId = Context.User!.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new HubException("Sign in first.");
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
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

    /// <summary>Challenge another character with a clue: it goes on the big screen and they get the floor.</summary>
    public Task Confront(string clueId, string suspectId) => AsSeat((seat, now) => new Confront(now, seat, clueId, suspectId));
    /// <summary>"Who looks guiltiest?" Only totals are ever shown.</summary>
    public Task SetSuspicion(string? characterId) => AsSeat((seat, now) => new SetSuspicion(now, seat, characterId));

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

    /// <summary>"Begin the evening". For a "Surprise me" party this also deals the story's version (see PartyDealer).</summary>
    public async Task StartGame(string code)
    {
        var party = await RequireHostParty(code);
        await dealer.StartAsync(party.Id);
    }

    /// <summary>The host doesn't want to wait for the AI's version: start now with a hand-written one.</summary>
    public async Task SkipTailoring(string code)
    {
        var party = await RequireHostParty(code);
        await dealer.StartWithoutTailoringAsync(party.Id);
    }
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
    /// <summary>Give the floor to a character: a guest's, or one the narrator plays. Null clears it.</summary>
    public Task Spotlight(string code, string? characterId) => AsHost(code, (_, now) => new SetSpotlight(now, characterId));
    /// <summary>"🎲 Spin": the floor goes to someone who hasn't spoken in this scene.</summary>
    public Task SpinSpotlight(string code) => AsHost(code, (_, now) => new SpinSpotlight(now));
    public Task ConvertToNpc(string code, Guid seatId) => AsHost(code, (_, now) => new ConvertToNpc(now, seatId));

    /// <summary>Every kind of game has seats, so this goes through the runtime rather than the mystery rules.</summary>
    public async Task RemoveSeat(string code, Guid seatId)
    {
        var party = await RequireHostParty(code);
        // Removing the Seat row in the same SaveChanges as the new state means the
        // token stops working at exactly the moment the player leaves the game.
        await runtime.ExecuteAsync(party.Id, (s, now) => s.RemovePlayer(now, seatId), beforeSave: (p, _) =>
        {
            var seat = db.Seats.Local.FirstOrDefault(x => x.Id == seatId)
                ?? db.Seats.FirstOrDefault(x => x.Id == seatId && x.PartyId == p.Id);
            if (seat is not null) db.Seats.Remove(seat);
        });
        await runtime.NotifySeatRemovedAsync(seatId);
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
        var party = await RequireHostParty(code);
        return await parties.ExecuteAsync(party.Id, make, beforeSave);
    }

    private async Task<Party> RequireHostParty(string code)
    {
        var party = await parties.FindByCodeAsync(code) ?? throw new HubException("Party not found.");
        if (!IsHostOf(party)) throw new HubException("Only the host can do that.");
        return party;
    }

    private bool IsHostOf(Party party) =>
        Context.User!.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId && userId == party.HostUserId;
}

/// <summary>Tells a host's open pages that one of their background jobs changed (see <see cref="PartyHub.WatchMyJobs"/>).</summary>
public sealed class JobEvents(IHubContext<PartyHub> hub, ILogger<JobEvents> log)
{
    public async Task ChangedAsync(string hostUserId)
    {
        try
        {
            await hub.Clients.Group(PartyHub.UserGroup(hostUserId)).SendAsync("jobs");
        }
        catch (Exception ex)
        {
            // Pages fall back to polling, so a lost signal only makes them a little slower.
            log.LogDebug(ex, "Could not signal job progress");
        }
    }
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
