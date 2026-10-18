using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// Costume selfies at Family parties (#150): their guests may be children, so selfies start off, the host turns them
/// on once the parents are OK, and the recap a host shares never shows them.
/// </summary>
public class SelfieTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private const string FamilyMystery = "the-captains-last-cocoa";
    private const string FamilyRoom = "the-toy-factory";

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> MysteryAsync(string scenario)
    {
        var (host, cookie) = await app.RegisterHostAsync($"selfie{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(scenario, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
        return (host, cookie, party);
    }

    private async Task<(HttpClient Guest, SeatResponse Seat)> JoinAsync(PartyInfo party, string name)
    {
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name)));
        var guest = app.CreateClient();
        guest.DefaultRequestHeaders.Add("X-Seat-Token", seat.Token);
        return (guest, seat);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient guest)
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.DarkRed);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 90);
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(png.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "photo", "costume.png");
        return guest.PostAsync("/api/seat/photo", form);
    }

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient host, PartyInfo party, bool allow, bool parentsOk = false) =>
        host.PutAsJsonAsync($"/api/parties/{party.Code}/selfies", new SelfiesRequest(allow, parentsOk));

    private async Task<int> PhotosAsync(PartyInfo party)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = await db.Parties.Where(p => p.Code == party.Code).Select(p => p.Id).SingleAsync();
        return await db.MediaAssets.CountAsync(a => a.PartyId == id && a.Kind == MediaKind.Photo);
    }

    [Fact]
    public async Task A_family_party_starts_with_selfies_off_until_the_host_says_the_parents_are_ok()
    {
        var (host, cookie, party) = await MysteryAsync(FamilyMystery);
        Assert.False(party.SelfiesAllowed);
        var (guest, seat) = await JoinAsync(party, "Pip");
        Assert.Contains("hasn't turned costume selfies on", await (await UploadAsync(guest)).Content.ReadAsStringAsync());
        Assert.Equal(0, await PhotosAsync(party)); // nothing was kept

        // The phone hears the switch as it happens.
        var heard = new List<bool>();
        await using var phone = await app.ConnectAsync(seat.Token);
        phone.On<bool>("selfies", heard.Add);
        await phone.InvokeAsync<PlayerView>("JoinSeat");

        // Turning them on is saying the parents are happy for their children to share a photo.
        Assert.Contains("parents", await (await SwitchAsync(host, party, allow: true)).Content.ReadAsStringAsync());
        Assert.True((await Read<SelfiesView>(await SwitchAsync(host, party, allow: true, parentsOk: true))).Allow);
        Assert.True((await Read<PartyInfo>(await app.CreateClient().GetAsync($"/api/parties/{party.Code}"))).SelfiesAllowed);
        using (var scope = app.Services.CreateScope())
            Assert.NotNull((await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.SingleAsync(p => p.Code == party.Code)).SelfiesConsentAt);

        var url = (await Read<Dictionary<string, string>>(await UploadAsync(guest)))["photoUrl"];
        await using var tv = await app.ConnectAsync(cookie: cookie);
        Assert.Equal(url, (await tv.InvokeAsync<StageView>("WatchParty", party.Code)).Players.Single().PhotoUrl);

        // Off again: the selfies taken are gone from the screens and from the server, and no new one gets in.
        Assert.False((await Read<SelfiesView>(await SwitchAsync(host, party, allow: false))).Allow);
        Assert.Null((await tv.InvokeAsync<StageView>("WatchParty", party.Code)).Players.Single().PhotoUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(url)).StatusCode);
        Assert.Equal(0, await PhotosAsync(party));
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(guest)).StatusCode);
        // A guest can always take theirs back.
        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync("/api/seat/photo")).StatusCode);

        for (var i = 0; i < 20 && heard.Count < 2; i++) await Task.Delay(50);
        Assert.Equal([true, false], heard);

        // Only the host switches them: another host's party looks like no party, and a guest isn't a host.
        var (other, _) = await app.RegisterHostAsync($"other{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await SwitchAsync(other, party, allow: true, parentsOk: true)).StatusCode);
        Assert.False((await SwitchAsync(guest, party, allow: true, parentsOk: true)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Other_parties_start_with_selfies_on_and_need_no_confirmation()
    {
        var (host, _, party) = await MysteryAsync("death-at-blackwood-manor");
        Assert.True(party.SelfiesAllowed);
        var (guest, _) = await JoinAsync(party, "Alice");
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(guest)).StatusCode);
        Assert.False((await Read<SelfiesView>(await SwitchAsync(host, party, allow: false))).Allow);
        Assert.True((await Read<SelfiesView>(await SwitchAsync(host, party, allow: true))).Allow);

        // A Family escape room starts with them off too.
        var room = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest(FamilyRoom, PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        Assert.False(room.SelfiesAllowed);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync((await JoinAsync(room, "Ada")).Guest)).StatusCode);
    }

    [Fact]
    public async Task A_family_mysterys_recap_shows_no_selfies()
    {
        var (host, cookie, party) = await MysteryAsync(FamilyMystery);
        await SwitchAsync(host, party, allow: true, parentsOk: true);
        var (guest, _) = await JoinAsync(party, "Pip");
        await JoinAsync(party, "Bo");
        await JoinAsync(party, "Kit");
        await Read<Dictionary<string, string>>(await UploadAsync(guest));

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);
        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        for (var i = 0; i < 40 && view.Phase != Phase.Finished; i++)
        {
            await stage.InvokeAsync("Advance", party.Code);
            view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        }
        Assert.Equal(Phase.Finished, view.Phase);
        Assert.NotNull(view.Players.Single(p => p.Name == "Pip").PhotoUrl); // still the party's, until it's pruned

        // The host's preview is what the link shows, and neither has the photo.
        var preview = await Read<RecapSharing>(await host.GetAsync($"/api/parties/{party.Code}/recap"));
        Assert.Contains(preview.Page.Recap.Cast, c => c.PlayedBy == "Pip");
        Assert.All(preview.Page.Recap.Cast, c => Assert.Null(c.PhotoUrl));
        var slug = (await Read<Dictionary<string, string>>(await host.PostAsync($"/api/parties/{party.Code}/recap/share", null)))["url"]["/recap/".Length..];
        Assert.All((await Read<RecapPage>(await app.CreateClient().GetAsync($"/api/recap/{slug}"))).Recap.Cast, c => Assert.Null(c.PhotoUrl));
    }

    [Fact]
    public async Task A_family_escape_rooms_recap_shows_no_selfies()
    {
        var (host, cookie) = await app.RegisterHostAsync($"room{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest(FamilyRoom, PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        await SwitchAsync(host, party, allow: true, parentsOk: true);
        var (guest, _) = await JoinAsync(party, "Ada");
        await Read<Dictionary<string, string>>(await UploadAsync(guest));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await tv.InvokeAsync("EscapeStart", party.Code);
        Assert.NotNull((await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).Players.Single().PhotoUrl);

        using (var scope = app.Services.CreateScope())
        {
            var id = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.Where(p => p.Code == party.Code).Select(p => p.Id).SingleAsync();
            // The ticker, an hour late: the clock has run out.
            await scope.ServiceProvider.GetRequiredService<PartyRuntime>().ExecuteAsync(id, (s, now) => s.Tick(now.AddHours(1)));
        }

        var recap = (await Read<EscapeRecapSharing>(await host.GetAsync($"/api/parties/{party.Code}/escape-recap"))).Page.Recap;
        Assert.Equal("Ada", recap.Team.Single().Name);
        Assert.Null(recap.Team.Single().PhotoUrl);
    }
}
