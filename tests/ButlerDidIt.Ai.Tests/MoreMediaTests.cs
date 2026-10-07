using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ButlerDidIt.Ai.Media;

namespace ButlerDidIt.Ai.Tests;

/// <summary>
/// The voice and picture providers added in #33: ElevenLabs, and the local Piper and Stable Diffusion servers,
/// against stand-ins for their APIs (the requests we send, and what we make of the replies), plus which roles
/// each kind of provider can take. No network, no key.
/// </summary>
public class MoreMediaTests
{
    /// <summary>Answers by path (the first route the request's path and query start with), and remembers every request.</summary>
    private sealed class Stub(params (string Path, HttpStatusCode Status, string ContentType, byte[] Body)[] routes) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var route = routes.FirstOrDefault(r => request.RequestUri!.PathAndQuery.StartsWith(r.Path, StringComparison.Ordinal));
            if (route.Path is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var content = new ByteArrayContent(route.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(route.ContentType);
            return new HttpResponseMessage(route.Status) { Content = content };
        }

        public (HttpRequestMessage Request, string Body) To(string path) => Requests.Last(r => r.Request.RequestUri!.AbsolutePath.StartsWith(path, StringComparison.Ordinal));
    }

    private static (string, HttpStatusCode, string, byte[]) Json(string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        (path, status, "application/json", Encoding.UTF8.GetBytes(json));

    private static (string, HttpStatusCode, string, byte[]) File(string path, string type, byte[] bytes) => (path, HttpStatusCode.OK, type, bytes);

    private static MediaClientFactory Factory(Stub stub) => new(allowFake: false, new HttpClient(stub));

    private static readonly byte[] Mp3 = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 1, 2, 3];
    private static readonly byte[] Wav = ButlerDidIt.Ai.Media.Wav.FromPcm16(new byte[480], 22_050);
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 9, 9];

    // ---- ElevenLabs

    // Each test has its own key: the voice list is kept per key for an hour.
    private static AiProviderSettings ElevenLabs() => new(Guid.NewGuid(), "ElevenLabs", AiProviderKind.ElevenLabs, null, $"xi-{Guid.NewGuid():N}");

    private const string AccountVoices = """
        {"voices":[
          {"voice_id":"id-george","name":"George","labels":{"gender":"male"}},
          {"voice_id":"id-daniel","name":"Daniel - Steady Broadcaster","labels":{"gender":"male"}},
          {"voice_id":"id-sarah","name":"Sarah","labels":{"gender":"female"}},
          {"voice_id":"id-georgette","name":"Georgette","labels":{"gender":"female"}}
        ]}
        """;

    [Fact]
    public async Task An_elevenlabs_clip_is_an_mp3_in_the_narrators_default_voice()
    {
        var stub = new Stub(Json("/v1/voices", AccountVoices), File("/v1/text-to-speech/", "audio/mpeg", Mp3));
        var provider = ElevenLabs();

        var file = await Factory(stub).CreateSpeech(provider, "eleven_multilingual_v2").SpeakAsync("Good evening, detectives.", VoiceCasting.Narrator, CancellationToken.None);

        Assert.Equal(Mp3, file.Bytes);
        Assert.Equal(("audio/mpeg", "mp3"), (file.ContentType, file.Extension));
        var (request, body) = stub.To("/v1/text-to-speech/");
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/id-george?output_format=mp3_44100_128", request.RequestUri!.ToString());
        Assert.Equal(provider.ApiKey, request.Headers.GetValues("xi-api-key").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("Good evening, detectives.", json["text"]!.GetValue<string>());
        Assert.Equal("eleven_multilingual_v2", json["model_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_account_list_is_read_once_and_deep_voices_go_to_deep_defaults()
    {
        var stub = new Stub(Json("/v1/voices", AccountVoices), File("/v1/text-to-speech/", "audio/mpeg", Mp3));
        var speech = Factory(stub).CreateSpeech(ElevenLabs(), "eleven_flash_v2_5");
        await speech.SpeakAsync("Hello.", "onyx", CancellationToken.None);
        Assert.Contains("/text-to-speech/id-daniel", stub.To("/v1/text-to-speech/").Request.RequestUri!.AbsolutePath); // "Daniel - Steady Broadcaster"
        await speech.SpeakAsync("Hello.", "nova", CancellationToken.None);
        Assert.Contains("/text-to-speech/id-sarah", stub.To("/v1/text-to-speech/").Request.RequestUri!.AbsolutePath);
        Assert.Single(stub.Requests, r => r.Request.RequestUri!.AbsolutePath == "/v1/voices");
    }

    [Fact]
    public void Without_the_default_voices_the_accounts_own_are_cast_by_gender()
    {
        ElevenLabsVoices.Voice[] own = [new("m1", "Grandpa Joe", "male"), new("f1", "Aunt May", "female")];
        Assert.Equal("m1", ElevenLabsVoices.Pick(own, "onyx"));    // deep
        Assert.Equal("m1", ElevenLabsVoices.Pick(own, "fable"));   // the narrator
        Assert.Equal("f1", ElevenLabsVoices.Pick(own, "shimmer")); // light
        Assert.Equal("my-cloned-voice", ElevenLabsVoices.Pick(own, "my-cloned-voice")); // a voice id the admin chose is used as it is
        // A name is matched whole: "Georgette" is never George, so the narrator gets the account's man's voice.
        Assert.Equal("m1", ElevenLabsVoices.Pick([new("f9", "Georgette", "female"), new("m1", "Grandpa Joe", "male")], VoiceCasting.Narrator));
    }

    [Fact]
    public async Task A_key_that_cant_list_voices_still_speaks_in_a_documented_default_voice()
    {
        var stub = new Stub(Json("/v1/voices", """{"detail":{"status":"missing_permissions","message":"voices_read"}}""", HttpStatusCode.Unauthorized),
            File("/v1/text-to-speech/", "audio/mpeg", Mp3));
        await Factory(stub).CreateSpeech(ElevenLabs(), "eleven_multilingual_v2").SpeakAsync("Hello.", VoiceCasting.Narrator, CancellationToken.None);
        Assert.Contains("/text-to-speech/JBFqnCBsd6RMkjVDRZzb", stub.To("/v1/text-to-speech/").Request.RequestUri!.AbsolutePath); // George
    }

    [Fact]
    public async Task Elevenlabs_errors_are_passed_on_in_its_own_words()
    {
        var stub = new Stub(Json("/v1/voices", AccountVoices),
            Json("/v1/text-to-speech/", """{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota."}}""", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            Factory(stub).CreateSpeech(ElevenLabs(), "eleven_multilingual_v2").SpeakAsync("Hello.", "nova", CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.Contains("exceeds your quota", ex.Message);
    }

    // ---- Piper

    // Each test has its own server: speaker counts are kept per server for a few minutes.
    private static AiProviderSettings Piper() => new(Guid.NewGuid(), "Piper", AiProviderKind.Piper, $"http://piper-{Guid.NewGuid():N}:5000/", null);

    [Fact]
    public async Task A_piper_voice_with_many_speakers_gives_each_character_their_own()
    {
        var stub = new Stub(Json("/voices", """{"en_GB-vctk-medium":{"num_speakers":109},"en_US-lessac-medium":{"num_speakers":1}}"""),
            File("/synthesize", "audio/wav", Wav));
        var provider = Piper();

        var file = await Factory(stub).CreateSpeech(provider, "en_GB-vctk-medium").SpeakAsync("Who goes there?", "onyx", CancellationToken.None);

        Assert.Equal(("audio/wav", "wav"), (file.ContentType, file.Extension));
        var (request, body) = stub.To("/synthesize");
        Assert.Equal($"{provider.BaseUrl!.TrimEnd('/')}/synthesize", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("Who goes there?", json["text"]!.GetValue<string>());
        Assert.Equal("en_GB-vctk-medium", json["voice"]!.GetValue<string>());
        Assert.Equal(1, json["speaker_id"]!.GetValue<int>()); // onyx is the second of the six
    }

    [Fact]
    public async Task A_single_speaker_voice_sends_no_speaker_and_default_means_the_servers_own_voice()
    {
        var stub = new Stub(Json("/info", """{"voice":{"name":"en_US-lessac-medium","num_speakers":1}}"""), File("/synthesize", "audio/wav", Wav));
        await Factory(stub).CreateSpeech(Piper(), "default").SpeakAsync("Hello.", "shimmer", CancellationToken.None);
        var json = JsonNode.Parse(stub.To("/synthesize").Body)!.AsObject();
        Assert.False(json.ContainsKey("voice"));
        Assert.False(json.ContainsKey("speaker_id"));
    }

    [Fact]
    public void Speakers_always_exist_in_the_voice()
    {
        Assert.Null(PiperVoices.Speaker("onyx", 1));
        Assert.Equal(1, PiperVoices.Speaker("onyx", 2)); // never 2: Piper's own check lets one too many through
        Assert.Equal(0, PiperVoices.Speaker("nova", 3)); // 3 % 3
        Assert.All(new[] { "fable", "onyx", "echo", "nova", "shimmer", "alloy", "someone-else" }, v => Assert.InRange(PiperVoices.Speaker(v, 4)!.Value, 0, 3));
    }

    [Fact]
    public async Task An_older_piper_server_is_sent_the_text_at_its_root()
    {
        var stubbed = new Stub(("/synthesize", HttpStatusCode.NotFound, "text/plain", []), ("/voices", HttpStatusCode.NotFound, "text/plain", []),
            ("/info", HttpStatusCode.NotFound, "text/plain", []), File("/", "audio/wav", Wav));
        var file = await Factory(stubbed).CreateSpeech(Piper(), "en_US-lessac-medium").SpeakAsync("Hello there.", "nova", CancellationToken.None);
        Assert.True(ButlerDidIt.Ai.Media.Wav.IsWav(file.Bytes));
        var root = stubbed.Requests.Last();
        Assert.Equal("/", root.Request.RequestUri!.AbsolutePath);
        Assert.Equal("Hello there.", root.Body);
    }

    [Fact]
    public async Task A_server_that_isnt_piper_is_named_as_the_problem()
    {
        var stub = new Stub(File("/synthesize", "text/html", Encoding.UTF8.GetBytes("<html>Welcome to nginx</html>")));
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            Factory(stub).CreateSpeech(Piper(), "en_US-lessac-medium").SpeakAsync("Hello.", "nova", CancellationToken.None));
        Assert.Contains("Is it a Piper server?", ex.Message);
    }

    // ---- Stable Diffusion WebUI

    private static AiProviderSettings StableDiffusion() => new(Guid.NewGuid(), "SD", AiProviderKind.StableDiffusion, "http://sd:7860", null);

    private static string Txt2Img(byte[] image, string prefix = "") =>
        new JsonObject { ["images"] = new JsonArray(prefix + Convert.ToBase64String(image)), ["parameters"] = new JsonObject(), ["info"] = "{}" }.ToJsonString();

    [Fact]
    public async Task A_stable_diffusion_picture_is_asked_for_at_its_models_size_with_its_checkpoint()
    {
        var stub = new Stub(Json("/sdapi/v1/txt2img", Txt2Img(Png)));
        var file = await Factory(stub).CreateImages(StableDiffusion(), "sd_xl_base_1.0").PaintAsync("A foggy harbour at dawn", ImageShape.Landscape, CancellationToken.None);

        Assert.Equal(Png, file.Bytes);
        Assert.Equal("image/png", file.ContentType);
        var (request, body) = stub.To("/sdapi/v1/txt2img");
        Assert.Equal("http://sd:7860/sdapi/v1/txt2img", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("A foggy harbour at dawn", json["prompt"]!.GetValue<string>());
        Assert.Contains("watermark", json["negative_prompt"]!.GetValue<string>());
        Assert.Equal((1216, 832), (json["width"]!.GetValue<int>(), json["height"]!.GetValue<int>())); // SDXL, wide
        Assert.Equal("sd_xl_base_1.0", json["override_settings"]!["sd_model_checkpoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_default_checkpoint_is_left_alone_and_a_data_url_reply_is_understood()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 };
        var stub = new Stub(Json("/sdapi/v1/txt2img", Txt2Img(jpeg, "data:image/jpeg;base64,")));
        var file = await Factory(stub).CreateImages(StableDiffusion(), "default").PaintAsync("A portrait", ImageShape.Portrait, CancellationToken.None);
        Assert.Equal(("image/jpeg", "jpg"), (file.ContentType, file.Extension));
        var json = JsonNode.Parse(stub.To("/sdapi/v1/txt2img").Body)!.AsObject();
        Assert.False(json.ContainsKey("override_settings"));
        Assert.Equal((512, 768), (json["width"]!.GetValue<int>(), json["height"]!.GetValue<int>())); // SD 1.5 size, tall
    }

    [Fact]
    public async Task A_webui_without_its_api_or_out_of_memory_says_so()
    {
        var noApi = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            Factory(new Stub()).CreateImages(StableDiffusion(), "default").PaintAsync("x", ImageShape.Landscape, CancellationToken.None));
        Assert.Contains("--api", noApi.Message);

        var oom = new Stub(Json("/sdapi/v1/txt2img", """{"error":"OutOfMemoryError","detail":"","body":"","errors":"CUDA out of memory. Tried to allocate 2.00 GiB"}""",
            HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            Factory(oom).CreateImages(StableDiffusion(), "default").PaintAsync("x", ImageShape.Landscape, CancellationToken.None));
        Assert.Contains("CUDA out of memory", ex.Message);
    }

    [Fact]
    public async Task The_connection_test_lists_checkpoints_instead_of_painting()
    {
        var stub = new Stub(Json("/sdapi/v1/sd-models", """[{"title":"sd_xl_base_1.0.safetensors [31e35c80fc]","model_name":"sd_xl_base_1.0"},{"title":"v1-5-pruned-emaonly.safetensors [6ce0161689]"}]"""));
        var factory = Factory(stub);
        Assert.Contains("is one of its checkpoints", await factory.CheckAsync(StableDiffusion(), "sd_xl_base_1.0", CancellationToken.None));
        Assert.Contains("2 checkpoint(s)", await factory.CheckAsync(StableDiffusion(), "default", CancellationToken.None));
        var missing = await Assert.ThrowsAsync<AiCallFailedException>(() => factory.CheckAsync(StableDiffusion(), "flux1-dev", CancellationToken.None));
        Assert.Contains("v1-5-pruned-emaonly", missing.Message);
        Assert.DoesNotContain(stub.Requests, r => r.Request.RequestUri!.AbsolutePath.Contains("txt2img"));
    }

    [Fact]
    public void Model_names_choose_the_picture_size()
    {
        Assert.Equal((832, 1216), StableDiffusionSizes.For("juggernautXL_v9", ImageShape.Portrait));
        Assert.Equal((1216, 832), StableDiffusionSizes.For("flux1-dev-fp8", ImageShape.Landscape));
        Assert.Equal((768, 512), StableDiffusionSizes.For("dreamshaper_8", ImageShape.Landscape));
    }

    // ---- Which provider does which job

    [Fact]
    public void Each_kind_of_provider_takes_only_the_roles_it_can_do()
    {
        Assert.True(AiProviderAbilities.Can(AiProviderKind.ElevenLabs, AiRole.Voice));
        Assert.True(AiProviderAbilities.Can(AiProviderKind.Piper, AiRole.Voice));
        Assert.False(AiProviderAbilities.Can(AiProviderKind.Piper, AiRole.Illustrator));
        Assert.True(AiProviderAbilities.Can(AiProviderKind.StableDiffusion, AiRole.Illustrator));
        Assert.False(AiProviderAbilities.Can(AiProviderKind.StableDiffusion, AiRole.Voice));
        Assert.All(new[] { AiRole.Storyteller, AiRole.Actor, AiRole.Inspector }, role =>
        {
            Assert.False(AiProviderAbilities.Can(AiProviderKind.ElevenLabs, role));
            Assert.True(AiProviderAbilities.Can(AiProviderKind.Anthropic, role));
        });
        Assert.False(AiProviderAbilities.Can(AiProviderKind.Ollama, AiRole.Voice));
        Assert.True(AiProviderAbilities.Local(AiProviderKind.Piper) && AiProviderAbilities.Local(AiProviderKind.StableDiffusion));
        Assert.False(AiProviderAbilities.Local(AiProviderKind.ElevenLabs));
    }

    [Fact]
    public void A_voice_provider_is_refused_for_chat_in_words_that_say_why()
    {
        var ex = Assert.Throws<AiUnavailableException>(() => new ChatClientFactory(allowFake: false).Create(ElevenLabs(), "eleven_multilingual_v2"));
        Assert.Contains("makes voices, not chat", ex.Message);
    }

    [Fact]
    public void A_local_openai_compatible_server_needs_no_key_but_openai_itself_does()
    {
        var factory = new MediaClientFactory(allowFake: false);
        var local = new AiProviderSettings(Guid.NewGuid(), "Kokoro", AiProviderKind.OpenAI, "http://kokoro:8880/v1", null);
        Assert.NotNull(factory.CreateSpeech(local, "kokoro"));
        Assert.NotNull(factory.CreateImages(local with { Name = "LocalAI" }, "stablediffusion"));
        Assert.Contains("no API key", Assert.Throws<AiUnavailableException>(() => factory.CreateSpeech(local with { BaseUrl = null }, "tts-1")).Message);
        Assert.Contains("no API key", Assert.Throws<AiUnavailableException>(() => factory.CreateSpeech(ElevenLabs() with { ApiKey = null }, "x")).Message);
    }
}
