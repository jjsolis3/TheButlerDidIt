using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ButlerDidIt.Ai.Media;

namespace ButlerDidIt.Ai.Tests;

/// <summary>
/// Gemini's image (Nano Banana) and speech models, against a stand-in for Google's API: the requests we send,
/// and what we make of the replies. No network, no key.
/// </summary>
public class GeminiMediaTests
{
    private static readonly AiProviderSettings Gemini = new(Guid.NewGuid(), "Gemini", AiProviderKind.Gemini, null, "test-key");

    /// <summary>Answers every request with one reply, and remembers what it was sent.</summary>
    private sealed class StubGoogle(HttpStatusCode status, string reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public JsonNode? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static string InlineReply(string mimeType, byte[] data) => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = "Here is your picture." }, // image models may add a caption first
                    new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mimeType, ["data"] = Convert.ToBase64String(data) } }),
            },
            ["finishReason"] = "STOP",
        }),
    }.ToJsonString();

    private static (MediaClientFactory Factory, StubGoogle Google) Setup(HttpStatusCode status, string reply)
    {
        var google = new StubGoogle(status, reply);
        return (new MediaClientFactory(allowFake: false, new HttpClient(google)), google);
    }

    [Fact]
    public async Task A_picture_is_asked_for_in_the_right_shape_and_comes_back_as_the_image_itself()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        var (factory, google) = Setup(HttpStatusCode.OK, InlineReply("image/jpeg", jpeg));

        var file = await factory.CreateImages(Gemini, "gemini-2.5-flash-image").PaintAsync("A candlelit library", ImageShape.Portrait, CancellationToken.None);

        Assert.Equal(jpeg, file.Bytes);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("jpg", file.Extension);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-image:generateContent", google.Request!.RequestUri!.ToString());
        Assert.Equal("test-key", google.Request.Headers.GetValues("x-goog-api-key").Single());
        Assert.Equal("A candlelit library", google.Body!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Contains("IMAGE", google.Body["generationConfig"]!["responseModalities"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal("3:4", google.Body["generationConfig"]!["imageConfig"]!["aspectRatio"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scenes_for_the_tv_are_wide()
    {
        var (factory, google) = Setup(HttpStatusCode.OK, InlineReply("image/png", [0x89, 0x50]));
        var file = await factory.CreateImages(Gemini, "gemini-3.1-flash-image").PaintAsync("The ballroom", ImageShape.Landscape, CancellationToken.None);
        Assert.Equal("16:9", google.Body!["generationConfig"]!["imageConfig"]!["aspectRatio"]!.GetValue<string>());
        Assert.Equal("png", file.Extension);
    }

    [Fact]
    public async Task A_voice_clip_becomes_a_playable_wav_in_the_matching_gemini_voice()
    {
        var pcm = new byte[4800]; // 0.1 s of 24 kHz, 16-bit silence
        var (factory, google) = Setup(HttpStatusCode.OK, InlineReply("audio/L16;codec=pcm;rate=24000", pcm));

        var file = await factory.CreateSpeech(Gemini, "gemini-2.5-flash-preview-tts").SpeakAsync("Good evening, detectives.", VoiceCasting.Narrator, CancellationToken.None);

        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(file.Bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(file.Bytes, 8, 4));
        Assert.Equal(24_000, BitConverter.ToInt32(file.Bytes, 24)); // sample rate
        Assert.Equal(16, BitConverter.ToInt16(file.Bytes, 34));     // bits per sample
        Assert.Equal(pcm.Length, BitConverter.ToInt32(file.Bytes, 40)); // data size
        Assert.Equal(44 + pcm.Length, file.Bytes.Length);

        var config = google.Body!["generationConfig"]!;
        Assert.Equal("AUDIO", config["responseModalities"]![0]!.GetValue<string>());
        Assert.Equal("Orus", config["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!.GetValue<string>());
    }

    [Fact]
    public void Every_voice_the_app_casts_has_a_gemini_voice_and_deep_voices_stay_deep()
    {
        Assert.Equal("Charon", GeminiVoices.For("onyx"));
        Assert.Equal("Fenrir", GeminiVoices.For("echo"));
        Assert.Equal("Kore", GeminiVoices.For("nova"));
        Assert.All(new[] { "onyx", "echo", "fable", "nova", "shimmer", "alloy" }, v => Assert.NotEqual(v, GeminiVoices.For(v)));
        Assert.Equal("Zephyr", GeminiVoices.For("Zephyr")); // a Gemini voice name is used as it is
    }

    [Fact]
    public async Task A_blocked_picture_explains_itself()
    {
        var blocked = """{"candidates":[{"finishReason":"IMAGE_SAFETY","content":{"parts":[]}}]}""";
        var (factory, _) = Setup(HttpStatusCode.OK, blocked);
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            factory.CreateImages(Gemini, "gemini-2.5-flash-image").PaintAsync("x", ImageShape.Landscape, CancellationToken.None));
        Assert.Contains("IMAGE_SAFETY", ex.Message);
        Assert.Contains("safety filter", ex.Message);
    }

    [Fact]
    public async Task A_text_only_model_is_named_as_the_problem()
    {
        var textOnly = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"I can only write."}]}}]}""";
        var (factory, _) = Setup(HttpStatusCode.OK, textOnly);
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            factory.CreateSpeech(Gemini, "gemini-3.5-flash-lite").SpeakAsync("Hello", "nova", CancellationToken.None));
        Assert.Contains("gemini-3.5-flash-lite", ex.Message);
        Assert.Contains("voice clip", ex.Message);
    }

    [Fact]
    public async Task Googles_error_message_is_passed_on()
    {
        var error = """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}""";
        var (factory, _) = Setup(HttpStatusCode.BadRequest, error);
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            factory.CreateImages(Gemini, "gemini-2.5-flash-image").PaintAsync("x", ImageShape.Portrait, CancellationToken.None));
        Assert.Contains("API key not valid", ex.Message);
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public void Providers_without_voices_or_pictures_are_refused_clearly()
    {
        var factory = new MediaClientFactory(allowFake: false);
        var claude = Gemini with { Kind = AiProviderKind.Anthropic, Name = "Claude" };
        Assert.Contains("OpenAI, Gemini or Stable Diffusion", Assert.Throws<AiUnavailableException>(() => factory.CreateImages(claude, "x")).Message);
        Assert.False(AiProviderAbilities.Speaks(AiProviderKind.Ollama) || AiProviderAbilities.Paints(AiProviderKind.Ollama));
        Assert.True(AiProviderAbilities.Speaks(AiProviderKind.Gemini) && AiProviderAbilities.Paints(AiProviderKind.Gemini));
        Assert.Contains("no API key", Assert.Throws<AiUnavailableException>(() => factory.CreateSpeech(Gemini with { ApiKey = null }, "x")).Message);
    }
}
