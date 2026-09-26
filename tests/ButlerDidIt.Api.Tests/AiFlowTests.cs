using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

public class AiFlowTests(FakeAiFactory app) : IClassFixture<FakeAiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> PartyAsync(string scenarioId = "death-at-blackwood-manor", HttpClient? host = null, string? cookie = null)
    {
        if (host is null) (host, cookie) = await app.RegisterHostAsync($"host{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(scenarioId, PartyMode.SharedScreen, null), GameJson.Options));
        return (host, cookie!, party);
    }

    private async Task<SeatResponse> JoinAsync(string code, string name) =>
        await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/join", new JoinRequest(name)));

    [Fact]
    public async Task Generated_mystery_is_saved_for_its_host_only_and_playable()
    {
        var (host, _) = await app.RegisterHostAsync($"gen{Guid.NewGuid():N}@example.com");
        var job = await Read<GenerationJobView>(await host.PostAsJsonAsync("/api/generation",
            new GenerateRequest("speakeasy", 4, ContentRating.Family, MysteryLength.Short, "a stolen trumpet"), GameJson.Options));
        Assert.Equal(GenerationStatus.Queued, job.Status);

        // The background worker picks the job up within a couple of seconds.
        var done = job;
        for (var i = 0; i < 100 && done.Status is not (GenerationStatus.Succeeded or GenerationStatus.Failed); i++)
        {
            await Task.Delay(200);
            done = await Read<GenerationJobView>(await host.GetAsync($"/api/generation/{job.Id}"));
        }
        Assert.Equal(GenerationStatus.Succeeded, done.Status);
        Assert.StartsWith("ai-speakeasy-", done.ScenarioId);

        // The host sees it under the Speakeasy theme; other hosts and guests don't.
        Assert.Contains(done.ScenarioId!, await host.GetStringAsync("/api/themes"));
        Assert.DoesNotContain(done.ScenarioId!, await app.CreateClient().GetStringAsync("/api/themes"));
        var (other, _) = await app.RegisterHostAsync($"other{Guid.NewGuid():N}@example.com");
        Assert.DoesNotContain(done.ScenarioId!, await other.GetStringAsync("/api/themes"));
        var stolen = await other.PostAsJsonAsync("/api/parties", new CreatePartyRequest(done.ScenarioId!, PartyMode.SharedScreen, null), GameJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);

        // …and the owner can start a Family party with it.
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(done.ScenarioId!, PartyMode.SharedScreen, null), GameJson.Options));
        Assert.Equal("The Fake Affair", party.Title);
    }

    [Fact]
    public async Task Guests_can_question_npcs_and_everyone_hears_the_answer()
    {
        var (_, cookie, party) = await PartyAsync();
        var alice = await JoinAsync(party.Code, "Alice");
        var bob = await JoinAsync(party.Code, "Bob");
        var cara = await JoinAsync(party.Code, "Cara");

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await using var aliceHub = await app.ConnectAsync(alice.Token);
        await using var bobHub = await app.ConnectAsync(bob.Token);
        await aliceHub.InvokeAsync("ChooseCharacter", "violet");
        await stage.InvokeAsync("StartGame", party.Code);
        foreach (var _ in Enumerable.Range(0, 3)) await stage.InvokeAsync("Advance", party.Code); // act 1 mingle

        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.True(view.Ai.NpcQuestions);
        var npc = view.Cast.First(c => c.IsNpc);

        await aliceHub.InvokeAsync("AskNpc", npc.CharacterId, "Where were you at 9:47?");

        var after = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        var entry = Assert.Single(after.Interrogations);
        Assert.Equal("Alice", entry.AskerName);
        Assert.Contains(npc.Name, entry.Answer);
        var bobView = await bobHub.InvokeAsync<PlayerView>("JoinSeat");
        Assert.Equal(entry.Answer, Assert.Single(bobView.Stage.Interrogations).Answer);

        // Characters played by guests can't be questioned by the AI.
        var aliceChar = after.Cast.First(c => c.PlayedBy == "Bob");
        var ex = await Assert.ThrowsAsync<HubException>(() => aliceHub.InvokeAsync("AskNpc", aliceChar.CharacterId, "Did you do it?"));
        Assert.Contains("narrator", ex.Message);

        // Usage was logged against the host.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AiUsage.AnyAsync(u => u.Purpose == "npc-answer" && u.Role == AiRole.Actor));
    }

    [Fact]
    public async Task Hints_are_private_and_verdicts_arrive_at_the_reveal()
    {
        var (_, cookie, party) = await PartyAsync();
        var alice = await JoinAsync(party.Code, "Alice");
        var bob = await JoinAsync(party.Code, "Bob");
        await JoinAsync(party.Code, "Cara");

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await using var aliceHub = await app.ConnectAsync(alice.Token);
        await using var bobHub = await app.ConnectAsync(bob.Token);
        await stage.InvokeAsync("StartGame", party.Code);
        foreach (var _ in Enumerable.Range(0, 3)) await stage.InvokeAsync("Advance", party.Code);

        await aliceHub.InvokeAsync("RequestHint");
        var aliceView = await aliceHub.InvokeAsync<PlayerView>("JoinSeat");
        var hint = Assert.Single(aliceView.MyHints);
        Assert.Contains("Inspector", hint.Text);
        Assert.Equal(0, aliceView.HintsLeft);
        Assert.Empty((await bobHub.InvokeAsync<PlayerView>("JoinSeat")).MyHints);
        Assert.DoesNotContain(hint.Text!, GameJson.Serialize(await stage.InvokeAsync<StageView>("WatchParty", party.Code)));

        // Skip to the reveal; verdicts are generated in the background.
        StageView view;
        do
        {
            await stage.InvokeAsync("Advance", party.Code);
            view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        } while (view.Phase != Phase.Reveal);
        await stage.InvokeAsync("Advance", party.Code); // unmask

        for (var i = 0; i < 50; i++)
        {
            view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
            if (view.Reveal!.Guesses.All(g => g.Verdict is not null)) break;
            await Task.Delay(100);
        }
        Assert.All(view.Reveal!.Guesses, g => Assert.StartsWith("Fake verdict", g.Verdict));
    }

    [Fact]
    public async Task Admin_settings_are_admin_only_and_never_return_keys()
    {
        // The first account registered in this test database is the admin; register a second one too.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (maybeAdmin, _) = await app.RegisterHostAsync($"a{Guid.NewGuid():N}@example.com");
        var adminEmail = (await db.Users.OrderBy(u => u.Id).Where(u => u.IsAdmin).Select(u => u.Email).FirstAsync());
        var (nonAdmin, _) = await app.RegisterHostAsync($"n{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await nonAdmin.GetAsync("/api/admin/ai/providers")).StatusCode);

        var admin = app.CreateClient(new() { HandleCookies = false });
        var login = await admin.PostAsJsonAsync("/api/auth/login", new LoginRequest(adminEmail!, "password123"));
        login.EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Add("Cookie", string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0])));

        var created = await admin.PostAsJsonAsync("/api/admin/ai/providers",
            new ProviderRequest($"OpenAI {Guid.NewGuid():N}", AiProviderKind.OpenAI, null, "sk-test-SUPERSECRET"), GameJson.Options);
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, body);
        Assert.DoesNotContain("SUPERSECRET", body);
        Assert.Contains("\"hasApiKey\":true", body);

        // Stored encrypted, not in plain text.
        Assert.DoesNotContain(await db.AiProviders.AsNoTracking().Select(p => p.EncryptedApiKey).ToListAsync(), k => k != null && k.Contains("SUPERSECRET"));
        Assert.DoesNotContain("SUPERSECRET", await admin.GetStringAsync("/api/admin/ai/providers"));

        var status = await admin.GetStringAsync("/api/ai/status");
        Assert.Contains("\"isAdmin\":true", status);
        _ = maybeAdmin;
    }
}

public class AiBudgetTests(TinyBudgetAiFactory app) : IClassFixture<TinyBudgetAiFactory>
{
    [Fact]
    public async Task Requests_stop_once_the_monthly_budget_is_spent()
    {
        // First user is the admin: give the fake model a price so calls cost money.
        var (admin, cookie) = await app.RegisterHostAsync("budget-admin@example.com");
        (await admin.PutAsJsonAsync("/api/admin/ai/prices", new PriceView("fake-model", 100m, 100m), GameJson.Options)).EnsureSuccessStatusCode();

        var party = GameJson.Deserialize<PartyInfo>(await (await admin.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null), GameJson.Options)).Content.ReadAsStringAsync());
        var seats = new List<SeatResponse>();
        foreach (var name in new[] { "Alice", "Bob", "Cara" })
        {
            seats.Add(GameJson.Deserialize<SeatResponse>(await (await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name))).Content.ReadAsStringAsync()));
        }

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await using var alice = await app.ConnectAsync(seats[0].Token);
        await stage.InvokeAsync("StartGame", party.Code);
        foreach (var _ in Enumerable.Range(0, 3)) await stage.InvokeAsync("Advance", party.Code);
        var npc = (await stage.InvokeAsync<StageView>("WatchParty", party.Code)).Cast.First(c => c.IsNpc).CharacterId;

        await alice.InvokeAsync("AskNpc", npc, "First question, please?"); // spends 0.12 of a 0.0001 budget
        var ex = await Assert.ThrowsAsync<HubException>(() => alice.InvokeAsync("AskNpc", npc, "Second question?"));
        Assert.Contains("budget", ex.Message);

        // The refused question didn't use up a slot.
        var view = await alice.InvokeAsync<PlayerView>("JoinSeat");
        Assert.Equal(2, view.QuestionsLeft);
    }
}
