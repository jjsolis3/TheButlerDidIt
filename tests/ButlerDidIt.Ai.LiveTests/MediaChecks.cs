using System.Buffers.Binary;
using System.Diagnostics;
using ButlerDidIt.Ai.Media;
using Xunit.Abstractions;

namespace ButlerDidIt.Ai.LiveTests;

/// <summary>
/// The #32 checklist: a voice comes back as audio the stage can play, and portrait and landscape pictures
/// come back the right way round. (Safety refusals and the cost log are checked by hand, on a real party's
/// preparation; see docs/verifying-providers.md.)
/// </summary>
public static class MediaChecks
{
    private static readonly MediaClientFactory Factory = new(allowFake: false);

    public static async Task Speaks(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        var watch = Stopwatch.StartNew();
        var file = await Factory.CreateSpeech(provider, model).SpeakAsync("Good evening, detectives. The butler did not do it.", VoiceCasting.Narrator, CancellationToken.None);
        Report.Add(output, name, "speaks", model, $"{file.Bytes.Length / 1024} KB {file.ContentType}", ms: watch.ElapsedMilliseconds);
        Assert.StartsWith("audio/", file.ContentType);
        Assert.True(file.Bytes.Length > 2_000, "a sentence of speech is more than a couple of kilobytes");
        Assert.True(IsMp3(file.Bytes) || IsWav(file.Bytes), $"the bytes are playable audio ({file.ContentType})");
    }

    public static async Task Paints(ITestOutputHelper output, string name, AiProviderSettings provider, string model, ImageShape shape)
    {
        var watch = Stopwatch.StartNew();
        var file = await Factory.CreateImages(provider, model).PaintAsync(
            "A candlelit Victorian library at night, painted in a warm storybook style, no text", shape, CancellationToken.None);
        var (width, height) = Size(file.Bytes);
        Report.Add(output, name, $"paints {shape.ToString().ToLowerInvariant()}", model, $"{width}×{height} {file.ContentType}", ms: watch.ElapsedMilliseconds);
        Assert.StartsWith("image/", file.ContentType);
        Assert.True(width > 0 && height > 0, "a PNG, JPEG or WebP picture");
        if (shape == ImageShape.Portrait) Assert.True(height > width, $"a portrait is taller than wide ({width}×{height})");
        else Assert.True(width > height, $"a landscape is wider than tall ({width}×{height})");
    }

    /// <summary>One clip from a painted picture, as a stage's reveal gets (#110): an MP4 of a few seconds.</summary>
    public static async Task Films(ITestOutputHelper output, string name, AiProviderSettings provider, string model)
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1536, 1024));
        surface.Canvas.Clear(new SkiaSharp.SKColor(40, 30, 60));
        using var png = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        var watch = Stopwatch.StartNew();
        var clip = await Factory.CreateVideos(provider, model).AnimateAsync(new MediaFile(png.ToArray(), "image/png", "png"),
            "Bring this still picture of an empty, candlelit escape room to life with a slow camera push-in. No people, no text.", CancellationToken.None);
        Report.Add(output, name, "films a clip", model, $"{clip.Bytes.Length / 1024} KB {clip.ContentType}", ms: watch.ElapsedMilliseconds);
        Assert.Equal("video/mp4", clip.ContentType);
        Assert.True(clip.Bytes.Length > 50_000, "a few seconds of video is more than 50 KB");
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(clip.Bytes, 4, 4)); // an MP4
    }

    private static bool IsMp3(byte[] b) => b.Length > 3 && (b[0] == 'I' && b[1] == 'D' && b[2] == '3' || b[0] == 0xFF && (b[1] & 0xE0) == 0xE0);
    private static bool IsWav(byte[] b) => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'A' && b[10] == 'V' && b[11] == 'E';

    /// <summary>Width and height from a PNG, JPEG or WebP header; (0, 0) when it's none of those.</summary>
    public static (int Width, int Height) Size(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            return (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)));
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            // Walk the JPEG segments to the start-of-frame marker, which holds the size.
            for (var i = 2; i + 9 < b.Length;)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                var length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 5)));
                i += 2 + length;
            }
        }
        if (b.Length > 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' && b[12] == 'V' && b[13] == 'P' && b[14] == '8')
        {
            if (b[15] == 'X') return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
            if (b[15] == ' ') return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3FFF);
            if (b[15] == 'L') { var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21)); return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1); }
        }
        return (0, 0);
    }
}

public class OpenAiMediaLiveTests(ITestOutputHelper output)
{
    private const string Name = "OpenAI";

    [LiveFact(Live.OpenAiKey)] public Task Speaks() => MediaChecks.Speaks(output, Name, Live.OpenAi, Live.OpenAiSpeechModel);
    [LiveFact(Live.OpenAiKey)] public Task Paints_a_portrait() => MediaChecks.Paints(output, Name, Live.OpenAi, Live.OpenAiImageModel, ImageShape.Portrait);
    [LiveFact(Live.OpenAiKey)] public Task Paints_a_landscape() => MediaChecks.Paints(output, Name, Live.OpenAi, Live.OpenAiImageModel, ImageShape.Landscape);
    [LiveFact(Live.OpenAiKey, Live.Video)] public Task Films_a_clip() => MediaChecks.Films(output, Name, Live.OpenAi, Live.OpenAiVideoModel);
}

public class GeminiMediaLiveTests(ITestOutputHelper output)
{
    private const string Name = "Gemini";

    [LiveFact(Live.GeminiKey)] public Task Speaks() => MediaChecks.Speaks(output, Name, Live.Gemini, Live.GeminiSpeechModel);
    [LiveFact(Live.GeminiKey)] public Task Paints_a_portrait() => MediaChecks.Paints(output, Name, Live.Gemini, Live.GeminiImageModel, ImageShape.Portrait);
    [LiveFact(Live.GeminiKey)] public Task Paints_a_landscape() => MediaChecks.Paints(output, Name, Live.Gemini, Live.GeminiImageModel, ImageShape.Landscape);
    [LiveFact(Live.GeminiKey, Live.Video)] public Task Films_a_clip() => MediaChecks.Films(output, Name, Live.Gemini, Live.GeminiVideoModel);
}

/// <summary>ElevenLabs (#33): the voice is cast from the account's own list, and comes back as an MP3.</summary>
public class ElevenLabsMediaLiveTests(ITestOutputHelper output)
{
    [LiveFact(Live.ElevenLabsKey)] public Task Speaks() => MediaChecks.Speaks(output, "ElevenLabs", Live.ElevenLabs, Live.ElevenLabsModel);
}

/// <summary>The local servers (#33): Piper's voices and a Stable Diffusion WebUI's pictures. Free, so every check runs.</summary>
public class LocalMediaLiveTests(ITestOutputHelper output)
{
    [LiveFact(Live.PiperUrl)] public Task Piper_speaks() => MediaChecks.Speaks(output, "Piper", Live.Piper, Live.PiperVoice);
    [LiveFact(Live.StableDiffusionUrl)] public Task Stable_diffusion_paints_a_portrait() =>
        MediaChecks.Paints(output, "Stable Diffusion", Live.StableDiffusion, Live.StableDiffusionModel, ImageShape.Portrait);
    [LiveFact(Live.StableDiffusionUrl)] public Task Stable_diffusion_paints_a_landscape() =>
        MediaChecks.Paints(output, "Stable Diffusion", Live.StableDiffusion, Live.StableDiffusionModel, ImageShape.Landscape);
}

/// <summary>The image-size reader is itself checked offline, so a live failure is about the service, not the parser.</summary>
public class ImageSizeTests
{
    [Fact]
    public void Reads_png_sizes()
    {
        var png = new byte[32];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 1024);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 1536);
        Assert.Equal((1024, 1536), MediaChecks.Size(png));
    }

    [Fact]
    public void Reads_jpeg_sizes()
    {
        // SOI, an APP0 segment of 16 bytes, then a baseline start-of-frame: 8-bit, height 1024, width 1792.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. new byte[14], 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x04, 0x00, 0x07, 0x00, 0x03, 0, 0, 0, 0];
        Assert.Equal((1792, 1024), MediaChecks.Size(jpeg));
    }

    [Fact]
    public void Anything_else_has_no_size() => Assert.Equal((0, 0), MediaChecks.Size([1, 2, 3, 4]));
}
