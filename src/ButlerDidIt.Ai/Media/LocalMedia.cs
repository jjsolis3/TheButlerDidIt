using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// Voices from a local Piper server (#33): free, private, and no key, to match Ollama for chat.
///
/// Piper's own HTTP server (<c>pip install piper-tts[http]</c>, then <c>python3 -m piper.http_server -m en_US-lessac-medium</c>)
/// answers <c>POST /synthesize</c> with a WAV file. The role's model is the Piper voice to use ("default" for the
/// server's own). A voice with several speakers (e.g. en_GB-vctk-medium) gives each character one of them.
/// </summary>
internal sealed class PiperSpeech(HttpClient http, string baseUrl, string model) : ITextToSpeech
{
    public const string DefaultUrl = "http://localhost:5000";

    private string Root => baseUrl.TrimEnd('/');
    private string? Voice => string.IsNullOrWhiteSpace(model) || model.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : model.Trim();

    public async Task<MediaFile> SpeakAsync(string text, string voice, CancellationToken ct)
    {
        var body = new JsonObject { ["text"] = text };
        if (Voice is { } name) body["voice"] = name;
        if (PiperVoices.Speaker(voice, await PiperVoices.SpeakersAsync(http, Root, Voice, ct)) is { } speaker) body["speaker_id"] = speaker;

        using var response = await http.PostAsync($"{Root}/synthesize", JsonContent.Create(body), ct);
        // Piper's server before 2025 had one endpoint: the text, posted to the root.
        using var older = response.StatusCode == HttpStatusCode.NotFound
            ? await http.PostAsync($"{Root}/", new StringContent(text, Encoding.UTF8, "text/plain"), ct)
            : null;
        var reply = older ?? response;
        if (!reply.IsSuccessStatusCode)
            throw new AiCallFailedException($"The Piper server at {Root} refused the voice clip ({(int)reply.StatusCode}): {LocalText.Short(await reply.Content.ReadAsStringAsync(ct))}");

        var bytes = await reply.Content.ReadAsByteArrayAsync(ct);
        if (!Wav.IsWav(bytes)) throw new AiCallFailedException($"The server at {Root} didn't answer with a WAV file. Is it a Piper server?");
        return new MediaFile(bytes, "audio/wav", "wav");
    }
}

/// <summary>Which of a Piper voice's speakers plays each of the app's voice names.</summary>
public static class PiperVoices
{
    private static readonly string[] Cast = ["fable", "onyx", "echo", "nova", "shimmer", "alloy"];

    /// <summary>
    /// The speaker for one of the app's voice names, or null for a voice with one speaker. Always below
    /// <paramref name="speakers"/> (Piper's own range check lets one too many through).
    /// </summary>
    public static int? Speaker(string voice, int speakers)
    {
        if (speakers <= 1) return null;
        var slot = Array.FindIndex(Cast, v => v.Equals(voice, StringComparison.OrdinalIgnoreCase));
        if (slot < 0) slot = (int)((uint)voice.Aggregate(7, (a, c) => a * 31 + c) % 1000u);
        return slot % speakers;
    }

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, int Speakers)> Counts = new();

    /// <summary>How many speakers the voice has, from the server's /voices (or /info for its own voice); 1 when it can't say.</summary>
    public static async Task<int> SpeakersAsync(HttpClient http, string root, string? voice, CancellationToken ct)
    {
        var key = $"{root}|{voice}";
        if (Counts.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10)) return cached.Speakers;
        var count = 1;
        try
        {
            int? named = null;
            if (voice is not null) named = JsonNode.Parse(await http.GetStringAsync($"{root}/voices", ct))?[voice]?["num_speakers"]?.GetValue<int>();
            // Not one of its voices (the server then uses its own), or no voice asked for: the server's own voice.
            count = named ?? JsonNode.Parse(await http.GetStringAsync($"{root}/info", ct))?["voice"]?["num_speakers"]?.GetValue<int>() ?? 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException)
        {
            // An older server without these endpoints: one voice for everyone.
        }
        Counts[key] = (DateTimeOffset.UtcNow, count);
        return count;
    }
}

/// <summary>
/// Pictures from a local Stable Diffusion WebUI (#33): AUTOMATIC1111 or Forge, started with <c>--api</c>. Free and
/// private, like Piper for voices.
///
/// <c>POST /sdapi/v1/txt2img</c> answers with the pictures as base64. The role's model is the checkpoint to use
/// ("default" for whichever the WebUI has loaded); it's sent as an override, so the WebUI's own choice is left alone.
/// </summary>
internal sealed class StableDiffusionImages(HttpClient http, string baseUrl, string model) : IImageGenerator
{
    public const string DefaultUrl = "http://localhost:7860";

    /// <summary>The things every prompt already asks to leave out, said the way these models listen.</summary>
    public const string NegativePrompt = "text, letters, words, watermark, signature, logo, blurry, deformed";

    private string Root => baseUrl.TrimEnd('/');

    public async Task<MediaFile> PaintAsync(string prompt, ImageShape shape, CancellationToken ct)
    {
        var (width, height) = StableDiffusionSizes.For(model, shape);
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["negative_prompt"] = NegativePrompt,
            ["width"] = width,
            ["height"] = height,
            ["steps"] = 25,
            ["cfg_scale"] = 7,
        };
        if (!string.IsNullOrWhiteSpace(model) && !model.Equals("default", StringComparison.OrdinalIgnoreCase))
            body["override_settings"] = new JsonObject { ["sd_model_checkpoint"] = model.Trim() };

        using var response = await http.PostAsync($"{Root}/sdapi/v1/txt2img", JsonContent.Create(body), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new AiCallFailedException($"The server at {Root} has no Stable Diffusion API. Start the WebUI with --api.");
        if (!response.IsSuccessStatusCode)
            throw new AiCallFailedException($"Stable Diffusion at {Root} refused the picture ({(int)response.StatusCode}): {ErrorMessage(text)}");

        var image = JsonNode.Parse(text)?["images"]?[0]?.GetValue<string>()
            ?? throw new AiCallFailedException("Stable Diffusion returned no picture.");
        // Some forks prefix a data URL.
        var comma = image.StartsWith("data:", StringComparison.Ordinal) ? image.IndexOf(',') : -1;
        var bytes = Convert.FromBase64String(comma >= 0 ? image[(comma + 1)..] : image);
        return LocalText.IsJpeg(bytes) ? new MediaFile(bytes, "image/jpeg", "jpg")
            : LocalText.IsWebp(bytes) ? new MediaFile(bytes, "image/webp", "webp")
            : new MediaFile(bytes, "image/png", "png");
    }

    /// <summary>The WebUI's checkpoints, by title ("sd_xl_base_1.0.safetensors [31e35c80fc]"), for Admin → AI's connection test.</summary>
    public static async Task<IReadOnlyList<string>> CheckpointsAsync(HttpClient http, string root, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{root}/sdapi/v1/sd-models", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new AiCallFailedException($"The server at {root} has no Stable Diffusion API. Start the WebUI with --api.");
        if (!response.IsSuccessStatusCode)
            throw new AiCallFailedException($"Stable Diffusion at {root} answered {(int)response.StatusCode}.");
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsArray() ?? [])
            .Select(m => m?["title"]?.GetValue<string>() ?? m?["model_name"]?.GetValue<string>())
            .OfType<string>().ToList();
    }

    /// <summary>The WebUI explains errors as <c>{"errors": …}</c> (out of memory, a bad checkpoint) or FastAPI's <c>{"detail": …}</c>.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            var reply = JsonNode.Parse(body);
            foreach (var field in new[] { "errors", "detail", "error" })
                if (reply?[field] is JsonValue v && v.GetValue<string>() is { Length: > 0 } message) return LocalText.Short(message);
            return LocalText.Short(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return LocalText.Short(body);
        }
    }
}

/// <summary>
/// Stable Diffusion paints best at the size its model was trained at: about 512 pixels for SD 1.5, about 1024
/// for SDXL, SD3 and Flux. The checkpoint's name usually says which ("sd_xl_base_1.0", "flux1-dev"); a name that
/// doesn't is treated as SD 1.5, the size every model can manage.
/// </summary>
public static class StableDiffusionSizes
{
    public static (int Width, int Height) For(string model, ImageShape shape)
    {
        var name = model.ToLowerInvariant();
        var large = name.Contains("xl") || name.Contains("flux") || name.Contains("sd3");
        var tall = shape == ImageShape.Portrait;
        return large ? (tall ? (832, 1216) : (1216, 832)) : (tall ? (512, 768) : (768, 512));
    }
}

internal static class LocalText
{
    public static string Short(string s) => s.Length <= 200 ? s : s[..200];
    public static bool IsJpeg(byte[] b) => b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8;
    public static bool IsWebp(byte[] b) => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';
}
