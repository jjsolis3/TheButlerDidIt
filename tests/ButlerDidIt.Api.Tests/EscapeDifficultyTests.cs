using System.Net;
using System.Net.Http.Json;
using System.Text;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// Difficulties and the newer puzzles through the real app: a solo player escapes the Laboratory (a test room
/// with every kind of puzzle, saved as this host's own room) on Hard, using only the hub, and lands on Hard's board.
/// </summary>
public class EscapeDifficultyTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static EscapeRoom LoadLab() =>
        GameJson.Deserialize<EscapeRoom>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "the-laboratory.json")));

    /// <summary>A host with the Laboratory on their shelf, under an id of its own (rooms are stored once per id).</summary>
    private async Task<(HttpClient Host, string Cookie, string RoomId)> HostWithLabAsync()
    {
        var email = $"lab{Guid.NewGuid():N}@example.com";
        var (host, cookie) = await app.RegisterHostAsync(email);
        var roomId = $"lab-{Guid.NewGuid():N}"[..20];
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lab = LoadLab() with { Id = roomId };
        db.EscapeRooms.Add(new EscapeRoomEntity
        {
            Id = roomId, OwnerUserId = db.Users.Single(u => u.Email == email).Id, Title = lab.Title, ContentRating = lab.ContentRating,
            Document = GameJson.Serialize(lab), CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (host, cookie, roomId);
    }

    private EscapeState StateOf(string code)
    {
        using var scope = app.Services.CreateScope();
        return GameJson.Deserialize<EscapeState>(scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == code).State);
    }

    [Fact]
    public async Task A_solo_player_escapes_on_hard_through_the_hub_and_lands_on_hards_board()
    {
        var (host, cookie, roomId) = await HostWithLabAsync();
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest(roomId, PartyMode.SharedScreen, Difficulty: EscapeDifficulty.Hard), GameJson.Options));
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await using var phone = await app.ConnectAsync(seat.Token);
        await tv.InvokeAsync("EscapeStart", party.Code);

        var first = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(EscapeDifficulty.Hard, first.Difficulty);
        Assert.NotNull(first.Scene);
        Assert.All(first.Scene!.Objects, o => Assert.Null(o.Look)); // nothing searched yet
        Assert.Contains(first.Puzzles, p => p.Kind == PuzzleKind.Search && p.Finds is { Found: 0, Total: 3 });
        var template = LoadLab() with { Id = roomId };

        await EscapeHubBot.PlayToEndAsync([phone], [seat.SeatId], template, () => StateOf(party.Code));

        var end = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(EscapePhase.Escaped, end.Phase);
        Assert.Contains(end.Notebook, n => n.Source == "Poster");
        using (var scope = app.Services.CreateScope())
        {
            var partyId = scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == party.Code).Id;
            var result = scope.ServiceProvider.GetRequiredService<AppDbContext>().EscapeResults.AsNoTracking().Single(r => r.PartyId == partyId);
            Assert.Equal(EscapeDifficulty.Hard, result.Difficulty);
        }

        var hard = await Read<Leaderboard>(await host.GetAsync($"/api/escape-rooms/{roomId}/leaderboard?difficulty=hard&party={party.Code}"));
        Assert.Equal(EscapeDifficulty.Hard, hard.Difficulty);
        Assert.NotNull(hard.ThisParty);
        var normal = await Read<Leaderboard>(await host.GetAsync($"/api/escape-rooms/{roomId}/leaderboard?party={party.Code}"));
        Assert.Equal(EscapeDifficulty.Normal, normal.Difficulty);
        Assert.Null(normal.ThisParty); // each difficulty is ranked on its own
        Assert.Empty(normal.Top);
    }

    [Fact]
    public async Task Hub_rule_breaks_come_back_as_readable_errors()
    {
        var (host, cookie, roomId) = await HostWithLabAsync();
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(roomId, PartyMode.SharedScreen), GameJson.Options));
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await using var phone = await app.ConnectAsync(seat.Token);
        await tv.InvokeAsync("EscapeStart", party.Code);

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => phone.InvokeAsync("EscapeExamine", "poster"));
        Assert.Contains("different light", ex.Message);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => phone.InvokeAsync("EscapeInspect", "locket"));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => phone.InvokeAsync("EscapePress", "lights", 0)); // the next stage's
        await phone.InvokeAsync("EscapeExamine", "crate");
        Assert.Contains("bulb", StateOf(party.Code).Inventory);
    }

    [Fact]
    public async Task Difficulties_other_than_easy_normal_and_hard_are_refused()
    {
        var (host, _) = await app.RegisterHostAsync($"bad{Guid.NewGuid():N}@example.com");
        foreach (var difficulty in new[] { "\"extreme\"", "7" })
        {
            var body = new StringContent($$"""{"roomId":"the-workshop","mode":"sharedScreen","difficulty":{{difficulty}}}""", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync("/api/parties/escape", body)).StatusCode);
        }
        foreach (var difficulty in new[] { "extreme", "7" })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync($"/api/escape-rooms/the-workshop/leaderboard?difficulty={difficulty}")).StatusCode);

        // Any casing reads, as with other query values.
        var easy = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, Difficulty: EscapeDifficulty.Easy), GameJson.Options));
        Assert.Equal(EscapeDifficulty.Easy, StateOf(easy.Code).Difficulty);
        var board = await Read<Leaderboard>(await host.GetAsync("/api/escape-rooms/the-workshop/leaderboard?difficulty=Easy"));
        Assert.Equal(EscapeDifficulty.Easy, board.Difficulty);
    }
}
