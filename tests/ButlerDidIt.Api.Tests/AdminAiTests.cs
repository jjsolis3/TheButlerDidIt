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

        var voice = await admin.PutAsync("/api/admin/ai/roles/voice",
            Json($$"""{"providerId":"{{providerId}}","model":"tts-1","maxOutputTokens":null,"temperature":null}"""));
        Assert.Contains("needs an OpenAI provider", await voice.Content.ReadAsStringAsync());
    }
}
