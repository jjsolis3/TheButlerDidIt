using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Hubs;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ButlerDidIt.Api.Tests;

/// <summary>Spectator mode (#112): watching the TV on your own phone, cheering, and the host's controls.</summary>
public class SpectatorTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static async Task<T> Within<T>(TaskCompletionSource<T> source, int seconds = 10) =>
        await source.Task.WaitAsync(TimeSpan.FromSeconds(seconds));

    private static Task Within(TaskCompletionSource source, int seconds = 10) => source.Task.WaitAsync(TimeSpan.FromSeconds(seconds));

    /// <summary>A workshop party with one guest, and the host's TV watching it.</summary>
    private async Task<(HttpClient Host, PartyInfo Party, HubConnection Tv, SeatResponse Guest)> EscapePartyAsync()
    {
        var (host, cookie) = await app.RegisterHostAsync($"watch{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        var guest = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        var tv = await app.ConnectAsync(cookie: cookie);
        await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        return (host, party, tv, guest);
    }

    private async Task<WatchResponse> WatchAsync(string code, string name) =>
        await Read<WatchResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/watch", new WatchRequest(name)));

    [Fact]
    public async Task Anyone_with_the_code_can_watch_the_TV_even_mid_game_but_never_play()
    {
        var (host, party, tv, guest) = await EscapePartyAsync();
        await using var _ = tv;
        Assert.True(party.AllowSpectators);
        await tv.InvokeAsync("EscapeStart", party.Code);
        // Players can't join once the clock is running, but people can still watch.
        Assert.Equal(HttpStatusCode.Conflict, (await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Late"))).StatusCode);
        var watch = await WatchAsync(party.Code, "Grandma");
        Assert.StartsWith("w.", watch.Token);

        await using var grandma = await app.ConnectAsync(watch.Token);
        var view = await grandma.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(EscapePhase.Playing, view.Phase);
        Assert.Equal("The Workshop", view.RoomTitle);

        // The TV's updates reach the watcher as they happen.
        var update = new TaskCompletionSource<EscapeStageView>(TaskCreationOptions.RunContinuationsAsynchronously);
        grandma.On<EscapeStageView>("stage", v => update.TrySetResult(v));
        await using var phone = await app.ConnectAsync(guest.Token);
        Assert.False(await phone.InvokeAsync<bool>("EscapeAnswer", "tape", "kettle"));
        Assert.Equal(1, (await Within(update)).WrongAttempts);

        // But a watcher has no seat: no clues, no moves, no selfie, and no other party's TV.
        await Assert.ThrowsAsync<HubException>(() => grandma.InvokeAsync<EscapePlayerView>("JoinSeat"));
        var answer = await Assert.ThrowsAsync<HubException>(() => grandma.InvokeAsync<bool>("EscapeAnswer", "tape", "clock"));
        Assert.Contains("Join the party first", answer.Message);
        await Assert.ThrowsAsync<HubException>(() => grandma.InvokeAsync("EscapeExamine", "drain"));
        var selfie = app.CreateClient();
        selfie.DefaultRequestHeaders.Add("X-Seat-Token", watch.Token);
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "photo", "me.png" } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await selfie.PostAsync("/api/seat/photo", form)).StatusCode);
        var (_, otherParty, otherTv, _) = await EscapePartyAsync();
        await using var __ = otherTv;
        await Assert.ThrowsAsync<HubException>(() => grandma.InvokeAsync<EscapeStageView>("WatchParty", otherParty.Code));

        // A mystery can be watched the same way.
        var mystery = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
        await using var mysteryWatcher = await app.ConnectAsync((await WatchAsync(mystery.Code, "Grandpa")).Token);
        Assert.Equal(Phase.Lobby, (await mysteryWatcher.InvokeAsync<StageView>("WatchParty", mystery.Code)).Phase);
    }

    [Fact]
    public async Task The_host_sees_who_is_watching_removes_anyone_and_can_switch_watching_off()
    {
        var (host, party, tv, _) = await EscapePartyAsync();
        await using var _ = tv;
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tv.On("audience", () => joined.TrySetResult());

        var bea = await WatchAsync(party.Code, "Bea");
        await Within(joined); // the host's TV is told, and fetches the list
        var cal = await WatchAsync(party.Code, "Cal");
        var list = await Read<SpectatorList>(await host.GetAsync($"/api/parties/{party.Code}/spectators"));
        Assert.True(list.Allow);
        Assert.Equal(["Bea", "Cal"], list.Watching.Select(w => w.Name));

        var (other, _) = await app.RegisterHostAsync($"o{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/parties/{party.Code}/spectators")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/parties/{party.Code}/spectators/{bea.WatcherId}")).StatusCode);

        // Removing Bea tells her screen, and her token stops working.
        await using var beaScreen = await app.ConnectAsync(bea.Token);
        await beaScreen.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        var beaRemoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        beaScreen.On("removed", () => beaRemoved.TrySetResult());
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/parties/{party.Code}/spectators/{bea.WatcherId}")).StatusCode);
        await Within(beaRemoved);
        await Assert.ThrowsAnyAsync<Exception>(async () => await app.ConnectAsync(bea.Token));
        // Nor from the connection she still had open: here (long polling) every poll checks the token, so it's closed;
        // over a WebSocket, checked only when it connects, Cheer itself refuses a watcher who is gone.
        await Assert.ThrowsAnyAsync<Exception>(() => beaScreen.InvokeAsync("Cheer", "👏"));
        Assert.Equal(["Cal"], (await Read<SpectatorList>(await host.GetAsync($"/api/parties/{party.Code}/spectators"))).Watching.Select(w => w.Name));

        // Switching watching off sends everyone away and turns new watchers back.
        await using var calScreen = await app.ConnectAsync(cal.Token);
        await calScreen.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        var calRemoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        calScreen.On("removed", () => calRemoved.TrySetResult());
        var off = await Read<SpectatorList>(await host.PutAsJsonAsync($"/api/parties/{party.Code}/spectators", new AllowSpectatorsRequest(false)));
        Assert.False(off.Allow);
        Assert.Empty(off.Watching);
        await Within(calRemoved);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/watch", new WatchRequest("Dee"))).StatusCode);
        Assert.False((await Read<PartyInfo>(await app.CreateClient().GetAsync($"/api/parties/{party.Code}"))).AllowSpectators);

        // On again.
        Assert.True((await Read<SpectatorList>(await host.PutAsJsonAsync($"/api/parties/{party.Code}/spectators", new AllowSpectatorsRequest(true)))).Allow);
        await WatchAsync(party.Code, "Dee");
    }

    [Fact]
    public async Task Cheers_reach_the_TV_with_a_name_and_are_kept_to_a_trickle()
    {
        var (host, party, tv, guest) = await EscapePartyAsync();
        await using var _ = tv;
        var cheers = new List<CheerEvent>();
        var first = new TaskCompletionSource<CheerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        tv.On<CheerEvent>("cheer", c =>
        {
            lock (cheers) cheers.Add(c);
            first.TrySetResult(c);
        });

        await using var grandma = await app.ConnectAsync((await WatchAsync(party.Code, "Grandma")).Token);
        await grandma.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        await grandma.InvokeAsync("Cheer", "👏");
        Assert.Equal(new CheerEvent("👏", "Grandma"), await Within(first));

        // Only the set cheers, never free text.
        var bad = await Assert.ThrowsAsync<HubException>(() => grandma.InvokeAsync("Cheer", "<b>hello</b>"));
        Assert.Contains("Pick one of the cheers", bad.Message);

        // A second cheer straight away is dropped, quietly: one person can't flood the TV.
        await grandma.InvokeAsync("Cheer", "🎉");

        // Guests can cheer too, under the name on their seat (not the host's email, on the host's own phone).
        await using var ada = await app.ConnectAsync(guest.Token);
        await ada.InvokeAsync("Cheer", "🔥");
        var hostSeat = await Read<SeatResponse>(await host.PostAsJsonAsync($"/api/parties/{party.Code}/seats", new AddSeatRequest("Hostess", false)));
        await using var hostPhone = await app.ConnectAsync(hostSeat.Token, host.DefaultRequestHeaders.GetValues("Cookie").Single());
        await hostPhone.InvokeAsync("Cheer", "😂");

        await Task.Delay(1000); // let anything that was going to arrive, arrive
        lock (cheers)
            Assert.Equal([new CheerEvent("👏", "Grandma"), new CheerEvent("🔥", "Ada"), new CheerEvent("😂", "Hostess")], cheers);

        // The host's own TV (no seat, no watch token) has nothing to cheer as.
        await Assert.ThrowsAsync<HubException>(() => tv.InvokeAsync("Cheer", "👏"));
    }

    [Fact]
    public async Task Watching_ends_with_the_party()
    {
        var (host, party, tv, _) = await EscapePartyAsync();
        await using var _ = tv;
        var watch = await WatchAsync(party.Code, "Grandma");
        await using var grandma = await app.ConnectAsync(watch.Token);
        await grandma.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        grandma.On("removed", () => removed.TrySetResult());

        // The host removes the unfinished party: its watchers go with it, and are told.
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/parties/{party.Code}")).StatusCode);
        await Within(removed);
        await Assert.ThrowsAnyAsync<Exception>(async () => await app.ConnectAsync(watch.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/watch", new WatchRequest("Late"))).StatusCode);
    }
}
