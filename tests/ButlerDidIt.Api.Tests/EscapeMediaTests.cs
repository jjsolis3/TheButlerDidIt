using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ButlerDidIt.Api.Tests;

/// <summary>A server whose admin (the first account) is made once, first, so every other host is an ordinary one.</summary>
public class MediaFactory : ApiFactory
{
    private readonly Lazy<Task<HttpClient>> _admin;

    public MediaFactory() => _admin = new(async () => (await RegisterHostAsync("owner@example.com")).Client);

    public Task<HttpClient> AdminAsync() => _admin.Value;
}

/// <summary>Uploads limited to 1 MB videos and 2 MB in all, so the limits can be tested quickly.</summary>
public sealed class SmallUploadsFactory : MediaFactory
{
    protected override IEnumerable<(string Key, string Value)> ExtraSettings => [("Media:MaxVideoMb", "1"), ("Media:UploadQuotaMb", "2")];
}

/// <summary>Helpers shared by the room media tests.</summary>
internal static class RoomMedia
{
    public static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    public static async Task<(HttpClient Client, string Cookie)> HostAsync(MediaFactory app)
    {
        await app.AdminAsync();
        return await app.RegisterHostAsync($"media{Guid.NewGuid():N}@example.com");
    }

    public static async Task<string> CopyAsync(HttpClient host, string id) =>
        (await Read<Dictionary<string, string>>(await host.PostAsync($"/api/escape-rooms/{id}/duplicate", null)))["id"];

    /// <summary>The file as the request body, as the browser sends it.</summary>
    public static Task<HttpResponseMessage> UploadAsync(HttpClient client, string room, string key, byte[] bytes, string type = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PostAsync($"/api/escape-rooms/{room}/media/{Uri.EscapeDataString(key)}", content);
    }

    /// <summary>The start of an MP4 file ("ftyp" at byte 4), padded to the size wanted. Enough to be recognised.</summary>
    public static byte[] Mp4(int size = 4096)
    {
        var bytes = new byte[size];
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }.CopyTo(bytes, 0);
        return bytes;
    }

    public static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.DarkGoldenrod);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    public static RoomMediaSlot Slot(RoomMediaView view, string key) => view.Slots.Single(s => s.Key == key);
}

/// <summary>A room's own pictures, videos and sounds (#110 step 2).</summary>
public class EscapeMediaTests(MediaFactory app) : IClassFixture<MediaFactory>
{
    private static Task<T> Read<T>(HttpResponseMessage res) => RoomMedia.Read<T>(res);

    [Fact]
    public async Task An_uploaded_cover_is_made_safe_and_shows_on_the_shelf_and_the_tv()
    {
        var (host, cookie) = await RoomMedia.HostAsync(app);
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");

        var view = await Read<RoomMediaView>(await RoomMedia.UploadAsync(host, copy, EscapeArt.Cover, RoomMedia.Png(2400, 1200), "image/png"));
        var cover = RoomMedia.Slot(view, EscapeArt.Cover);
        Assert.True(cover.Uploaded);
        Assert.Equal(MediaKind.Image, cover.Kind);

        // Re-encoded as a JPEG no bigger than a 1080p TV needs (which also drops anything hidden in the file).
        var file = await app.CreateClient().GetAsync(cover.Url);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        using (var decoded = SKBitmap.Decode(await file.Content.ReadAsByteArrayAsync()))
            Assert.Equal((1920, 960), (decoded.Width, decoded.Height));

        var shelf = GameJson.Deserialize<List<EscapeRoomSummary>>(await host.GetStringAsync("/api/escape-rooms"));
        Assert.Equal(cover.Url, shelf.Single(r => r.Id == copy).CoverUrl);

        // A TV already showing the lobby gets it straight away, and so does the intro video.
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(copy, PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        Assert.Equal(cover.Url, (await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).ArtUrl);
        var pushed = new TaskCompletionSource<EscapeStageView>(TaskCreationOptions.RunContinuationsAsynchronously);
        tv.On<EscapeStageView>("stage", v => { if (v.IntroVideoUrl is not null) pushed.TrySetResult(v); });
        var intro = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(host, copy, EscapeArt.IntroVideo, RoomMedia.Mp4(), "video/mp4")), EscapeArt.IntroVideo);
        Assert.Equal(intro.Url, (await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10))).IntroVideoUrl);
    }

    [Fact]
    public async Task Only_the_owner_or_the_admin_can_change_a_room_s_media_and_built_in_rooms_only_the_admin()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");

        // Someone else's room looks like no room at all.
        var (other, _) = await RoomMedia.HostAsync(app);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/escape-rooms/{copy}/media")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RoomMedia.UploadAsync(other, copy, EscapeArt.IntroVideo, RoomMedia.Mp4())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/escape-rooms/{copy}/media/{EscapeArt.Cover}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync($"/api/escape-rooms/{copy}/media")).StatusCode);
        // Only the room's own places: three for the room and three per stage.
        Assert.Equal(HttpStatusCode.NotFound, (await RoomMedia.UploadAsync(host, copy, "anything-else", RoomMedia.Mp4())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RoomMedia.UploadAsync(host, copy, EscapeArt.StageVideo("no-such-stage"), RoomMedia.Mp4())).StatusCode);

        // A built-in room: anyone can look, only the admin can change it, and then every host's games get it.
        var builtIn = await Read<RoomMediaView>(await host.GetAsync("/api/escape-rooms/the-funhouse/media"));
        Assert.False(builtIn.CanEdit);
        Assert.True(builtIn.BuiltIn);
        var refused = await RoomMedia.UploadAsync(host, "the-funhouse", EscapeArt.IntroVideo, RoomMedia.Mp4());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Make your own copy", await refused.Content.ReadAsStringAsync());

        var admin = await app.AdminAsync();
        var adminView = await Read<RoomMediaView>(await RoomMedia.UploadAsync(admin, "the-funhouse", EscapeArt.Ambience, "ID3\u0004\0\0\0\0\0\0"u8.ToArray(), "audio/mpeg"));
        Assert.True(adminView.CanEdit);
        Assert.Null(adminView.Limits.AllowanceBytes); // the admin has no limit
        Assert.True(RoomMedia.Slot(await Read<RoomMediaView>(await host.GetAsync("/api/escape-rooms/the-funhouse/media")), EscapeArt.Ambience).Uploaded);
        // The admin can change a host's room too.
        Assert.True((await RoomMedia.UploadAsync(admin, copy, EscapeArt.IntroVideo, RoomMedia.Mp4())).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Files_are_checked_by_what_they_are_not_what_they_are_called()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");
        var stage = (await Read<RoomMediaView>(await host.GetAsync($"/api/escape-rooms/{copy}/media"))).Slots.First(s => s.StageId is not null).StageId!;

        // A text file sent as a video, a picture sent as a sound, junk sent as a picture: refused, kindly.
        var text = await RoomMedia.UploadAsync(host, copy, EscapeArt.IntroVideo, "just some words, honestly"u8.ToArray(), "video/mp4");
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Contains("MP4", await text.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await RoomMedia.UploadAsync(host, copy, EscapeArt.Ambience, RoomMedia.Png(10, 10), "audio/mpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RoomMedia.UploadAsync(host, copy, EscapeArt.Cover, RoomMedia.Mp4(), "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RoomMedia.UploadAsync(host, copy, EscapeArt.Cover, [], "image/png")).StatusCode);

        // The real things are kept as they are, typed by what they are, and can be played from any point (byte ranges).
        var cases = new (string Key, byte[] Bytes, string Type)[]
        {
            (EscapeArt.StageVideo(stage), RoomMedia.Mp4(), "video/mp4"),
            (EscapeArt.IntroVideo, [0x1A, 0x45, 0xDF, 0xA3, .. new byte[200]], "video/webm"),
            (EscapeArt.Ambience, [.. "OggS"u8, .. new byte[200]], "audio/ogg"),
            (EscapeArt.StageAmbience(stage), [0xFF, 0xFB, 0x90, 0x64, .. new byte[200]], "audio/mpeg"),
        };
        foreach (var (key, bytes, type) in cases)
        {
            var slot = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(host, copy, key, bytes, "application/octet-stream")), key);
            Assert.Equal(bytes.Length, slot.SizeBytes);
            using var request = new HttpRequestMessage(HttpMethod.Get, slot.Url);
            request.Headers.Range = new RangeHeaderValue(0, 3);
            var part = await app.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
            Assert.Equal(type, part.Content.Headers.ContentType!.MediaType);
            Assert.Equal(bytes[..4], await part.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task Copies_share_files_and_a_file_goes_only_when_no_room_uses_it()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var first = await RoomMedia.CopyAsync(host, "the-workshop");
        var original = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(host, first, EscapeArt.IntroVideo, RoomMedia.Mp4())), EscapeArt.IntroVideo).Url!;
        var second = await RoomMedia.CopyAsync(host, first);
        Assert.Equal(original, RoomMedia.Slot(await Read<RoomMediaView>(await host.GetAsync($"/api/escape-rooms/{second}/media")), EscapeArt.IntroVideo).Url);

        // Replacing it in one room leaves the other room's video playing.
        var replaced = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(host, first, EscapeArt.IntroVideo, RoomMedia.Mp4(5000))), EscapeArt.IntroVideo).Url;
        Assert.NotEqual(original, replaced);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync(original)).StatusCode);

        // Once no room uses it, it's deleted.
        var emptied = await Read<RoomMediaView>(await host.DeleteAsync($"/api/escape-rooms/{second}/media/{EscapeArt.IntroVideo}"));
        Assert.Null(RoomMedia.Slot(emptied, EscapeArt.IntroVideo).Url);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(original)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync(replaced)).StatusCode);
    }

    [Fact]
    public async Task Rewording_a_stage_keeps_an_uploaded_picture_but_forgets_an_ai_one()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");
        var editable = await Read<EditableRoom>(await host.GetAsync($"/api/escape-rooms/{copy}/document"));
        var doc = JsonNode.Parse(editable.Document.GetRawText())!.AsObject();
        var stages = doc["stages"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToList();

        var uploaded = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(host, copy, EscapeArt.Stage(stages[0]), RoomMedia.Png(64, 64))), EscapeArt.Stage(stages[0])).Url;
        using (var scope = app.Services.CreateScope())
        {
            // A picture the AI painted for the second stage.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ScenarioMedia.Add(new ScenarioMediaEntity { ScenarioId = EscapeMedia.JobId(copy), Key = EscapeArt.Stage(stages[1]), AssetId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        doc["stages"]!.AsArray()[0]!["description"] = "Grandpa's chains, and his old drain.";
        doc["stages"]!.AsArray()[1]!["description"] = "Grandpa's door, newly described.";
        Assert.True((await Read<SavedRoom>(await host.PutAsJsonAsync($"/api/escape-rooms/{copy}/document",
            new RoomDocumentRequest(JsonSerializer.SerializeToElement(doc)), GameJson.Options))).Valid);

        var after = await Read<RoomMediaView>(await host.GetAsync($"/api/escape-rooms/{copy}/media"));
        Assert.Equal(uploaded, RoomMedia.Slot(after, EscapeArt.Stage(stages[0])).Url); // the host's own picture stays
        Assert.Null(RoomMedia.Slot(after, EscapeArt.Stage(stages[1])).Url); // the AI's is repainted next time
    }

    [Fact]
    public async Task Deleting_a_room_or_an_account_deletes_its_uploads()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var room = await RoomMedia.CopyAsync(host, "the-workshop");
        var cover = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(host, room, EscapeArt.Cover, RoomMedia.Png(64, 64))), EscapeArt.Cover).Url;
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/escape-rooms/{room}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(cover)).StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScenarioMedia.AnyAsync(m => m.ScenarioId == EscapeMedia.JobId(room)));

        var (leaving, _) = await RoomMedia.HostAsync(app);
        var theirs = await RoomMedia.CopyAsync(leaving, "the-workshop");
        var video = RoomMedia.Slot(await Read<RoomMediaView>(await RoomMedia.UploadAsync(leaving, theirs, EscapeArt.IntroVideo, RoomMedia.Mp4())), EscapeArt.IntroVideo).Url;
        Assert.Equal(HttpStatusCode.NoContent, (await leaving.PostAsJsonAsync("/api/account/delete", new DeleteAccountRequest("password123"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(video)).StatusCode);
    }
}

/// <summary>The size limits and each host's allowance, on a server with small ones.</summary>
public class EscapeMediaLimitTests(SmallUploadsFactory app) : IClassFixture<SmallUploadsFactory>
{
    private const int MB = 1024 * 1024;

    [Fact]
    public async Task A_video_over_the_limit_is_refused_whether_or_not_it_says_how_big_it_is()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");
        var view = await RoomMedia.Read<RoomMediaView>(await host.GetAsync($"/api/escape-rooms/{copy}/media"));
        Assert.Equal(MB, view.Limits.VideoBytes);

        var tooBig = await RoomMedia.UploadAsync(host, copy, EscapeArt.IntroVideo, RoomMedia.Mp4(MB + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Contains("1 MB at most", await tooBig.Content.ReadAsStringAsync());

        // Sent in chunks without a length: counted as it arrives, and stopped at the limit.
        var chunked = new StreamContent(new OneWayStream(RoomMedia.Mp4(MB + 1)));
        var res = await host.PostAsync($"/api/escape-rooms/{copy}/media/{EscapeArt.IntroVideo}", chunked);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Null(RoomMedia.Slot(await RoomMedia.Read<RoomMediaView>(await host.GetAsync($"/api/escape-rooms/{copy}/media")), EscapeArt.IntroVideo).Url);
    }

    [Fact]
    public async Task Each_host_has_an_allowance_and_a_replaced_file_does_not_count()
    {
        var (host, _) = await RoomMedia.HostAsync(app);
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");
        var stages = (await RoomMedia.Read<RoomMediaView>(await host.GetAsync($"/api/escape-rooms/{copy}/media"))).Slots
            .Where(s => s.StageId is not null).Select(s => s.StageId!).Distinct().ToList();
        var nearlyOneMb = RoomMedia.Mp4(900 * 1024);

        Assert.True((await RoomMedia.UploadAsync(host, copy, EscapeArt.IntroVideo, nearlyOneMb)).IsSuccessStatusCode);
        var view = await RoomMedia.Read<RoomMediaView>(await RoomMedia.UploadAsync(host, copy, EscapeArt.StageVideo(stages[0]), nearlyOneMb));
        Assert.Equal((2L * MB, 2L * 900 * 1024), (view.Limits.AllowanceBytes, view.Limits.UsedBytes));

        var over = await RoomMedia.UploadAsync(host, copy, EscapeArt.StageVideo(stages[1]), nearlyOneMb);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Contains("2 MB of uploads", await over.Content.ReadAsStringAsync());
        // Swapping the intro for another is fine: the old one goes.
        Assert.True((await RoomMedia.UploadAsync(host, copy, EscapeArt.IntroVideo, nearlyOneMb)).IsSuccessStatusCode);

        // Another host has their own allowance.
        var (other, _) = await RoomMedia.HostAsync(app);
        var theirs = await RoomMedia.CopyAsync(other, "the-workshop");
        Assert.True((await RoomMedia.UploadAsync(other, theirs, EscapeArt.IntroVideo, nearlyOneMb)).IsSuccessStatusCode);
    }

    /// <summary>A stream that can't seek or say its length, so the client sends it in chunks.</summary>
    private sealed class OneWayStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => base.Position; set => throw new NotSupportedException(); }
    }
}
