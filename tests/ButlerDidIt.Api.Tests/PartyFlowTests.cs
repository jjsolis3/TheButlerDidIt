using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ButlerDidIt.Api.Tests;

public class PartyFlowTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private const string Scenario = "death-at-blackwood-manor";

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> CreatePartyAsync()
    {
        var (host, cookie) = await app.RegisterHostAsync($"host{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(Scenario, PartyMode.SharedScreen, null), GameJson.Options));
        return (host, cookie, party);
    }

    private async Task<SeatResponse> JoinAsync(string code, string name) =>
        await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/join", new JoinRequest(name)));

    [Fact]
    public async Task Themes_endpoint_never_includes_scenario_secrets()
    {
        var json = await app.CreateClient().GetStringAsync("/api/themes");
        Assert.Contains("Death at Blackwood Manor", json);
        Assert.DoesNotContain("murdererId", json);
        Assert.DoesNotContain("YOU ARE THE MURDERER", json);
        Assert.DoesNotContain("explanation", json);
    }

    [Fact]
    public async Task Scenario_files_cannot_be_downloaded_through_the_media_route()
    {
        var res = await app.CreateClient().GetAsync("/media/themes/the-butler-did-it/..%2Fscenarios%2Fdeath-at-blackwood-manor.json");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Full_party_over_signalr_keeps_secrets_private()
    {
        var (_, cookie, party) = await CreatePartyAsync();
        var alice = await JoinAsync(party.Code, "Alice");
        var bob = await JoinAsync(party.Code, "Bob");
        var cara = await JoinAsync(party.Code, "Cara");

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await using var aliceHub = await app.ConnectAsync(alice.Token);
        await using var bobHub = await app.ConnectAsync(bob.Token);
        await using var caraHub = await app.ConnectAsync(cara.Token);

        var lobby = await stage.InvokeAsync<StageView>("WatchParty", party.Code.ToLowerInvariant());
        Assert.Equal(3, lobby.Players.Count);
        Assert.Equal(8, lobby.Cast.Count); // everyone sees all characters in the lobby

        await aliceHub.InvokeAsync("ChooseCharacter", "finch");
        await bobHub.InvokeAsync("ChooseCharacter", "hargrove");
        var clash = await Assert.ThrowsAsync<HubException>(() => caraHub.InvokeAsync("ChooseCharacter", "finch"));
        Assert.Contains("Alice", clash.Message);

        // Guests can't use host controls.
        var denied = await Assert.ThrowsAsync<HubException>(() => bobHub.InvokeAsync("StartGame", party.Code));
        Assert.Contains("Only the host", denied.Message);

        // Listen for pushed updates before starting.
        var bobUpdates = new List<PlayerView>();
        bobHub.On<PlayerView>("player", v => { lock (bobUpdates) bobUpdates.Add(v); });

        await stage.InvokeAsync("StartGame", party.Code);

        var aliceView = await aliceHub.InvokeAsync<PlayerView>("JoinSeat");
        var bobView = await bobHub.InvokeAsync<PlayerView>("JoinSeat");
        Assert.Equal(Phase.CastReveal, aliceView.Stage.Phase);
        Assert.True(aliceView.Dossier!.IsMurderer);
        Assert.False(bobView.Dossier!.IsMurderer);

        var bobJson = GameJson.Serialize(bobView);
        Assert.DoesNotContain("YOU ARE THE MURDERER", bobJson);
        Assert.Contains("half-brother", bobJson); // his own secret

        var stageJson = GameJson.Serialize(await stage.InvokeAsync<StageView>("WatchParty", party.Code));
        Assert.DoesNotContain("YOU ARE THE MURDERER", stageJson);
        Assert.DoesNotContain("half-brother", stageJson);

        // Cara was auto-assigned one of the two remaining required characters
        // (Evelyn or Violet); the other becomes an NPC.
        var cast = (await stage.InvokeAsync<StageView>("WatchParty", party.Code)).Cast;
        Assert.Single(cast, c => c.IsNpc && c.CharacterId is "evelyn" or "violet");
        Assert.Equal(4, cast.Count);

        // Walk to the first mingle, where clues drop.
        await stage.InvokeAsync("Advance", party.Code); // prologue
        await stage.InvokeAsync("Advance", party.Code); // act 1 cinematic
        await stage.InvokeAsync("Advance", party.Code); // act 1 mingle
        var mingle = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.Equal(ActStep.Mingle, mingle.ActStep);
        Assert.NotNull(mingle.Timer!.EndsAt);
        Assert.Contains(mingle.Clues, c => c.Id == "candlestick");

        // Pushed updates reached Bob's phone without him asking.
        await WaitUntil(() => { lock (bobUpdates) return bobUpdates.Any(v => v.Stage.ActStep == ActStep.Mingle); });

        // Reconnect: a new connection with the same token lands in the same seat.
        await using var bobAgain = await app.ConnectAsync(bob.Token);
        var reconnected = await bobAgain.InvokeAsync<PlayerView>("JoinSeat");
        Assert.Equal(bob.SeatId, reconnected.SeatId);
        Assert.Equal("hargrove", reconnected.Dossier!.Character.CharacterId);

        // Notes are private per seat.
        await bobHub.InvokeAsync("SaveNotes", "The doctor seems nervous.");
        Assert.Equal("The doctor seems nervous.", await bobAgain.InvokeAsync<string>("GetNotes"));
        Assert.Equal("", await caraHub.InvokeAsync<string>("GetNotes"));
    }

    [Fact]
    public async Task Joining_after_the_game_starts_is_refused()
    {
        var (_, cookie, party) = await CreatePartyAsync();
        await JoinAsync(party.Code, "Alice");
        await JoinAsync(party.Code, "Bob");
        await JoinAsync(party.Code, "Cara");
        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);

        var res = await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Late"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_seat_token_only_works_for_its_own_party()
    {
        var (_, _, partyA) = await CreatePartyAsync();
        var (_, _, partyB) = await CreatePartyAsync();
        var guest = await JoinAsync(partyA.Code, "Alice");

        await using var hub = await app.ConnectAsync(guest.Token);
        await hub.InvokeAsync<StageView>("WatchParty", partyA.Code);
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<StageView>("WatchParty", partyB.Code));
    }

    [Fact]
    public async Task Host_can_add_pass_and_play_seats_and_remove_guests()
    {
        var (host, cookie, party) = await CreatePartyAsync();
        var local = await Read<SeatResponse>(await host.PostAsJsonAsync($"/api/parties/{party.Code}/seats", new AddSeatRequest("Grandma", IsLocal: true)));
        var guest = await JoinAsync(party.Code, "Bob");

        await using var stage = await app.ConnectAsync(cookie: cookie);
        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.Contains(view.Players, p => p.Name == "Grandma" && p.IsLocal);

        await stage.InvokeAsync("RemoveSeat", party.Code, guest.SeatId);
        view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.DoesNotContain(view.Players, p => p.Name == "Bob");

        // The removed guest's token no longer authenticates.
        await Assert.ThrowsAnyAsync<Exception>(() => app.ConnectAsync(guest.Token));
        await using var grandma = await app.ConnectAsync(local.Token);
        Assert.Equal("Grandma", (await grandma.InvokeAsync<PlayerView>("JoinSeat")).Name);
    }

    [Fact]
    public async Task Only_hosts_can_create_parties()
    {
        var res = await app.CreateClient().PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(Scenario, PartyMode.SharedScreen, null), GameJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task The_content_level_is_the_mysterys_own_rating_and_the_tone_is_the_hosts_choice()
    {
        var (host, cookie) = await app.RegisterHostAsync($"fam{Guid.NewGuid():N}@example.com");
        async Task<(PartyInfo Info, StageView Stage)> Create(string scenario, Tone tone)
        {
            var res = await host.PostAsJsonAsync("/api/parties",
                new CreatePartyRequest(scenario, PartyMode.SharedScreen, null, UseAi: false, DrinkingPrompts: true, Tone: tone), GameJson.Options);
            var info = GameJson.Deserialize<PartyInfo>(await res.Content.ReadAsStringAsync());
            await using var stage = await app.ConnectAsync(cookie: cookie);
            return (info, await stage.InvokeAsync<StageView>("WatchParty", info.Code));
        }

        var adults = await Create(Scenario, Tone.Clean);
        Assert.Equal(ContentRating.Mature, adults.Info.ContentLevel);
        Assert.Equal(Tone.Clean, adults.Stage.Options.Tone);
        Assert.True(adults.Stage.Options.DrinkingPrompts);

        // A Family mystery is always a Family party: toasts stay off even if asked for.
        var family = await Create("the-captains-last-cocoa", Tone.Playful);
        Assert.Equal(ContentRating.Family, family.Info.ContentLevel);
        Assert.Equal(Tone.Playful, family.Stage.Options.Tone);
        Assert.False(family.Stage.Options.DrinkingPrompts);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), "Timed out waiting for a pushed update.");
    }
}

public class RegistrationTests(ClosedRegistrationFactory app) : IClassFixture<ClosedRegistrationFactory>
{
    [Fact]
    public async Task First_user_is_admin_and_registration_can_be_closed()
    {
        var first = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest("owner@example.com", "password123", "Owner"));
        var me = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        Assert.True(me.GetProperty("isAdmin").GetBoolean());

        var second = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest("other@example.com", "password123", "Other"));
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }
}
