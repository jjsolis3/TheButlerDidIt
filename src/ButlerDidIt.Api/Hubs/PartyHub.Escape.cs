using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using Microsoft.AspNetCore.SignalR;

namespace ButlerDidIt.Api.Hubs;

/// <summary>
/// Escape-room actions. Like the mystery's, player actions use the caller's seat token and host
/// controls check the signed-in host. Each goes through <see cref="EscapeService"/>, which refuses
/// parties of another kind. Answers are checked on the server; the phone only learns whether it opened.
/// </summary>
public sealed partial class PartyHub
{
    /// <summary>The host starts the clock. Clue pieces are dealt to everyone's phones.</summary>
    public async Task EscapeStart(string code)
    {
        var party = await RequireHostParty(code);
        await escape.ExecuteAsync(party.Id, (_, now) => new StartEscape(now));
    }

    /// <summary>Try a code or a word. Returns true if it opened.</summary>
    public async Task<bool> EscapeAnswer(string puzzleId, string answer)
    {
        var (seatId, partyId) = RequireSeat();
        if (answer.Length > 100) throw new HubException("That answer is too long.");
        var session = await escape.ExecuteAsync(partyId, (_, now) => new SubmitAnswer(now, seatId, puzzleId, answer));
        return session.State.IsSolved(puzzleId);
    }

    /// <summary>Use the items a puzzle needs.</summary>
    public async Task EscapeUse(string puzzleId)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new UseItems(now, seatId, puzzleId));
    }

    /// <summary>A hint from a player's phone: written by the AI game master when the party has one.</summary>
    public async Task EscapeHint(string puzzleId)
    {
        var (seatId, partyId) = RequireSeat();
        await gameMaster.RequestHintAsync(partyId, seatId, puzzleId);
    }

    /// <summary>A hint the host asks for from the TV.</summary>
    public async Task EscapeHostHint(string code, string puzzleId)
    {
        var party = await RequireHostParty(code);
        await gameMaster.RequestHintAsync(party.Id, null, puzzleId);
    }
}
