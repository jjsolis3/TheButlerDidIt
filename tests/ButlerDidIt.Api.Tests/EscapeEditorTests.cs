using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>The escape room editor (#113): copies of any room, edits that are checked, editions and pictures kept right.</summary>
public class EscapeEditorTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<HttpClient> HostAsync() => (await app.RegisterHostAsync($"editor{Guid.NewGuid():N}@example.com")).Client;

    private static async Task<string> CopyAsync(HttpClient host, string id) =>
        (await Read<Dictionary<string, string>>(await host.PostAsync($"/api/escape-rooms/{id}/duplicate", null)))["id"];

    private static async Task<JsonObject> DocumentAsync(HttpClient host, string id) =>
        JsonNode.Parse((await Read<EditableRoom>(await host.GetAsync($"/api/escape-rooms/{id}/document"))).Document.GetRawText())!.AsObject();

    private static Task<HttpResponseMessage> SaveAsync(HttpClient host, string id, JsonObject doc) =>
        host.PutAsJsonAsync($"/api/escape-rooms/{id}/document", new RoomDocumentRequest(JsonSerializer.SerializeToElement(doc)), GameJson.Options);

    [Fact]
    public async Task Any_host_can_copy_a_built_in_room_and_edit_only_their_own_copy()
    {
        var host = await HostAsync();

        // A built-in room can be read (answers and all, behind the editor's spoiler warning) but not edited.
        var builtIn = await Read<EditableRoom>(await host.GetAsync("/api/escape-rooms/the-workshop/document"));
        Assert.True(builtIn.BuiltIn);
        Assert.False(builtIn.CanEdit);
        Assert.Contains("\"answers\"", builtIn.Document.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(host, "the-workshop", await DocumentAsync(host, "the-workshop"))).StatusCode);

        // The copy is the host's own: editable, on their shelf, a new room with its own leaderboards.
        var copy = await CopyAsync(host, "the-workshop");
        Assert.Matches("^the-workshop-[0-9a-f]{6}$", copy);
        var doc = await DocumentAsync(host, copy);
        Assert.Equal("The Workshop (copy)", doc["title"]!.GetValue<string>());
        Assert.Equal(1, doc["edition"]!.GetValue<int>());
        var shelf = GameJson.Deserialize<List<EscapeRoomSummary>>(await host.GetStringAsync("/api/escape-rooms"));
        var card = shelf.Single(r => r.Id == copy);
        Assert.True(card.Mine);
        Assert.False(card.Generated); // a copy, not written by AI
        Assert.False(shelf.Single(r => r.Id == "the-workshop").Mine);

        // Nobody else can see it, change it or copy it.
        var other = await HostAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/escape-rooms/{copy}/document")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(other, copy, doc)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/escape-rooms/{copy}/duplicate", null)).StatusCode);
        Assert.DoesNotContain(GameJson.Deserialize<List<EscapeRoomSummary>>(await other.GetStringAsync("/api/escape-rooms")), r => r.Id == copy);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync($"/api/escape-rooms/{copy}/document")).StatusCode);
    }

    [Fact]
    public async Task A_room_is_saved_only_when_it_can_still_be_escaped()
    {
        var host = await HostAsync();
        var copy = await CopyAsync(host, "the-workshop");
        var doc = await DocumentAsync(host, copy);

        // A puzzle that needs an item nobody hands out: the room can't be finished.
        var broken = doc.DeepClone().AsObject();
        broken["puzzles"]!.AsArray()[0]!["requires"] = new JsonArray("a-key-that-does-not-exist");
        var check = await Read<ValidationResult>(await host.PostAsJsonAsync("/api/escape-rooms/validate", new RoomDocumentRequest(JsonSerializer.SerializeToElement(broken)), GameJson.Options));
        Assert.False(check.Valid);
        var res = await SaveAsync(host, copy, broken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.NotEmpty(problem["errors"]!.AsArray());
        Assert.False(string.IsNullOrEmpty(problem["detail"]!.GetValue<string>())); // what the page shows

        // Missing a required part: a readable message, not a crash.
        var noIntro = doc.DeepClone().AsObject();
        noIntro.Remove("intro");
        var missing = await Read<ValidationResult>(await host.PostAsJsonAsync("/api/escape-rooms/validate", new RoomDocumentRequest(JsonSerializer.SerializeToElement(noIntro)), GameJson.Options));
        Assert.Contains(missing.Errors, e => e.Contains("intro"));

        // The id is fixed.
        var renamed = doc.DeepClone().AsObject();
        renamed["id"] = "someone-elses-room";
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(host, copy, renamed)).StatusCode);

        // And the room as it was is fine.
        Assert.True((await Read<ValidationResult>(await host.PostAsJsonAsync("/api/escape-rooms/validate", new RoomDocumentRequest(JsonSerializer.SerializeToElement(doc)), GameJson.Options))).Valid);
        Assert.True((await Read<SavedRoom>(await SaveAsync(host, copy, doc))).Valid);
    }

    [Fact]
    public async Task Rewording_the_story_keeps_the_leaderboard_but_changing_a_puzzle_starts_a_new_edition()
    {
        var host = await HostAsync();
        var copy = await CopyAsync(host, "the-workshop");
        var doc = await DocumentAsync(host, copy);

        doc["title"] = "Grandpa's Workshop";
        doc["intro"] = "Welcome to Grandpa's workshop, kids.";
        doc["stages"]!.AsArray()[0]!["description"] = "Grandpa's chains, and his old drain.";
        var story = await Read<SavedRoom>(await SaveAsync(host, copy, doc));
        Assert.Equal(1, story.Edition);
        Assert.False(story.NewEdition);
        Assert.Equal("Grandpa's Workshop", (await DocumentAsync(host, copy))["title"]!.GetValue<string>());

        var tape = doc["puzzles"]!.AsArray().First(p => p!["id"]!.GetValue<string>() == "tape")!;
        tape["answers"]!.AsArray().Add("grandpa's clock");
        var puzzle = await Read<SavedRoom>(await SaveAsync(host, copy, doc));
        Assert.Equal(2, puzzle.Edition);
        Assert.True(puzzle.NewEdition);
        // The server decides the edition: a document claiming another one is ignored.
        doc["edition"] = 99;
        Assert.Equal(2, (await Read<SavedRoom>(await SaveAsync(host, copy, doc))).Edition);
    }

    [Fact]
    public async Task Changing_a_stage_s_words_repaints_only_that_stage()
    {
        var host = await HostAsync();
        var copy = await CopyAsync(host, "the-workshop");
        var doc = await DocumentAsync(host, copy);
        var stages = doc["stages"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToList();
        var jobId = EscapeMedia.JobId(copy);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var key in new[] { EscapeArt.Cover, EscapeArt.Stage(stages[0]), EscapeArt.Stage(stages[1]) })
                db.ScenarioMedia.Add(new ScenarioMediaEntity { ScenarioId = jobId, Key = key, AssetId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        doc["stages"]!.AsArray()[0]!["description"] = "A brand new description, so a brand new picture.";
        Assert.True((await Read<SavedRoom>(await SaveAsync(host, copy, doc))).Valid);

        using var check = app.Services.CreateScope();
        var keys = await check.ServiceProvider.GetRequiredService<AppDbContext>().ScenarioMedia.Where(m => m.ScenarioId == jobId).Select(m => m.Key).ToListAsync();
        Assert.Equal([EscapeArt.Cover, EscapeArt.Stage(stages[1])], keys.Order());
    }

    [Fact]
    public async Task A_room_in_play_waits_and_the_next_game_plays_the_edit_on_every_server()
    {
        var host = await HostAsync();
        var copy = await CopyAsync(host, "the-workshop");
        var doc = await DocumentAsync(host, copy);
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(copy, PartyMode.SharedScreen, UseAi: false), GameJson.Options));

        doc["title"] = "Edited while in use";
        var busy = await SaveAsync(host, copy, doc);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains(party.Code, await busy.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/parties/{party.Code}")).StatusCode);
        Assert.True((await Read<SavedRoom>(await SaveAsync(host, copy, doc))).Valid);
        var next = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(copy, PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        Assert.Equal("Edited while in use", next.Title);

        // Another server's edit: the row changes without this server's cache being told. The saved time gives it away.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.EscapeRooms.SingleAsync(r => r.Id == copy);
            var node = JsonNode.Parse(row.Document)!;
            node["title"] = "Edited on another server";
            row.Document = node.ToJsonString();
            row.UpdatedAt = row.UpdatedAt.AddSeconds(1);
            await db.SaveChangesAsync();
        }
        Assert.Equal("Edited on another server", (await Read<PartyInfo>(await host.GetAsync($"/api/parties/{next.Code}"))).Title);
    }
}
