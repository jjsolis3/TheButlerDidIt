using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

public class EscapeFlowTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> EscapePartyAsync(string room)
    {
        var (host, cookie) = await app.RegisterHostAsync($"escape{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(room, PartyMode.SharedScreen), GameJson.Options));
        return (host, cookie, party);
    }

    [Fact]
    public async Task The_shelf_lists_both_rooms_without_giving_anything_away()
    {
        var json = await app.CreateClient().GetStringAsync("/api/escape-rooms");
        var rooms = GameJson.Deserialize<List<EscapeRoomSummary>>(json);
        Assert.Contains(rooms, r => r.Id == "the-workshop");
        Assert.Contains(rooms, r => r.Id == "the-funhouse");
        Assert.DoesNotContain("3728", json);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Three_phones_escape_the_workshop_together()
    {
        var (_, cookie, party) = await EscapePartyAsync("the-workshop");
        Assert.Equal(GameKind.EscapeRoom, party.Kind);
        Assert.Equal("The Workshop", party.Title);

        var seats = new List<SeatResponse>();
        foreach (var name in new[] { "Ada", "Ben", "Cy" })
            seats.Add(await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name))));

        await using var tv = await app.ConnectAsync(cookie: cookie);
        var phones = new List<HubConnection>();
        foreach (var seat in seats) phones.Add(await app.ConnectAsync(seat.Token));
        try
        {
            // A mystery action can't run in an escape room.
            await Assert.ThrowsAsync<HubException>(() => tv.InvokeAsync("StartGame", party.Code));
            await tv.InvokeAsync("EscapeStart", party.Code);

            // Every phone holds some clue pieces, and together they cover the toolbox code.
            var stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
            Assert.Equal(EscapePhase.Playing, stage.Phase);
            Assert.Equal("The Chains", stage.Stage!.Title);

            // Wrong answers don't open anything and show on the TV.
            Assert.False(await phones[0].InvokeAsync<bool>("EscapeAnswer", "tape", "kettle"));
            Assert.Contains((await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).Feed, f => f.Text.Contains("kettle"));
            await Task.Delay(EscapeEngine.WrongAnswerCooldown); // the lock resets

            var room = app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!;
            var i = 0;
            while (true)
            {
                stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
                if (stage.Phase != EscapePhase.Playing) break;
                var next = stage.Puzzles.First(p => !p.Solved && p.Needs.Count == 0);
                var phone = phones[i++ % phones.Count];
                if (next.Kind == PuzzleKind.Use) await phone.InvokeAsync("EscapeUse", next.Id);
                else Assert.True(await phone.InvokeAsync<bool>("EscapeAnswer", next.Id, room.FindPuzzle(next.Id)!.Answers[0]));
            }

            Assert.Equal(EscapePhase.Escaped, stage.Phase);
            Assert.Equal(room.EscapedText, stage.EndText);
            Assert.Equal(room.Puzzles.Count, stage.SolvedCount);

            using var scope = app.Services.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().SingleAsync(p => p.Code == party.Code);
            Assert.Equal(PartyStatus.Finished, row.Status);
            Assert.Null(row.NextDueAt);
        }
        finally
        {
            foreach (var phone in phones) await phone.DisposeAsync();
        }
    }

    [Fact]
    public async Task Each_phone_sees_only_its_own_clue_pieces_and_nobody_joins_mid_game()
    {
        var (_, cookie, party) = await EscapePartyAsync("the-funhouse");
        var ada = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        var ben = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ben")));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await tv.InvokeAsync("EscapeStart", party.Code);

        await using var adaPhone = await app.ConnectAsync(ada.Token);
        await using var benPhone = await app.ConnectAsync(ben.Token);
        var adaView = await adaPhone.InvokeAsync<EscapePlayerView>("JoinSeat");
        var benView = await benPhone.InvokeAsync<EscapePlayerView>("JoinSeat");
        Assert.Equal("Ada", adaView.Name);
        Assert.Empty(adaView.Pieces.Select(p => p.Text).Intersect(benView.Pieces.Select(p => p.Text)));

        var late = await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Late"));
        Assert.False(late.IsSuccessStatusCode);

        // A hint from the TV costs time for everyone.
        var before = (await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).Deadline;
        await tv.InvokeAsync("EscapeHostHint", party.Code, "mirror-riddle");
        var after = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(before!.Value.AddSeconds(-after.HintPenaltySeconds), after.Deadline);
        Assert.Single(after.Puzzles.Single(p => p.Id == "mirror-riddle").Hints);
    }

    [Fact]
    public async Task Escape_actions_refuse_a_mystery_party()
    {
        var (host, cookie) = await app.RegisterHostAsync($"mystery{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null), GameJson.Options));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        var ex = await Assert.ThrowsAsync<HubException>(() => tv.InvokeAsync("EscapeStart", party.Code));
        Assert.Contains("isn't part of this party's game", ex.Message);
    }
}
