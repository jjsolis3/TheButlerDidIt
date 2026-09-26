using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

public class ScenarioEditorTests(FakeAiFactory app) : IClassFixture<FakeAiFactory>
{
    private const string Blackwood = "death-at-blackwood-manor";

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<HttpClient> HostAsync(bool admin = false)
    {
        var email = $"e{Guid.NewGuid():N}@example.com";
        var (client, _) = await app.RegisterHostAsync(email);
        if (admin)
        {
            // Promote directly, so the test doesn't depend on being the first account in the database.
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsAdmin, true));
        }
        return client;
    }

    private static async Task<string> DuplicateAsync(HttpClient client, string id) =>
        (await Read<Dictionary<string, string>>(await client.PostAsync($"/api/scenarios/{id}/duplicate", null)))["id"];

    private static async Task<JsonObject> DocumentAsync(HttpClient client, string id) =>
        JsonNode.Parse(JsonSerializer.Serialize((await Read<EditableScenario>(await client.GetAsync($"/api/scenarios/{id}"))).Document))!.AsObject();

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string id, JsonNode document) =>
        client.PutAsync($"/api/scenarios/{id}", JsonContent.Create(new { document }));

    [Fact]
    public async Task Hand_written_mysteries_are_duplicated_not_edited_in_place()
    {
        var admin = await HostAsync(admin: true);
        var mine = await Read<List<MyMystery>>(await admin.GetAsync("/api/scenarios/mine"));
        Assert.False(mine.Single(m => m.Id == Blackwood).CanEdit);

        var original = await DocumentAsync(admin, Blackwood);
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(admin, Blackwood, original)).StatusCode);

        var copyId = await DuplicateAsync(admin, Blackwood);
        Assert.StartsWith("death-at-blackwood-manor-", copyId);
        var copy = await DocumentAsync(admin, copyId);
        Assert.Equal("Death at Blackwood Manor (copy)", copy["title"]!.GetValue<string>());

        // Rename it and reword a clue: saved, and shown on the new-party page as the host's own copy.
        copy["title"] = "Murder at Blackwood Grange";
        copy["clues"]![0]!["text"] = "A torn page from Reginald's diary.";
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(admin, copyId, copy)).StatusCode);
        var themes = await admin.GetStringAsync("/api/themes");
        Assert.Contains("Murder at Blackwood Grange", themes);
        Assert.Contains("\"custom\":true", themes);

        // Hosts other than the owner can't see it at all.
        var stranger = await HostAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/scenarios/{copyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/scenarios/{copyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/scenarios/{Blackwood}")).StatusCode); // solutions stay secret
    }

    [Fact]
    public async Task Broken_mysteries_are_explained_and_never_saved()
    {
        var admin = await HostAsync(admin: true);
        var copyId = await DuplicateAsync(admin, Blackwood);
        var doc = await DocumentAsync(admin, copyId);
        doc["solution"]!["murdererId"] = "nobody";

        var check = await Read<ValidationResult>(await admin.PostAsJsonAsync("/api/scenarios/validate", new { document = doc }));
        Assert.False(check.Valid);
        Assert.Contains(check.Errors, e => e.Contains("nobody"));

        var save = await SaveAsync(admin, copyId, doc);
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Equal("finch", (await DocumentAsync(admin, copyId))["solution"]!["murdererId"]!.GetValue<string>());

        // Unreadable documents get a message too, not a server error.
        doc["clues"]![0]!["act"] = "first";
        Assert.Contains("couldn't be read", (await Read<ValidationResult>(await admin.PostAsJsonAsync("/api/scenarios/validate", new { document = doc }))).Errors[0]);
        doc["solution"] = null;
        Assert.False((await Read<ValidationResult>(await admin.PostAsJsonAsync("/api/scenarios/validate", new { document = doc }))).Valid);
    }

    [Fact]
    public async Task Editing_a_line_revoices_only_that_line()
    {
        var admin = await HostAsync(admin: true);
        var copyId = await DuplicateAsync(admin, Blackwood);
        using (var scope = app.Services.CreateScope())
        {
            // Prepare the copy's media the way a party would, then wait for it.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ButlerDidIt.Api.Media.MediaWorker.EnqueueAsync(db, copyId, "test", TimeProvider.System, default);
        }
        await WaitForMediaAsync(copyId);
        var before = await MediaKeysAsync(copyId);
        var lineKey = before.Keys.First(k => k.StartsWith("line/"));
        var parts = lineKey.Split('/'); // line/{character}/{act}/{index}

        var doc = await DocumentAsync(admin, copyId);
        var character = doc["characters"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == parts[1])!;
        character["private"]!["lines"]![parts[2]]![int.Parse(parts[3])] = "An entirely new line, darling.";
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(admin, copyId, doc)).StatusCode);

        // Saving queues a new preparation job for what changed. When it's done, the edited line has a
        // new voice clip, and every other voice and picture is still the very same file.
        await WaitForMediaAsync(copyId);
        var after = await MediaKeysAsync(copyId);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.NotEqual(before[lineKey], after[lineKey]);
        Assert.All(before.Where(kv => kv.Key != lineKey), kv => Assert.Equal(kv.Value, after[kv.Key]));
    }

    [Fact]
    public async Task Deleting_is_blocked_mid_party_and_archives_mysteries_that_were_played()
    {
        var host = await HostAsync(admin: true);
        var unplayed = await DuplicateAsync(host, Blackwood);
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/scenarios/{unplayed}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/scenarios/{unplayed}")).StatusCode);

        var played = await DuplicateAsync(host, Blackwood);
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(played, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
        Assert.Equal(HttpStatusCode.Conflict, (await host.DeleteAsync($"/api/scenarios/{played}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SaveAsync(host, played, await DocumentAsync(host, played))).StatusCode);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Parties.Where(p => p.Code == party.Code).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PartyStatus.Finished));

        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/scenarios/{played}")).StatusCode);
        var row = await db.Scenarios.AsNoTracking().SingleAsync(s => s.Id == played);
        Assert.NotNull(row.ArchivedAt); // kept, so that party's recap still works
        Assert.DoesNotContain(await Read<List<MyMystery>>(await host.GetAsync("/api/scenarios/mine")), m => m.Id == played);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(played, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options)).StatusCode);
    }

    private async Task<Dictionary<string, Guid>> MediaKeysAsync(string scenarioId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == scenarioId).ToDictionaryAsync(m => m.Key, m => m.AssetId);
    }

    private async Task WaitForMediaAsync(string scenarioId)
    {
        for (var i = 0; i < 150; i++)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.MediaJobs.AsNoTracking().Where(j => j.ScenarioId == scenarioId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync();
            if (job is { Status: MediaJobStatus.Succeeded or MediaJobStatus.Failed }) return;
            await Task.Delay(200);
        }
        Assert.Fail("Media preparation did not finish in time.");
    }
}
