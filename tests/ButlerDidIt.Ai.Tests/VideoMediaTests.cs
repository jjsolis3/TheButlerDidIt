using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ButlerDidIt.Ai.Media;
using SkiaSharp;

namespace ButlerDidIt.Ai.Tests;

/// <summary>
/// The Filmmaker role's clips (#110): OpenAI's Sora and Google's Veo, against stand-ins for their APIs. A clip is started,
/// asked about until it's done, then fetched; these check each request, the waiting, and what goes wrong. No network, no key.
/// </summary>
public class VideoMediaTests
{
    /// <summary>Replies in turn for each path (the last one repeating), and remembers every request with its body.</summary>
    private sealed class Stub : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(HttpStatusCode, string, byte[])>> _routes = [];
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        /// <summary>The fields of a multipart form (Sora's), each as it was sent.</summary>
        public List<(string Name, string? FileName, string? Type, byte[] Bytes)> Parts { get; } = [];

        public Stub On(string path, HttpStatusCode status, string type, byte[] body)
        {
            if (!_routes.TryGetValue(path, out var replies)) _routes[path] = replies = new();
            replies.Enqueue((status, type, body));
            return this;
        }

        public Stub Json(string path, string json, HttpStatusCode status = HttpStatusCode.OK) => On(path, status, "application/json", Encoding.UTF8.GetBytes(json));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is MultipartFormDataContent form)
                foreach (var part in form)
                    Parts.Add((part.Headers.ContentDisposition?.Name?.Trim('"') ?? "", part.Headers.ContentDisposition?.FileName?.Trim('"'),
                        part.Headers.ContentType?.MediaType, await part.ReadAsByteArrayAsync(ct)));
            Requests.Add((request, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct)));
            var path = request.RequestUri!.PathAndQuery;
            var route = _routes.Keys.Where(k => path.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
            if (route is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var replies = _routes[route];
            var (status, type, body) = replies.Count > 1 ? replies.Dequeue() : replies.Peek();
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return new HttpResponseMessage(status) { Content = content };
        }
    }

    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2'];

    /// <summary>A portrait-shaped picture, so the tests see it cropped and scaled to the clip's 16:9.</summary>
    private static MediaFile Picture()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 600));
        surface.Canvas.Clear(SKColors.DarkSlateBlue);
        using var png = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        return new MediaFile(png.ToArray(), "image/png", "png");
    }

    private static IVideoGenerator Videos(Stub stub, AiProviderKind kind, string model) =>
        new MediaClientFactory(allowFake: true, new HttpClient(stub), videoPoll: TimeSpan.Zero)
            .CreateVideos(new AiProviderSettings(Guid.NewGuid(), kind.ToString(), kind, null, "test-key"), model);

    [Fact]
    public void The_first_frame_fills_the_clips_16_by_9()
    {
        var frame = VideoFrames.Fit(Picture());
        Assert.Equal("image/jpeg", frame.ContentType);
        using var decoded = SKBitmap.Decode(frame.Bytes);
        Assert.Equal((1280, 720), (decoded.Width, decoded.Height));
    }

    // ---- OpenAI (Sora)

    [Fact]
    public async Task Sora_is_given_the_frame_and_asked_until_the_clip_is_done()
    {
        var stub = new Stub()
            .On("/v1/videos/video_123/content", HttpStatusCode.OK, "video/mp4", Mp4)
            .Json("/v1/videos/video_123", """{"id":"video_123","status":"in_progress","progress":40}""")
            .Json("/v1/videos/video_123", """{"id":"video_123","status":"completed","progress":100}""")
            .Json("/v1/videos", """{"id":"video_123","object":"video","status":"queued"}""");

        var clip = await Videos(stub, AiProviderKind.OpenAI, "sora-2").AnimateAsync(Picture(), "A slow push-in on the galley", CancellationToken.None);

        Assert.Equal(Mp4, clip.Bytes);
        Assert.Equal(("video/mp4", "mp4"), (clip.ContentType, clip.Extension));
        var create = stub.Requests[0].Request;
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal("https://api.openai.com/v1/videos", create.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", create.Headers.Authorization!.ToString());
        string Field(string name) => Encoding.UTF8.GetString(stub.Parts.Single(p => p.Name == name).Bytes);
        Assert.Equal(("sora-2", "A slow push-in on the galley", "8", "1280x720"), (Field("model"), Field("prompt"), Field("seconds"), Field("size")));
        // The first frame, at exactly the clip's size, as Sora asks.
        var frame = stub.Parts.Single(p => p.Name == "input_reference");
        Assert.Equal(("frame.jpg", "image/jpeg"), (frame.FileName, frame.Type));
        using (var decoded = SKBitmap.Decode(frame.Bytes)) Assert.Equal((1280, 720), (decoded.Width, decoded.Height));
        // Asked twice (in progress, then done), then fetched as the video itself.
        Assert.Equal(2, stub.Requests.Count(r => r.Request.RequestUri!.AbsolutePath == "/v1/videos/video_123"));
        Assert.Equal("/v1/videos/video_123/content?variant=video", stub.Requests[^1].Request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task A_sora_clip_that_fails_says_why()
    {
        var stub = new Stub()
            .Json("/v1/videos/video_9", """{"id":"video_9","status":"failed","error":{"code":"moderation_blocked","message":"Your request was blocked by our moderation system."}}""")
            .Json("/v1/videos", """{"id":"video_9","status":"queued"}""");
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() => Videos(stub, AiProviderKind.OpenAI, "sora-2").AnimateAsync(Picture(), "x", CancellationToken.None));
        Assert.Contains("blocked by our moderation system", ex.Message);

        var refused = new Stub().Json("/v1/videos", """{"error":{"message":"Incorrect API key provided."}}""", HttpStatusCode.Unauthorized);
        var no = await Assert.ThrowsAsync<AiCallFailedException>(() => Videos(refused, AiProviderKind.OpenAI, "sora-2").AnimateAsync(Picture(), "x", CancellationToken.None));
        Assert.Contains("401", no.Message);
        Assert.Contains("Incorrect API key", no.Message);
    }

    // ---- Google (Veo)

    private const string Operation = "models/veo-3.0-fast-generate-001/operations/abc123";

    [Fact]
    public async Task Veo_is_given_the_frame_and_the_finished_file_is_fetched_with_the_key()
    {
        var stub = new Stub()
            .On("/v1beta/files/xyz789:download", HttpStatusCode.OK, "video/mp4", Mp4)
            .Json($"/v1beta/{Operation}", """{"name":"OP","done":false}""".Replace("OP", Operation))
            .Json($"/v1beta/{Operation}", """
                {"name":"OP","done":true,"response":{"generateVideoResponse":{"generatedSamples":[
                  {"video":{"uri":"https://generativelanguage.googleapis.com/v1beta/files/xyz789:download?alt=media"}}]}}}
                """.Replace("OP", Operation))
            .Json("/v1beta/models/veo-3.0-fast-generate-001:predictLongRunning", """{"name":"OP"}""".Replace("OP", Operation));

        var clip = await Videos(stub, AiProviderKind.Gemini, "veo-3.0-fast-generate-001").AnimateAsync(Picture(), "The deck at dusk", CancellationToken.None);

        Assert.Equal(Mp4, clip.Bytes);
        var (start, body) = stub.Requests[0];
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/veo-3.0-fast-generate-001:predictLongRunning", start.RequestUri!.ToString());
        Assert.Equal("test-key", start.Headers.GetValues("x-goog-api-key").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("The deck at dusk", json["instances"]![0]!["prompt"]!.GetValue<string>());
        Assert.Equal("image/jpeg", json["instances"]![0]!["image"]!["mimeType"]!.GetValue<string>());
        Assert.NotEmpty(Convert.FromBase64String(json["instances"]![0]!["image"]!["bytesBase64Encoded"]!.GetValue<string>()));
        Assert.Equal("16:9", json["parameters"]!["aspectRatio"]!.GetValue<string>());
        // Fetched the way Google's own SDK does, with the key.
        var download = stub.Requests[^1].Request;
        Assert.Equal("/v1beta/files/xyz789:download?alt=media", download.RequestUri!.PathAndQuery);
        Assert.Equal("test-key", download.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task A_veo_clip_held_back_by_its_safety_filter_says_so()
    {
        var stub = new Stub()
            .Json($"/v1beta/{Operation}", """
                {"name":"OP","done":true,"response":{"generateVideoResponse":{"raiMediaFilteredCount":1,
                  "raiMediaFilteredReasons":["The video was filtered because it may depict a person."]}}}
                """.Replace("OP", Operation))
            .Json("/v1beta/models/veo-3.0-fast-generate-001:predictLongRunning", """{"name":"OP"}""".Replace("OP", Operation));
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            Videos(stub, AiProviderKind.Gemini, "veo-3.0-fast-generate-001").AnimateAsync(Picture(), "x", CancellationToken.None));
        Assert.Contains("may depict a person", ex.Message);
        Assert.Contains("safety filter", ex.Message);
    }

    [Fact]
    public async Task A_veo_operation_that_failed_passes_on_its_error()
    {
        var stub = new Stub()
            .Json($"/v1beta/{Operation}", """{"name":"OP","done":true,"error":{"code":3,"message":"Unsupported aspect ratio for this model."}}""".Replace("OP", Operation))
            .Json("/v1beta/models/veo-2.0-generate-001:predictLongRunning", """{"name":"OP"}""".Replace("OP", Operation));
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            Videos(stub, AiProviderKind.Gemini, "veo-2.0-generate-001").AnimateAsync(Picture(), "x", CancellationToken.None));
        Assert.Contains("Unsupported aspect ratio", ex.Message);
    }

    // ---- Which providers make clips

    [Fact]
    public async Task Only_sora_and_veo_make_clips_and_the_fake_makes_a_playable_one()
    {
        var factory = new MediaClientFactory(allowFake: true);
        var ex = Assert.Throws<AiUnavailableException>(() =>
            factory.CreateVideos(new AiProviderSettings(Guid.NewGuid(), "Claude", AiProviderKind.Anthropic, null, "k"), "claude-opus-5"));
        Assert.Contains("OpenAI (Sora) or Gemini (Veo)", ex.Message);
        Assert.True(AiProviderAbilities.Can(AiProviderKind.Gemini, AiRole.Filmmaker));
        Assert.False(AiProviderAbilities.Can(AiProviderKind.StableDiffusion, AiRole.Filmmaker));
        Assert.False(AiProviderAbilities.Can(AiProviderKind.Ollama, AiRole.Filmmaker));

        var fake = await factory.CreateVideos(new AiProviderSettings(Guid.NewGuid(), "Fake", AiProviderKind.Fake, null, null), "fake-video")
            .AnimateAsync(Picture(), "x", CancellationToken.None);
        Assert.Equal("video/webm", fake.ContentType);
        Assert.Equal([0x1A, 0x45, 0xDF, 0xA3], fake.Bytes[..4]); // the WebM (EBML) signature
    }
}
