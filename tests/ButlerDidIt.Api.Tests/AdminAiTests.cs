using System.Net;
using System.Text;
using System.Text.Json;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// The admin AI page, called exactly the way the browser calls it: raw JSON with
/// camelCase enum values, including in the URL (e.g. /roles/storyteller).
/// </summary>
public class AdminAiTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Admin_can_assign_providers_to_roles_from_the_settings_page()
    {
        // A fresh database per test class, so this first account is the admin.
        var (admin, _) = await app.RegisterHostAsync("admin@example.com");

        var created = await admin.PostAsync("/api/admin/ai/providers",
            Json("""{"name":"Gemini3.8","kind":"gemini","baseUrl":null,"apiKey":"test-key"}"""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var providerId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        // Every role name the page sends, in the camelCase the page uses.
        foreach (var role in new[] { "storyteller", "actor", "inspector" })
        {
            var saved = await admin.PutAsync($"/api/admin/ai/roles/{role}",
                Json($$"""{"providerId":"{{providerId}}","model":"gemini-3.8-flash","maxOutputTokens":null,"temperature":null}"""));
            Assert.True(saved.StatusCode == HttpStatusCode.NoContent, $"{role}: {(int)saved.StatusCode} {await saved.Content.ReadAsStringAsync()}");
        }

        var roles = await admin.GetStringAsync("/api/admin/ai/roles");
        Assert.Contains("\"role\":\"storyteller\",\"providerId\":\"" + providerId, roles);

        // Clearing a role works with the same spelling.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/ai/roles/actor")).StatusCode);

        // Mistakes get a readable message, not a bare "400".
        var bad = await admin.PutAsync("/api/admin/ai/roles/butler",
            Json($$"""{"providerId":"{{providerId}}","model":"x","maxOutputTokens":null,"temperature":null}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Unknown role", await bad.Content.ReadAsStringAsync());

        // Gemini makes voices and pictures too (its TTS and Nano Banana image models).
        foreach (var (role, model) in new[] { ("voice", "gemini-2.5-flash-preview-tts"), ("illustrator", "gemini-2.5-flash-image") })
        {
            var media = await admin.PutAsync($"/api/admin/ai/roles/{role}",
                Json($$"""{"providerId":"{{providerId}}","model":"{{model}}","maxOutputTokens":null,"temperature":null}"""));
            Assert.True(media.StatusCode == HttpStatusCode.NoContent, $"{role}: {(int)media.StatusCode} {await media.Content.ReadAsStringAsync()}");
        }

        // A provider with no voice or image API is still refused, with a reason.
        var local = await admin.PostAsync("/api/admin/ai/providers",
            Json("""{"name":"Local","kind":"ollama","baseUrl":"http://localhost:11434","apiKey":null}"""));
        var localId = JsonDocument.Parse(await local.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        var voice = await admin.PutAsync("/api/admin/ai/roles/voice",
            Json($$"""{"providerId":"{{localId}}","model":"tts-1","maxOutputTokens":null,"temperature":null}"""));
        Assert.Equal(HttpStatusCode.BadRequest, voice.StatusCode);
        Assert.Contains("needs an OpenAI, Gemini, ElevenLabs or Piper provider", await voice.Content.ReadAsStringAsync());

        // Claude's options (#63): effort and a refusal fallback, saved, shown back, and refused for other providers.
        var claude = await admin.PostAsync("/api/admin/ai/providers",
            Json("""{"name":"Claude","kind":"anthropic","baseUrl":null,"apiKey":"sk-ant-test"}"""));
        var claudeId = JsonDocument.Parse(await claude.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        var actor = await admin.PutAsync("/api/admin/ai/roles/actor",
            Json($$"""{"providerId":"{{claudeId}}","model":"claude-opus-5-5","maxOutputTokens":null,"temperature":null,"effort":"Low","refusalFallbackModel":" claude-opus-4-8 "}"""));
        Assert.True(actor.StatusCode == HttpStatusCode.NoContent, $"{(int)actor.StatusCode} {await actor.Content.ReadAsStringAsync()}");
        var saved2 = await admin.GetStringAsync("/api/admin/ai/roles");
        Assert.Contains("\"model\":\"claude-opus-5-5\",\"maxOutputTokens\":null,\"temperature\":null,\"effort\":\"low\",\"refusalFallbackModel\":\"claude-opus-4-8\"", saved2);

        var turbo = await admin.PutAsync("/api/admin/ai/roles/actor",
            Json($$"""{"providerId":"{{claudeId}}","model":"claude-opus-5-5","maxOutputTokens":null,"temperature":null,"effort":"turbo"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, turbo.StatusCode);
        Assert.Contains("Effort must be one of: low, medium, high, xhigh", await turbo.Content.ReadAsStringAsync());

        var notClaude = await admin.PutAsync("/api/admin/ai/roles/storyteller",
            Json($$"""{"providerId":"{{providerId}}","model":"gemini-3.8-flash","maxOutputTokens":null,"temperature":null,"effort":"low"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, notClaude.StatusCode);
        Assert.Contains("are Claude settings", await notClaude.Content.ReadAsStringAsync());
    }
}
