using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>A mystery's own pictures, videos and music: uploaded to the original, played in every version of it.</summary>
public class MysteryMediaTests(MediaFactory app) : IClassFixture<MediaFactory>
{
    private const string Blackwood = "death-at-blackwood-manor";

    private static Task<T> Read<T>(HttpResponseMessage res) => RoomMedia.Read<T>(res);

    /// <summary>The file as the request body. A key's slashes stay as they are ("portrait/finch").</summary>
    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string id, string key, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return client.PostAsync($"/api/scenarios/{id}/media/{key}", content);
    }

    private static MysteryMediaSlot Slot(MysteryMediaView view, string key) => view.Slots.Single(s => s.Key == key);

    private async Task<Scenario> PlayableAsync(string id)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ContentCatalog>().GetScenarioAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), id);
    }

    /// <summary>A mystery of this host's own: a copy of Blackwood, saved straight into the database.</summary>
    private async Task<string> OwnMysteryAsync(HttpClient host)
    {
        var me = GameJson.Deserialize<JsonObject>(await host.GetStringAsync("/api/auth/me"))["id"]!.GetValue<string>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.Scenarios.AsNoTracking().SingleAsync(s => s.Id == Blackwood);
        var id = $"blackwood-{Guid.NewGuid():N}"[..20];
        var doc = JsonNode.Parse(original.Document)!;
        doc["id"] = id;
        db.Scenarios.Add(new ScenarioEntity
        {
            Id = id, ThemeSlug = original.ThemeSlug, Title = original.Title, MinPlayers = original.MinPlayers, MaxPlayers = original.MaxPlayers,
            ContentRating = original.ContentRating, Source = ScenarioSource.Custom, OwnerUserId = me, Document = doc.ToJsonString(), UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task The_admin_s_uploads_to_a_built_in_mystery_play_in_every_version_of_it()
    {
        var admin = await app.AdminAsync();
        var view = await Read<MysteryMediaView>(await admin.GetAsync($"/api/scenarios/{Blackwood}/media"));
        Assert.True(view.BuiltIn);
        Assert.True(view.HasVersions);
        Assert.Contains(view.Slots, s => s.Section == "cast");
        Assert.Contains(view.Slots, s => s.Section == "clues");

        var opening = Slot(await Read<MysteryMediaView>(await UploadAsync(admin, Blackwood, MediaOverlay.Video(MediaOverlay.Prologue), RoomMedia.Mp4())),
            MediaOverlay.Video(MediaOverlay.Prologue));
        var music = Slot(await Read<MysteryMediaView>(await UploadAsync(admin, Blackwood, MediaOverlay.Music, [.. "OggS"u8, .. new byte[100]])), MediaOverlay.Music);
        Assert.True(opening.Uploaded);

        // A host's party playing version B gets them: the opening scene is the video, and the music plays in the lobby.
        var (host, cookie) = await RoomMedia.HostAsync(app);
        var versionB = ScenarioVariants.VariantId(Blackwood, "B");
        var played = await PlayableAsync(versionB);
        Assert.Equal((CueType.Video, opening.Url), (played.Prologue[0].Type, played.Prologue[0].Src));
        Assert.DoesNotContain(played.Prologue, c => c.Type is CueType.Narration or CueType.Image);
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(Blackwood, PartyMode.SharedScreen, null, UseAi: false, Version: versionB), GameJson.Options));
        Assert.Equal(versionB, party.ScenarioId);
        await using var tv = await app.ConnectAsync(cookie: cookie);
        Assert.Equal(music.Url, (await tv.InvokeAsync<StageView>("WatchParty", party.Code)).MusicUrl);

        // Removing the video brings the written opening back, in every version.
        await Read<MysteryMediaView>(await admin.DeleteAsync($"/api/scenarios/{Blackwood}/media/{MediaOverlay.Video(MediaOverlay.Prologue)}"));
        Assert.NotEqual(CueType.Video, (await PlayableAsync(versionB)).Prologue[0].Type);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(opening.Url)).StatusCode); // the file is gone too
        await admin.DeleteAsync($"/api/scenarios/{Blackwood}/media/{MediaOverlay.Music}");
    }

    [Fact]
    public async Task Only_the_owner_or_the_admin_can_change_a_mystery_s_media_and_only_on_the_original()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var mine = await OwnMysteryAsync(host);
        var portrait = MediaOverlay.Portrait("hargrove");
        Assert.True((await UploadAsync(host, mine, portrait, RoomMedia.Png(64, 64))).IsSuccessStatusCode);

        // Someone else's, a built-in one (for a host) and a version all look like no mystery at all.
        var (other, _) = await RoomMedia.HostAsync(app);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/scenarios/{mine}/media")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other, mine, portrait, RoomMedia.Png(8, 8))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/scenarios/{Blackwood}/media")).StatusCode);
        var admin = await app.AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/scenarios/{ScenarioVariants.VariantId(Blackwood, "B")}/media")).StatusCode);
        // Only the mystery's own places.
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(host, mine, "portrait/nobody", RoomMedia.Png(8, 8))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(host, mine, "video/no-such-act", RoomMedia.Mp4())).StatusCode);
        // The admin can change anyone's.
        Assert.True((await UploadAsync(admin, mine, MediaOverlay.Victim, RoomMedia.Png(64, 64))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_clue_s_own_photo_replaces_its_picture_and_editing_the_mystery_keeps_uploads_whose_place_is_still_there()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var mine = await OwnMysteryAsync(host);
        var doc = JsonNode.Parse((await Read<EditableScenario>(await host.GetAsync($"/api/scenarios/{mine}"))).Document.GetRawText())!.AsObject();
        var clue = doc["clues"]!.AsArray()[0]!.AsObject();
        var clueKey = MediaOverlay.ClueImage(clue["id"]!.GetValue<string>(), clue["title"]!.GetValue<string>());

        var view = await Read<MysteryMediaView>(await UploadAsync(host, mine, clueKey, RoomMedia.Png(64, 64)));
        var photo = Slot(view, clueKey).Url;
        var portraitKey = MediaOverlay.Portrait(doc["characters"]!.AsArray()[0]!["id"]!.GetValue<string>());
        var portrait = Slot(await Read<MysteryMediaView>(await UploadAsync(host, mine, portraitKey, RoomMedia.Png(64, 64))), portraitKey).Url;
        Assert.Equal(photo, (await PlayableAsync(mine)).FindClue(clue["id"]!.GetValue<string>())!.Image);

        // Reword the character (which would make the AI paint a new portrait) and rename the clue: the uploaded portrait
        // stays; the clue's picture belonged to its old title, so it goes.
        doc["characters"]!.AsArray()[0]!["publicBio"] = "A brand new description of this character.";
        clue["title"] = "A renamed clue";
        var saved = await host.PutAsJsonAsync($"/api/scenarios/{mine}", new ScenarioDocumentRequest(JsonSerializer.SerializeToElement(doc)), GameJson.Options);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var after = await Read<MysteryMediaView>(await host.GetAsync($"/api/scenarios/{mine}/media"));
        Assert.Equal(portrait, Slot(after, portraitKey).Url);
        Assert.DoesNotContain(after.Slots, s => s.Url == photo);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(photo)).StatusCode);

        // Deleting the mystery (never played) deletes its uploads.
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/scenarios/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(portrait)).StatusCode);
    }

    [Fact]
    public async Task A_copy_made_saved_and_given_media_in_the_editor_plays_it_at_its_party()
    {
        // The editor's own steps: duplicate a hand-written mystery, save an edit, add media, then play-test.
        var (admin, cookie) = await app.RegisterHostAsync($"m{Guid.NewGuid():N}@example.com");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Email!.StartsWith("m") && u.Email.EndsWith("@example.com"))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsAdmin, true));
        var copy = (await Read<Dictionary<string, string>>(await admin.PostAsync($"/api/scenarios/{Blackwood}/duplicate", null)))["id"];
        var doc = JsonNode.Parse((await Read<EditableScenario>(await admin.GetAsync($"/api/scenarios/{copy}"))).Document.GetRawText())!;
        doc["title"] = "Murder at Blackwood Grange";
        Assert.True((await admin.PutAsJsonAsync($"/api/scenarios/{copy}", new ScenarioDocumentRequest(JsonSerializer.SerializeToElement(doc)), GameJson.Options)).IsSuccessStatusCode);
        var portrait = Slot(await Read<MysteryMediaView>(await UploadAsync(admin, copy, MediaOverlay.Portrait("evelyn"), RoomMedia.Png(64, 64))), MediaOverlay.Portrait("evelyn"));
        var music = Slot(await Read<MysteryMediaView>(await UploadAsync(admin, copy, MediaOverlay.Music, [.. "OggS"u8, .. new byte[100]])), MediaOverlay.Music);

        var party = await Read<PartyInfo>(await admin.PostAsJsonAsync("/api/parties", new CreatePartyRequest(copy, PartyMode.PassAndPlay, null, UseAi: false), GameJson.Options));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        var stage = await tv.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.Equal(music.Url, stage.MusicUrl);
        Assert.Equal(portrait.Url, stage.Cast.Single(c => c.CharacterId == "evelyn").Portrait);
    }

    [Fact]
    public async Task A_lobby_already_on_the_tv_plays_its_theme_s_sound_then_gets_new_music_straight_away()
    {
        var (host, cookie) = await RoomMedia.HostAsync(app);
        var mine = await OwnMysteryAsync(host);
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties", new CreatePartyRequest(mine, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        var lobby = await tv.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.Null(lobby.MusicUrl);
        // Until then, the theme's made-up background sound (#127): Blackwood's country house in a storm.
        Assert.Equal(Soundscape.Storm, lobby.Soundscape);
        var pushed = new TaskCompletionSource<StageView>(TaskCreationOptions.RunContinuationsAsynchronously);
        tv.On<StageView>("stage", v => { if (v.MusicUrl is not null) pushed.TrySetResult(v); });

        var music = Slot(await Read<MysteryMediaView>(await UploadAsync(host, mine, MediaOverlay.Music, [0xFF, 0xFB, 0x90, 0x64, .. new byte[100]])), MediaOverlay.Music);
        Assert.Equal(music.Url, (await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10))).MusicUrl);
    }
}
