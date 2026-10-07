using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// The Filmmaker role (#110 step 3): it brings a still picture to life as a short clip. Image to video rather than
/// text to video, so each clip shows the same room as the picture the group has already seen.
/// </summary>
public interface IVideoGenerator
{
    /// <summary>Animates <paramref name="picture"/> following <paramref name="prompt"/>. Returns the clip (an MP4; a WebM from the fake).</summary>
    Task<MediaFile> AnimateAsync(MediaFile picture, string prompt, CancellationToken ct);
}

/// <summary>
/// The first frame of every clip: the picture cropped to fill 16:9 and scaled to 1280×720, as a JPEG. Sora wants the
/// reference image at exactly the clip's size, and Veo's 16:9 clips fit it too, whatever shape the picture was painted.
/// </summary>
public static class VideoFrames
{
    public const int Width = 1280, Height = 720;

    public static MediaFile Fit(MediaFile picture)
    {
        using var source = SKBitmap.Decode(picture.Bytes) ?? throw new AiCallFailedException("The stage's picture couldn't be read to make a clip from.");
        var scale = Math.Max((float)Width / source.Width, (float)Height / source.Height);
        var (w, h) = (source.Width * scale, source.Height * scale);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(image, SKRect.Create((Width - w) / 2, (Height - h) / 2, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var jpeg = surface.Snapshot().Encode(SKEncodedImageFormat.Jpeg, 92);
        return new MediaFile(jpeg.ToArray(), "image/jpeg", "jpg");
    }
}

/// <summary>
/// A job that runs for minutes: started with one request, then asked about until it's done. Shared by Sora and Veo.
/// </summary>
internal static class VideoJobs
{
    /// <summary>The longest a clip may take before the job gives up (Sora and Veo usually take one to five minutes).</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(15);

    public static async Task<JsonNode> SendAsync(HttpClient http, HttpRequestMessage request, string vendor, CancellationToken ct)
    {
        using var _ = request;
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiCallFailedException($"{vendor} refused the video clip ({(int)response.StatusCode}): {ErrorMessage(text)}");
        return JsonNode.Parse(text) ?? throw new AiCallFailedException($"{vendor} answered with nothing.");
    }

    public static async Task<byte[]> DownloadAsync(HttpClient http, HttpRequestMessage request, string vendor, CancellationToken ct)
    {
        using var _ = request;
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new AiCallFailedException($"{vendor} made the clip but wouldn't hand it over ({(int)response.StatusCode}): {ErrorMessage(await response.Content.ReadAsStringAsync(ct))}");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Both explain errors as <c>{"error": {"message": …}}</c>.</summary>
    public static string ErrorMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? Short(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Short(body);
        }
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200];
}

/// <summary>
/// OpenAI's Sora (sora-2, sora-2-pro): <c>POST /videos</c> with the prompt and the first frame (<c>input_reference</c>),
/// then <c>GET /videos/{id}</c> until it's completed, then <c>GET /videos/{id}/content</c> for the MP4. Eight seconds at 1280×720.
/// </summary>
internal sealed class OpenAiVideos(HttpClient http, string apiKey, string? baseUrl, string model, TimeSpan pollEvery) : IVideoGenerator
{
    public const string Endpoint = "https://api.openai.com/v1/";
    private const string Vendor = "OpenAI";
    private string Root => string.IsNullOrWhiteSpace(baseUrl) ? Endpoint : baseUrl.TrimEnd('/') + "/";

    public async Task<MediaFile> AnimateAsync(MediaFile picture, string prompt, CancellationToken ct)
    {
        var frame = VideoFrames.Fit(picture);
        var image = new ByteArrayContent(frame.Bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue(frame.ContentType);
        var form = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent(prompt), "prompt" },
            { new StringContent("8"), "seconds" },
            { new StringContent($"{VideoFrames.Width}x{VideoFrames.Height}"), "size" },
            { image, "input_reference", "frame.jpg" },
        };
        var job = await VideoJobs.SendAsync(http, Request(HttpMethod.Post, "videos", form), Vendor, ct);
        var id = job["id"]?.GetValue<string>() ?? throw new AiCallFailedException("OpenAI didn't say which video it's making.");

        var giveUp = DateTimeOffset.UtcNow + VideoJobs.GiveUpAfter;
        while (job["status"]?.GetValue<string>() is "queued" or "in_progress")
        {
            if (DateTimeOffset.UtcNow > giveUp) throw new AiCallFailedException($"OpenAI's clip wasn't ready after {VideoJobs.GiveUpAfter.TotalMinutes} minutes.");
            await Task.Delay(pollEvery, ct);
            job = await VideoJobs.SendAsync(http, Request(HttpMethod.Get, $"videos/{Uri.EscapeDataString(id)}"), Vendor, ct);
        }
        if (job["status"]?.GetValue<string>() != "completed")
            throw new AiCallFailedException($"OpenAI couldn't make the clip: {job["error"]?["message"]?.GetValue<string>() ?? job["status"]?.GetValue<string>() ?? "no reason given"}.");

        var bytes = await VideoJobs.DownloadAsync(http, Request(HttpMethod.Get, $"videos/{Uri.EscapeDataString(id)}/content?variant=video"), Vendor, ct);
        return new MediaFile(bytes, "video/mp4", "mp4");
    }

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, Root + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }
}

/// <summary>
/// Google's Veo, through the Gemini API: <c>POST models/{model}:predictLongRunning</c> with the prompt and the first
/// frame, then the operation until it's done, then the finished file (<c>files/{id}:download</c>). A 16:9 clip.
/// </summary>
internal sealed partial class GeminiVideos(HttpClient http, string apiKey, string model, TimeSpan pollEvery) : IVideoGenerator
{
    private const string Vendor = "Gemini";
    private const string Root = GeminiApi.NativeEndpoint + "v1beta/";

    public async Task<MediaFile> AnimateAsync(MediaFile picture, string prompt, CancellationToken ct)
    {
        var frame = VideoFrames.Fit(picture);
        var body = new JsonObject
        {
            ["instances"] = new JsonArray(new JsonObject
            {
                ["prompt"] = prompt,
                ["image"] = new JsonObject { ["bytesBase64Encoded"] = Convert.ToBase64String(frame.Bytes), ["mimeType"] = frame.ContentType },
            }),
            ["parameters"] = new JsonObject { ["aspectRatio"] = "16:9" },
        };
        var operation = await VideoJobs.SendAsync(http, Request(HttpMethod.Post, $"{Root}models/{Uri.EscapeDataString(model)}:predictLongRunning", JsonContent.Create(body)), Vendor, ct);
        var name = operation["name"]?.GetValue<string>() ?? throw new AiCallFailedException("Gemini didn't say which video it's making.");

        var giveUp = DateTimeOffset.UtcNow + VideoJobs.GiveUpAfter;
        while (operation["done"]?.GetValue<bool>() != true)
        {
            if (DateTimeOffset.UtcNow > giveUp) throw new AiCallFailedException($"Veo's clip wasn't ready after {VideoJobs.GiveUpAfter.TotalMinutes} minutes.");
            await Task.Delay(pollEvery, ct);
            operation = await VideoJobs.SendAsync(http, Request(HttpMethod.Get, Root + name), Vendor, ct);
        }
        if (operation["error"]?["message"]?.GetValue<string>() is { } error) throw new AiCallFailedException($"Veo couldn't make the clip: {error}");

        var result = operation["response"]?["generateVideoResponse"];
        var video = result?["generatedSamples"]?[0]?["video"];
        if (video?["encodedVideo"]?.GetValue<string>() is { Length: > 0 } inline) return new MediaFile(Convert.FromBase64String(inline), "video/mp4", "mp4");
        if (video?["uri"]?.GetValue<string>() is not { Length: > 0 } uri)
        {
            // No clip: say why (a safety filter reads very differently from a wrong model).
            var reason = result?["raiMediaFilteredReasons"]?[0]?.GetValue<string>();
            throw new AiCallFailedException(reason is null
                ? $"Veo returned no clip. Check that '{model}' is a Veo model."
                : $"Veo didn't make the clip ({reason}). It may have been blocked by its safety filter.");
        }

        var bytes = await VideoJobs.DownloadAsync(http, Request(HttpMethod.Get, DownloadUrl(uri)), Vendor, ct);
        return new MediaFile(bytes, "video/mp4", "mp4");
    }

    /// <summary>A generated file's address, as Google's own SDK reads it: the id after "files/", fetched with <c>:download?alt=media</c>.</summary>
    internal static string DownloadUrl(string uri)
    {
        var at = uri.IndexOf("files/", StringComparison.Ordinal);
        if (at < 0) return uri;
        var id = FileId().Match(uri[(at + "files/".Length)..]);
        return id.Success ? $"{Root}files/{id.Value}:download?alt=media" : uri;
    }

    [GeneratedRegex("^[a-z0-9]+")]
    private static partial Regex FileId();

    private HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
    }
}

/// <summary>A two-second WebM, the same for every picture. Lets tests run the whole clip pipeline without a provider.</summary>
public sealed class FakeVideos : IVideoGenerator
{
    private static readonly Lazy<byte[]> Clip = new(() =>
    {
        using var stream = typeof(FakeVideos).Assembly.GetManifestResourceStream("ButlerDidIt.Ai.Fake.FakeClip.webm")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    public Task<MediaFile> AnimateAsync(MediaFile picture, string prompt, CancellationToken ct) =>
        Task.FromResult(new MediaFile(Clip.Value, "video/webm", "webm"));
}
