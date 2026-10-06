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

    /// <summary>Search a spot in the scene.</summary>
    public async Task EscapeExamine(string objectId)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new ExamineSpot(now, seatId, objectId));
    }

    /// <summary>Look closely at an item the group holds.</summary>
    public async Task EscapeInspect(string itemId)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new InspectItem(now, seatId, itemId));
    }

    /// <summary>Try two items together.</summary>
    public async Task EscapeCombine(string first, string second)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new CombineItems(now, seatId, first, second));
    }

    /// <summary>Press one light of a Switches puzzle.</summary>
    public async Task EscapePress(string puzzleId, int cell)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new PressSwitch(now, seatId, puzzleId, cell));
    }

    /// <summary>Take a puzzle to work on (#132): while puzzles go to people, only its holder answers it.</summary>
    public async Task EscapeTake(string puzzleId)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new TakePuzzle(now, seatId, puzzleId));
    }

    /// <summary>Hand a puzzle back to the table, for anyone to take.</summary>
    public async Task EscapeRelease(string puzzleId)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new ReleasePuzzle(now, seatId, puzzleId));
    }

    /// <summary>Pass a puzzle straight to someone else at the table.</summary>
    public async Task EscapePass(string puzzleId, Guid toSeatId)
    {
        var (seatId, partyId) = RequireSeat();
        await escape.ExecuteAsync(partyId, (_, now) => new PassPuzzle(now, seatId, puzzleId, toSeatId));
    }

    /// <summary>The host frees a puzzle from the TV, whoever holds it.</summary>
    public async Task EscapeHostFree(string code, string puzzleId)
    {
        var party = await RequireHostParty(code);
        await escape.ExecuteAsync(party.Id, (_, now) => new ReleasePuzzle(now, null, puzzleId));
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
