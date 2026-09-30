using System.ClientModel;
using ButlerDidIt.Game.Scenarios;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Images;
using SkiaSharp;

namespace ButlerDidIt.Ai.Media;

/// <summary>A generated file: its bytes, type, and how much was used (characters for speech, 1 for an image).</summary>
public sealed record MediaFile(byte[] Bytes, string ContentType, string Extension);

public interface ITextToSpeech
{
    Task<MediaFile> SpeakAsync(string text, string voice, CancellationToken ct);
}

public interface IImageGenerator
{
    Task<MediaFile> PaintAsync(string prompt, ImageShape shape, CancellationToken ct);
}

public enum ImageShape
{
    /// <summary>Portrait orientation, for characters.</summary>
    Portrait,

    /// <summary>Landscape, for scenes on the TV.</summary>
    Landscape,
}

public interface IMediaClientFactory
{
    ITextToSpeech CreateSpeech(AiProviderSettings provider, string model);
    IImageGenerator CreateImages(AiProviderSettings provider, string model);
}

/// <summary>
/// Builds voice and image clients. Media APIs aren't standardised the way chat is
/// (there's no IChatClient equivalent), so each provider needs its own adapter.
/// OpenAI (or an OpenAI-compatible server) and Google Gemini are supported; more can be added here.
/// </summary>
/// <param name="http">For Gemini's HTTP calls; tests pass one with a stub handler.</param>
public sealed class MediaClientFactory(bool allowFake, HttpClient? http = null) : IMediaClientFactory
{
    /// <summary>The providers that can make voices and pictures. The admin screen only offers these for those roles.</summary>
    public static bool SupportsMedia(AiProviderKind kind) => kind is AiProviderKind.OpenAI or AiProviderKind.Gemini or AiProviderKind.Fake;

    // One shared client for the app's lifetime (the usual .NET advice): pictures can take a minute to paint.
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(3) };
    private HttpClient Http => http ?? SharedHttp;

    public ITextToSpeech CreateSpeech(AiProviderSettings provider, string model) => provider.Kind switch
    {
        AiProviderKind.OpenAI => new OpenAiSpeech(OpenAi(provider).GetAudioClient(model)),
        AiProviderKind.Gemini => new GeminiSpeech(Http, Key(provider), model),
        AiProviderKind.Fake when allowFake => new FakeSpeech(),
        _ => throw new AiUnavailableException($"Voices need an OpenAI or Gemini provider; '{provider.Name}' is {provider.Kind}."),
    };

    public IImageGenerator CreateImages(AiProviderSettings provider, string model) => provider.Kind switch
    {
        AiProviderKind.OpenAI => new OpenAiImages(OpenAi(provider).GetImageClient(model), model),
        AiProviderKind.Gemini => new GeminiImages(Http, Key(provider), model),
        AiProviderKind.Fake when allowFake => new FakeImages(),
        _ => throw new AiUnavailableException($"Images need an OpenAI or Gemini provider; '{provider.Name}' is {provider.Kind}."),
    };

    private static string Key(AiProviderSettings p) =>
        string.IsNullOrWhiteSpace(p.ApiKey) ? throw new AiUnavailableException($"The AI provider '{p.Name}' has no API key.") : p.ApiKey;

    private static OpenAIClient OpenAi(AiProviderSettings p)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(p.BaseUrl)) options.Endpoint = new Uri(p.BaseUrl);
        if (string.IsNullOrWhiteSpace(p.ApiKey)) throw new AiUnavailableException($"The AI provider '{p.Name}' has no API key.");
        return new OpenAIClient(new ApiKeyCredential(p.ApiKey), options);
    }
}

#pragma warning disable OPENAI001 // some audio/image options are marked experimental in the OpenAI SDK

internal sealed class OpenAiSpeech(AudioClient client) : ITextToSpeech
{
    public async Task<MediaFile> SpeakAsync(string text, string voice, CancellationToken ct)
    {
        var result = await client.GenerateSpeechAsync(text, new GeneratedSpeechVoice(voice),
            new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Mp3 }, ct);
        return new MediaFile(result.Value.ToArray(), "audio/mpeg", "mp3");
    }
}

internal sealed class OpenAiImages(ImageClient client, string model) : IImageGenerator
{
    private static readonly HttpClient Download = new();

    public async Task<MediaFile> PaintAsync(string prompt, ImageShape shape, CancellationToken ct)
    {
        var (width, height) = ImageSizes.For(model, shape);
        var result = await client.GenerateImageAsync(prompt, new ImageGenerationOptions { Size = new GeneratedImageSize(width, height) }, ct);
        var image = result.Value;

        // Newer image models return the bytes directly; older ones return a short-lived URL.
        var bytes = image.ImageBytes?.ToArray()
            ?? (image.ImageUri is { } uri ? await Download.GetByteArrayAsync(uri, ct) : throw new AiCallFailedException("The image service returned no image."));
        return new MediaFile(bytes, "image/png", "png");
    }
}

#pragma warning restore OPENAI001

/// <summary>
/// Each OpenAI image model accepts its own fixed list of sizes, and rejects any
/// other with an error, so we pick the closest tall or wide size the model supports.
/// </summary>
public static class ImageSizes
{
    public static (int Width, int Height) For(string model, ImageShape shape)
    {
        var tall = shape == ImageShape.Portrait;
        if (model.StartsWith("dall-e-3", StringComparison.OrdinalIgnoreCase)) return tall ? (1024, 1792) : (1792, 1024);
        if (model.StartsWith("dall-e-2", StringComparison.OrdinalIgnoreCase)) return (1024, 1024); // squares only
        // gpt-image-1 and its successors.
        return tall ? (1024, 1536) : (1536, 1024);
    }
}

/// <summary>A short, valid, silent WAV file. Lets tests exercise the whole voice pipeline without a provider.</summary>
public sealed class FakeSpeech : ITextToSpeech
{
    public Task<MediaFile> SpeakAsync(string text, string voice, CancellationToken ct)
    {
        const int sampleRate = 8000;
        var silence = Enumerable.Repeat((byte)128, sampleRate / 4).ToArray(); // a quarter of a second; 128 = silence in 8-bit PCM
        return Task.FromResult(new MediaFile(Wav.Build(silence, sampleRate, channels: 1, bitsPerSample: 8), "audio/wav", "wav"));
    }
}

/// <summary>A gradient PNG whose colour comes from the prompt. Deterministic, so caching can be tested.</summary>
public sealed class FakeImages : IImageGenerator
{
    public Task<MediaFile> PaintAsync(string prompt, ImageShape shape, CancellationToken ct)
    {
        var (w, h) = shape == ImageShape.Portrait ? (160, 200) : (320, 180);
        var hue = (uint)prompt.Aggregate(17, (a, c) => a * 31 + c) % 360;
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h),
                [SKColor.FromHsl(hue, 45, 35), SKColor.FromHsl((hue + 40) % 360, 50, 12)], SKShaderTileMode.Clamp),
        };
        surface.Canvas.DrawRect(0, 0, w, h, paint);
        using var png = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        return Task.FromResult(new MediaFile(png.ToArray(), "image/png", "png"));
    }
}

/// <summary>
/// Picks a voice for each character from their <see cref="VoiceProfile"/>. The
/// same character always gets the same voice, deeper voices go to low-pitched
/// characters, and the narrator has its own. These six voices (alloy, echo,
/// fable, onyx, nova, shimmer) work on every OpenAI text-to-speech model.
/// </summary>
public static class VoiceCasting
{
    public const string Narrator = "fable";
    private static readonly string[] Low = ["onyx", "echo"];
    private static readonly string[] High = ["nova", "shimmer", "alloy"];

    public static string For(string characterId, VoiceProfile profile)
    {
        var pool = profile.Pitch < 1.0 ? Low : High;
        var index = (int)((uint)characterId.Aggregate(7, (a, c) => a * 31 + c) % (uint)pool.Length);
        return pool[index];
    }
}
