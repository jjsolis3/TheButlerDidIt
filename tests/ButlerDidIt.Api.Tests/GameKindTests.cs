using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Games;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// A stand-in second game, so the tests can prove the platform (joining, seats, the ticker,
/// live views) works for any kind of game, and that mystery actions refuse other kinds.
/// Its state is just the list of players and how many times it was ticked.
/// </summary>
public sealed class FakeEscapeModule : IGameModule
{
    public GameKind Kind => GameKind.EscapeRoom;

    public Task<GameSession> LoadAsync(AppDbContext db, Party party, CancellationToken ct) =>
        Task.FromResult<GameSession>(new FakeEscapeSession(JsonSerializer.Deserialize<FakeEscapeState>(party.State)!, party.ScenarioId));
}

public sealed record FakeEscapePlayer(Guid SeatId, string Name, string? Photo);
public sealed record FakeEscapeState(List<FakeEscapePlayer> Players, int Ticks);

public sealed class FakeEscapeSession(FakeEscapeState state, string room) : GameSession
{
    private FakeEscapeSession With(FakeEscapeState next) => new(next, room);

    public override IReadOnlyList<Guid> SeatIds => state.Players.Select(p => p.SeatId).ToList();
    public override PartyStatus Status => PartyStatus.Lobby;
    public override DateTimeOffset? NextDueAt => null;
    public override string ContentId => room;
    public override string Serialize() => JsonSerializer.Serialize(state);
    public override object StageView(DateTimeOffset now) => new { Room = room, Players = state.Players.Select(p => p.Name), state.Ticks };
    public override object PlayerView(Guid seatId, DateTimeOffset now) => new { Room = room, Me = state.Players.Single(p => p.SeatId == seatId).Name };
    public override GameSummary Describe(bool isHost) => new(room, "The Test Room", "test-escape", state.Players.Count, 6);
    public override string? PhotoUrl(Guid seatId) => state.Players.FirstOrDefault(p => p.SeatId == seatId)?.Photo;

    public override GameSession AddPlayer(DateTimeOffset now, Guid seatId, string name, bool isHost, bool isLocal) =>
        With(state with { Players = [.. state.Players, new FakeEscapePlayer(seatId, name, null)] });
    public override GameSession RemovePlayer(DateTimeOffset now, Guid seatId) =>
        With(state with { Players = state.Players.Where(p => p.SeatId != seatId).ToList() });
    public override GameSession SetPlayerPhoto(DateTimeOffset now, Guid seatId, string? url) =>
        With(state with { Players = state.Players.Select(p => p.SeatId == seatId ? p with { Photo = url } : p).ToList() });
    public override GameSession Tick(DateTimeOffset now) => With(state with { Ticks = state.Ticks + 1 });
}

/// <summary>The real app plus the stand-in escape-room module.</summary>
public sealed class FakeEscapeFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IGameModule, FakeEscapeModule>());
    }
}

public class GameKindTests(FakeEscapeFactory app) : IClassFixture<FakeEscapeFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> MysteryPartyAsync()
    {
        var (host, cookie) = await app.RegisterHostAsync($"kind{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null), GameJson.Options));
        return (host, cookie, party);
    }

    /// <summary>Until phase 2 can create one, an escape-room party is a mystery party turned into one in the database.</summary>
    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> EscapePartyAsync()
    {
        var (host, cookie, party) = await MysteryPartyAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Parties.Where(p => p.Code == party.Code).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Kind, GameKind.EscapeRoom)
            .SetProperty(p => p.ScenarioId, "test-room")
            .SetProperty(p => p.State, """{"Players":[],"Ticks":0}"""));
        return (host, cookie, party);
    }

    [Fact]
    public async Task Parties_are_mysteries_unless_said_otherwise()
    {
        var (_, _, party) = await MysteryPartyAsync();
        Assert.Equal(GameKind.Mystery, party.Kind);

        using var scope = app.Services.CreateScope();
        Assert.IsType<MysteryModule>(scope.ServiceProvider.GetRequiredService<GameModules>().For(GameKind.Mystery));
        Assert.Equal(GameKind.Mystery, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.SingleAsync(p => p.Code == party.Code)).Kind);
    }

    [Fact]
    public async Task Joining_seats_and_live_views_work_for_another_kind_of_game()
    {
        var (host, cookie, party) = await EscapePartyAsync();

        // The join page describes the party through its own game module.
        var info = await Read<PartyInfo>(await app.CreateClient().GetAsync($"/api/parties/{party.Code}"));
        Assert.Equal(GameKind.EscapeRoom, info.Kind);
        Assert.Equal("The Test Room", info.Title);

        await using var tv = await app.ConnectAsync(cookie: cookie);
        var seen = new List<JsonElement>();
        tv.On<JsonElement>("stage", v => { lock (seen) seen.Add(v); });
        var first = await tv.InvokeAsync<JsonElement>("WatchParty", party.Code);
        Assert.Equal("test-room", first.GetProperty("room").GetString());

        // A guest joins through the normal endpoint; their seat and phone view come from the escape module.
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        await using var phone = await app.ConnectAsync(seat.Token);
        Assert.Equal("Ada", (await phone.InvokeAsync<JsonElement>("JoinSeat")).GetProperty("me").GetString());
        for (var i = 0; i < 50 && !seen.Any(v => v.GetProperty("players").EnumerateArray().Any(p => p.GetString() == "Ada")); i++) await Task.Delay(100);
        Assert.Contains(seen, v => v.GetProperty("players").EnumerateArray().Any(p => p.GetString() == "Ada"));

        // The host can still remove a seat: that's the platform, not the mystery rules.
        await tv.InvokeAsync("RemoveSeat", party.Code, seat.SeatId);
        var after = await tv.InvokeAsync<JsonElement>("WatchParty", party.Code);
        Assert.Empty(after.GetProperty("players").EnumerateArray());
        _ = host;
    }

    [Fact]
    public async Task Mystery_actions_politely_refuse_another_kind_of_game()
    {
        var (_, cookie, party) = await EscapePartyAsync();
        await using var tv = await app.ConnectAsync(cookie: cookie);

        var ex = await Assert.ThrowsAsync<HubException>(() => tv.InvokeAsync("StartGame", party.Code));
        Assert.Contains("isn't part of this escape-room party", ex.Message);
        ex = await Assert.ThrowsAsync<HubException>(() => tv.InvokeAsync("Advance", party.Code));
        Assert.Contains("escape-room", ex.Message);
    }

    [Fact]
    public async Task The_ticker_ticks_any_kind_of_game()
    {
        var (_, cookie, party) = await EscapePartyAsync();
        using var scope = app.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.SingleAsync(p => p.Code == party.Code)).Id;
        await scope.ServiceProvider.GetRequiredService<ButlerDidIt.Api.Parties.PartyRuntime>().ExecuteAsync(id, (s, t) => s.Tick(t));

        await using var tv = await app.ConnectAsync(cookie: cookie);
        Assert.Equal(1, (await tv.InvokeAsync<JsonElement>("WatchParty", party.Code)).GetProperty("ticks").GetInt32());
    }
}

public class UnknownGameKindTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_kind_this_server_cannot_play_is_a_friendly_error_not_a_crash()
    {
        var (host, _) = await app.RegisterHostAsync($"unknown{Guid.NewGuid():N}@example.com");
        var res = await host.PostAsJsonAsync("/api/parties", new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null), GameJson.Options);
        var party = GameJson.Deserialize<PartyInfo>(await res.Content.ReadAsStringAsync());
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.Where(p => p.Code == party.Code)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Kind, (GameKind)99)); // a kind from a newer version of the app
        }

        var info = await app.CreateClient().GetAsync($"/api/parties/{party.Code}");
        Assert.Equal(HttpStatusCode.BadRequest, info.StatusCode);
        Assert.Contains("can't run 99 games yet", await info.Content.ReadAsStringAsync());
    }
}
