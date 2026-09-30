using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButlerDidIt.Ai.Media;

/// <summary>
/// Pictures and voices from Google Gemini: the "Nano Banana" image models (gemini-…-image) and the
/// text-to-speech models (gemini-…-tts).
///
/// Chat reaches Gemini through its OpenAI-compatible endpoint (see ChatClientFactory), but images and
/// speech aren't offered there. They use Gemini's own generateContent API, where the model is asked to
/// answer with an image or with audio instead of text. It's plain JSON over HTTPS, so no extra SDK is needed.
/// </summary>
internal static class GeminiApi
{
    public const string NativeEndpoint = "https://generativelanguage.googleapis.com/";

    /// <summary>Sends one generateContent request and returns the first inline file in the reply, or explains why there's none.</summary>
    public static async Task<(byte[] Bytes, string MimeType)> GenerateFileAsync(HttpClient http, string apiKey, string model, JsonObject body, string what, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NativeEndpoint}v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-goog-api-key", apiKey);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiCallFailedException($"Gemini refused the {what} request ({(int)response.StatusCode}): {ErrorMessage(text)}");

        var reply = JsonNode.Parse(text);
        var candidate = reply?["candidates"]?[0];
        foreach (var part in candidate?["content"]?["parts"]?.AsArray() ?? [])
        {
            // The REST API answers in camelCase; accept snake_case too, as some proxies and older versions use it.
            var inline = part?["inlineData"] ?? part?["inline_data"];
            if (inline?["data"]?.GetValue<string>() is { Length: > 0 } data)
                return (Convert.FromBase64String(data), (inline["mimeType"] ?? inline["mime_type"])?.GetValue<string>() ?? "");
        }

        // No file: say why, in words a host can act on (a safety block reads very differently from a wrong model).
        var reason = reply?["promptFeedback"]?["blockReason"]?.GetValue<string>() ?? candidate?["finishReason"]?.GetValue<string>();
        throw new AiCallFailedException(reason is null or "STOP"
            ? $"Gemini returned no {what}. Check that '{model}' is a model that makes {what}s."
            : $"Gemini didn't make the {what} ({reason}). It may have been blocked by its safety filter.");
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body;
        }
        catch (JsonException)
        {
            return body.Length <= 200 ? body : body[..200];
        }
    }
}

/// <summary>Nano Banana: Gemini's image models. Asked for a picture in the shape we need, it replies with the image itself.</summary>
internal sealed class GeminiImages(HttpClient http, string apiKey, string model) : IImageGenerator
{
    public async Task<MediaFile> PaintAsync(string prompt, ImageShape shape, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["generationConfig"] = new JsonObject
            {
                // Some image models insist on being allowed to add a caption, so text is allowed too; only the image is kept.
                ["responseModalities"] = new JsonArray("TEXT", "IMAGE"),
                ["imageConfig"] = new JsonObject { ["aspectRatio"] = shape == ImageShape.Portrait ? "3:4" : "16:9" },
            },
        };
        var (bytes, mime) = await GeminiApi.GenerateFileAsync(http, apiKey, model, body, "image", ct);
        return mime switch
        {
            "image/jpeg" or "image/jpg" => new MediaFile(bytes, "image/jpeg", "jpg"),
            "image/webp" => new MediaFile(bytes, "image/webp", "webp"),
            _ => new MediaFile(bytes, "image/png", "png"),
        };
    }
}

/// <summary>
/// Gemini's text-to-speech models. They reply with raw audio samples (16-bit PCM, usually 24 kHz, one
/// channel) rather than a file, so the samples are wrapped in a WAV header that every browser can play.
/// </summary>
internal sealed class GeminiSpeech(HttpClient http, string apiKey, string model) : ITextToSpeech
{
    public async Task<MediaFile> SpeakAsync(string text, string voice, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }),
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = GeminiVoices.For(voice) } },
                },
            },
        };
        var (pcm, mime) = await GeminiApi.GenerateFileAsync(http, apiKey, model, body, "voice clip", ct);
        return new MediaFile(Wav.FromPcm16(pcm, SampleRate(mime)), "audio/wav", "wav");
    }

    /// <summary>The rate is in the MIME type, e.g. "audio/L16;codec=pcm;rate=24000". Gemini's voices are 24 kHz.</summary>
    internal static int SampleRate(string mime)
    {
        foreach (var part in mime.Split(';', StringSplitOptions.TrimEntries))
            if (part.StartsWith("rate=", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[5..], out var rate) && rate > 0) return rate;
        return 24_000;
    }
}

/// <summary>
/// The app casts voices with OpenAI's names (see <see cref="VoiceCasting"/>). With Gemini, each is swapped
/// for a Gemini voice of the same kind, so a character keeps one voice and deep voices stay deep.
/// </summary>
public static class GeminiVoices
{
    private static readonly Dictionary<string, string> ByOpenAiName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["onyx"] = "Charon",   // deep, informative
        ["echo"] = "Fenrir",   // low, excitable
        ["fable"] = "Orus",    // the narrator: firm, storyteller
        ["nova"] = "Kore",     // bright, firm
        ["shimmer"] = "Aoede", // light, breezy
        ["alloy"] = "Puck",    // upbeat
    };

    /// <summary>The Gemini voice for one of the app's voice names. A name that is already a Gemini voice passes through.</summary>
    public static string For(string voice) => ByOpenAiName.GetValueOrDefault(voice, voice);
}

/// <summary>Wraps raw audio samples in a WAV file.</summary>
public static class Wav
{
    public static byte[] FromPcm16(byte[] samples, int sampleRate, short channels = 1) => Build(samples, sampleRate, channels, bitsPerSample: 16);

    internal static byte[] Build(byte[] samples, int sampleRate, short channels, short bitsPerSample)
    {
        var blockAlign = (short)(channels * bitsPerSample / 8);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels); w.Write(sampleRate); w.Write(sampleRate * blockAlign); w.Write(blockAlign); w.Write(bitsPerSample);
        w.Write("data"u8); w.Write(samples.Length); w.Write(samples);
        w.Flush();
        return ms.ToArray();
    }
}
