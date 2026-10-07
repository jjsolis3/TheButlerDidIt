using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ButlerDidIt.Api.Ai;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// The voice and picture providers added in #33 on Admin → AI, called the way the page calls them: each takes only
/// the roles it can do, and "Test connection" checks a voice or picture server with a call of its own kind.
/// </summary>
public class MediaProviderAdminTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<string> AddAsync(HttpClient admin, string name, string kind, string? baseUrl, string? apiKey)
    {
        var created = await admin.PostAsync("/api/admin/ai/providers", Json(JsonSerializer.Serialize(new { name, kind, baseUrl, apiKey })));
        Assert.True(created.StatusCode == HttpStatusCode.OK, $"{kind}: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> AssignAsync(HttpClient admin, string role, string providerId, string model) =>
        admin.PutAsync($"/api/admin/ai/roles/{role}", Json($$"""{"providerId":"{{providerId}}","model":"{{model}}","maxOutputTokens":null,"temperature":null}"""));

    [Fact]
    public async Task Voice_and_picture_providers_take_only_their_own_roles_and_test_with_their_own_calls()
    {
        // A fresh database per test class, so this first account is the admin.
        var (admin, _) = await app.RegisterHostAsync("media-admin@example.com");
        var elevenLabs = await AddAsync(admin, "ElevenLabs", "elevenLabs", null, "xi-test-key");
        // Port 9 ("discard") refuses the connection at once: no server needed, and nothing is sent anywhere.
        var piper = await AddAsync(admin, "Home Piper", "piper", "http://127.0.0.1:9", null);
        var stableDiffusion = await AddAsync(admin, "Home SD", "stableDiffusion", "http://127.0.0.1:9", null);

        var providers = await admin.GetStringAsync("/api/admin/ai/providers");
        Assert.Contains("\"kind\":\"elevenLabs\"", providers);
        Assert.Contains("\"kind\":\"stableDiffusion\"", providers);

        // Each does its own job…
        foreach (var (role, id, model) in new[] { ("voice", elevenLabs, "eleven_multilingual_v2"), ("voice", piper, "en_GB-vctk-medium"), ("illustrator", stableDiffusion, "default") })
        {
            var saved = await AssignAsync(admin, role, id, model);
            Assert.True(saved.StatusCode == HttpStatusCode.NoContent, $"{role}: {(int)saved.StatusCode} {await saved.Content.ReadAsStringAsync()}");
        }

        // …and is refused, with a reason, for the others.
        var story = await AssignAsync(admin, "storyteller", piper, "en_GB-vctk-medium");
        Assert.Equal(HttpStatusCode.BadRequest, story.StatusCode);
        Assert.Contains("needs a chat model", await story.Content.ReadAsStringAsync());
        var paint = await AssignAsync(admin, "illustrator", elevenLabs, "eleven_multilingual_v2");
        Assert.Equal(HttpStatusCode.BadRequest, paint.StatusCode);
        Assert.Contains("needs an OpenAI, Gemini or Stable Diffusion provider", await paint.Content.ReadAsStringAsync());
        var speak = await AssignAsync(admin, "voice", stableDiffusion, "default");
        Assert.Contains("ElevenLabs or Piper", await speak.Content.ReadAsStringAsync());

        // "Test connection" calls the voice or picture server itself (here nothing answers), never a chat model.
        foreach (var id in new[] { piper, stableDiffusion })
        {
            var test = await (await admin.PostAsJsonAsync($"/api/admin/ai/providers/{id}/test", new { model = "default" })).Content.ReadFromJsonAsync<TestResult>();
            Assert.False(test!.Ok);
            Assert.DoesNotContain("not chat", test.Message);
            Assert.Contains("127.0.0.1:9", test.Message);
        }
    }
}
