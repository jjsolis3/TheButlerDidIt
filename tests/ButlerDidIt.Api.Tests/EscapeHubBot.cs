using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Escape.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace ButlerDidIt.Api.Tests;

/// <summary>Plays an escape room through the real hub: <see cref="EscapeBot"/> picks each move, a phone sends it.</summary>
public static class EscapeHubBot
{
    /// <param name="stateOf">Reads the party's saved state (the bot plans from what the server holds).</param>
    public static async Task PlayToEndAsync(IReadOnlyList<HubConnection> phones, IReadOnlyList<Guid> seats, EscapeRoom template, Func<EscapeState> stateOf)
    {
        for (var move = 0; ; move++)
        {
            if (move >= 1000) throw new InvalidOperationException("The game should end.");
            var s = stateOf();
            if (s.Phase != EscapePhase.Playing) return;
            var i = move % phones.Count;
            await SendAsync(phones[i], EscapeBot.NextMove(s, EscapeEngine.RoomFor(s, template), seats[i], DateTimeOffset.UtcNow));
        }
    }

    /// <summary>A move as the phone's hub call. Answers must open (the bot only ever gives the right one).</summary>
    public static async Task SendAsync(HubConnection phone, EscapeCommand move)
    {
        switch (move)
        {
            case ExamineSpot c: await phone.InvokeAsync("EscapeExamine", c.ObjectId); break;
            case InspectItem c: await phone.InvokeAsync("EscapeInspect", c.ItemId); break;
            case CombineItems c: await phone.InvokeAsync("EscapeCombine", c.First, c.Second); break;
            case PressSwitch c: await phone.InvokeAsync("EscapePress", c.PuzzleId, c.Cell); break;
            case UseItems c: await phone.InvokeAsync("EscapeUse", c.PuzzleId); break;
            case SubmitAnswer c: Assert.True(await phone.InvokeAsync<bool>("EscapeAnswer", c.PuzzleId, c.Answer)); break;
            default: throw new InvalidOperationException($"No hub call for {move.GetType().Name}.");
        }
    }
}
