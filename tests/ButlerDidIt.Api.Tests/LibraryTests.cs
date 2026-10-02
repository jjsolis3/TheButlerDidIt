using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// The libraries (My escape rooms, My mysteries) and how the admin improves the built-in content for everyone:
/// edit a copy, share it with every host, and take the original off the shelf.
/// </summary>
public class LibraryTests(MediaFactory app) : IClassFixture<MediaFactory>
{
    private const string Blackwood = "death-at-blackwood-manor";

    private static Task<T> Read<T>(HttpResponseMessage res) => RoomMedia.Read<T>(res);

    private static Task<HttpResponseMessage> ShareRoomAsync(HttpClient client, string id, bool? shared = null, bool? hidden = null) =>
        client.PutAsJsonAsync($"/api/escape-rooms/{id}/sharing", new SharingRequest(shared, hidden), GameJson.Options);

    private static Task<HttpResponseMessage> ShareMysteryAsync(HttpClient client, string id, bool? shared = null, bool? hidden = null) =>
        client.PutAsJsonAsync($"/api/scenarios/{id}/sharing", new SharingRequest(shared, hidden), GameJson.Options);

    private static Task<HttpResponseMessage> HostRoomAsync(HttpClient client, string id) =>
        client.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(id, PartyMode.SharedScreen, UseAi: false), GameJson.Options);

    private static Task<HttpResponseMessage> HostMysteryAsync(HttpClient client, string id) =>
        client.PostAsJsonAsync("/api/parties", new CreatePartyRequest(id, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options);

    private static async Task<List<EscapeRoomSummary>> ShelfAsync(HttpClient client) =>
        GameJson.Deserialize<List<EscapeRoomSummary>>(await client.GetStringAsync("/api/escape-rooms"));

    private static async Task<List<ScenarioCard>> MysteryShelfAsync(HttpClient client) =>
        GameJson.Deserialize<List<ThemeCard>>(await client.GetStringAsync("/api/themes")).SelectMany(t => t.Scenarios).ToList();

    [Fact]
    public async Task My_escape_rooms_lists_a_host_s_own_rooms_and_the_built_in_ones()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");
        var library = await Read<List<EscapeLibraryItem>>(await host.GetAsync("/api/escape-rooms/library"));

        var mine = library.Single(i => i.Room.Id == copy);
        Assert.Equal((EscapeRoomSource.Copy, true, false), (mine.Source, mine.CanEdit, mine.CanShare)); // only the admin can share
        var workshop = library.Single(i => i.Room.Id == "the-workshop");
        Assert.Equal((EscapeRoomSource.BuiltIn, false, false), (workshop.Source, workshop.CanEdit, workshop.CanHide));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/escape-rooms/library")).StatusCode);

        // Sharing and hiding are the admin's.
        Assert.Equal(HttpStatusCode.Forbidden, (await ShareRoomAsync(host, copy, shared: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ShareRoomAsync(host, "the-workshop", hidden: true)).StatusCode);
    }

    [Fact]
    public async Task The_admin_shares_an_improved_room_with_every_host_and_takes_the_original_off_the_shelf()
    {
        var admin = await app.AdminAsync();
        var improved = await RoomMedia.CopyAsync(admin, "the-funhouse");
        var cover = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(admin, improved, EscapeArt.Cover, RoomMedia.Png(64, 64))), EscapeArt.Cover).Url;
        var adminLibrary = await Read<List<EscapeLibraryItem>>(await admin.GetAsync("/api/escape-rooms/library"));
        Assert.True(adminLibrary.Single(i => i.Room.Id == improved).CanShare);
        Assert.True(adminLibrary.Single(i => i.Room.Id == "the-funhouse").CanHide);

        Assert.Equal(HttpStatusCode.NoContent, (await ShareRoomAsync(admin, improved, shared: true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ShareRoomAsync(admin, "the-funhouse", hidden: true)).StatusCode);

        // Another host's shelf (and the public one) has the improved room, with its picture, and not the original.
        var (host, _) = await RoomMedia.HostAsync(app);
        foreach (var shelf in new[] { await ShelfAsync(host), await ShelfAsync(app.CreateClient()) })
        {
            var card = shelf.Single(r => r.Id == improved);
            Assert.Equal((true, false, cover), (card.Shared, card.Mine, card.CoverUrl));
            Assert.DoesNotContain(shelf, r => r.Id == "the-funhouse");
        }
        Assert.Equal(EscapeRoomSource.Shared, (await Read<List<EscapeLibraryItem>>(await host.GetAsync("/api/escape-rooms/library"))).Single(i => i.Room.Id == improved).Source);

        // They can host it, read it and make their own copy; only the admin can change it or its media.
        Assert.True((await HostRoomAsync(host, improved)).IsSuccessStatusCode);
        var doc = await Read<EditableRoom>(await host.GetAsync($"/api/escape-rooms/{improved}/document"));
        Assert.Equal((false, false, true), (doc.CanEdit, doc.BuiltIn, doc.Shared));
        var saved = await host.PutAsJsonAsync($"/api/escape-rooms/{improved}/document", new RoomDocumentRequest(doc.Document), GameJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, saved.StatusCode);
        Assert.Contains("Make your own copy", await saved.Content.ReadAsStringAsync());
        Assert.False((await Read<RoomMediaView>(await host.GetAsync($"/api/escape-rooms/{improved}/media"))).CanEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await RoomMedia.UploadAsync(host, improved, EscapeArt.IntroVideo, RoomMedia.Mp4())).StatusCode);
        Assert.NotEqual(improved, await RoomMedia.CopyAsync(host, improved)); // their own copy: a new room

        // The original takes no new parties, except the admin's; it's still in the admin's library, marked hidden.
        Assert.Equal(HttpStatusCode.BadRequest, (await HostRoomAsync(host, "the-funhouse")).StatusCode);
        Assert.True((await HostRoomAsync(admin, "the-funhouse")).IsSuccessStatusCode);
        Assert.True((await Read<List<EscapeLibraryItem>>(await admin.GetAsync("/api/escape-rooms/library"))).Single(i => i.Room.Id == "the-funhouse").Hidden);
        Assert.DoesNotContain(await Read<List<EscapeLibraryItem>>(await host.GetAsync("/api/escape-rooms/library")), i => i.Room.Id == "the-funhouse");

        // Put back, and no longer shared: as before.
        Assert.Equal(HttpStatusCode.NoContent, (await ShareRoomAsync(admin, "the-funhouse", hidden: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ShareRoomAsync(admin, improved, shared: false)).StatusCode);
        var after = await ShelfAsync(host);
        Assert.Contains(after, r => r.Id == "the-funhouse");
        Assert.DoesNotContain(after, r => r.Id == improved);
        Assert.Equal(HttpStatusCode.BadRequest, (await HostRoomAsync(host, improved)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/escape-rooms/{improved}/document")).StatusCode);
    }

    [Fact]
    public async Task Only_the_admin_s_own_rooms_can_be_shared_and_only_built_in_ones_hidden()
    {
        var admin = await app.AdminAsync();
        var (host, _) = await RoomMedia.HostAsync(app);
        var hosts = await RoomMedia.CopyAsync(host, "the-workshop");
        Assert.Equal(HttpStatusCode.BadRequest, (await ShareRoomAsync(admin, hosts, shared: true)).StatusCode); // copy it first
        Assert.Equal(HttpStatusCode.BadRequest, (await ShareRoomAsync(admin, "the-workshop", shared: true)).StatusCode);
        var own = await RoomMedia.CopyAsync(admin, "the-workshop");
        Assert.Equal(HttpStatusCode.BadRequest, (await ShareRoomAsync(admin, own, hidden: true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ShareRoomAsync(admin, "no-such-room", shared: true)).StatusCode);
    }

    [Fact]
    public async Task The_admin_shares_an_improved_mystery_and_takes_the_hand_written_one_off_the_shelf()
    {
        var admin = await app.AdminAsync();
        var improved = (await Read<Dictionary<string, string>>(await admin.PostAsync($"/api/scenarios/{Blackwood}/duplicate", null)))["id"];
        var node = JsonNode.Parse((await Read<EditableScenario>(await admin.GetAsync($"/api/scenarios/{improved}"))).Document.GetRawText())!;
        node["title"] = "Death at Blackwood Manor (revised)";
        Assert.True((await admin.PutAsJsonAsync($"/api/scenarios/{improved}", new ScenarioDocumentRequest(JsonSerializer.SerializeToElement(node)), GameJson.Options)).IsSuccessStatusCode);

        var (host, _) = await RoomMedia.HostAsync(app);
        // Private until shared: not on another host's shelf, and they can't start a party with it.
        Assert.DoesNotContain(await MysteryShelfAsync(host), c => c.Id == improved);
        Assert.Equal(HttpStatusCode.BadRequest, (await HostMysteryAsync(host, improved)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ShareMysteryAsync(host, Blackwood, hidden: true)).StatusCode);

        var mine = await Read<List<MyMystery>>(await admin.GetAsync("/api/scenarios/mine"));
        Assert.True(mine.Single(m => m.Id == improved).CanShare);
        Assert.True(mine.Single(m => m.Id == Blackwood).CanHide);
        Assert.Equal(HttpStatusCode.NoContent, (await ShareMysteryAsync(admin, improved, shared: true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ShareMysteryAsync(admin, Blackwood, hidden: true)).StatusCode);

        // On every shelf, like a hand-written one (not "your copy"), and playable; the original is gone from the shelf.
        foreach (var shelf in new[] { await MysteryShelfAsync(host), await MysteryShelfAsync(app.CreateClient()) })
        {
            var card = shelf.Single(c => c.Id == improved);
            Assert.Equal(("Death at Blackwood Manor (revised)", true, false, false), (card.Title, card.Shared, card.Custom, card.AiGenerated));
            Assert.DoesNotContain(shelf, c => c.Id == Blackwood);
        }
        Assert.True((await HostMysteryAsync(host, improved)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await HostMysteryAsync(host, Blackwood)).StatusCode);
        Assert.True((await HostMysteryAsync(admin, Blackwood)).IsSuccessStatusCode); // the admin can still try it out
        // Like hand-written mysteries, other hosts can't read or copy it (the solution is in there).
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/scenarios/{improved}")).StatusCode);
        Assert.True((await Read<List<MyMystery>>(await admin.GetAsync("/api/scenarios/mine"))).Single(m => m.Id == Blackwood).Hidden);

        Assert.Equal(HttpStatusCode.NoContent, (await ShareMysteryAsync(admin, Blackwood, hidden: false)).StatusCode);
        Assert.Contains(await MysteryShelfAsync(host), c => c.Id == Blackwood);
    }

    [Fact]
    public async Task A_host_s_own_mystery_is_still_theirs_alone()
    {
        var admin = await app.AdminAsync();
        var adminsCopy = (await Read<Dictionary<string, string>>(await admin.PostAsync($"/api/scenarios/{Blackwood}/duplicate", null)))["id"];
        var (host, _) = await RoomMedia.HostAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await HostMysteryAsync(host, adminsCopy)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ShareMysteryAsync(admin, Blackwood, shared: true)).StatusCode); // hand-written: already everyone's
        Assert.Equal(HttpStatusCode.BadRequest, (await ShareMysteryAsync(admin, adminsCopy, hidden: true)).StatusCode);
    }
}
