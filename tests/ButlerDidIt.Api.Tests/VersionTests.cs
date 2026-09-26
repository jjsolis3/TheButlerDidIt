using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>Versions of a story: same place and cast, a different killer each time.</summary>
public class VersionTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private const string Blackwood = "death-at-blackwood-manor";

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient host, string? version, string scenario = Blackwood) =>
        host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(scenario, PartyMode.SharedScreen, ContentRating.Mature, null, UseAi: false, Version: version), GameJson.Options);

    /// <summary>Marks a party as played (started), which is what "played by me" counts.</summary>
    private async Task StartAsync(string code)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.Where(p => p.Code == code)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PartyStatus.Finished));
    }

    [Fact]
    public void Surprise_prefers_versions_the_host_has_not_played_then_the_least_played()
    {
        string[] versions = ["a", "b", "c"];
        for (var seed = 0; seed < 20; seed++)
        {
            Assert.Equal("c", VersionPicker.Pick(versions, new Dictionary<string, int> { ["a"] = 2, ["b"] = 1 }, new Random(seed)));
            Assert.Contains(VersionPicker.Pick(versions, new Dictionary<string, int> { ["a"] = 1, ["b"] = 1, ["c"] = 1 }, new Random(seed)), versions);
            Assert.NotEqual("a", VersionPicker.Pick(versions, new Dictionary<string, int> { ["a"] = 3, ["b"] = 1, ["c"] = 1 }, new Random(seed)));
        }
    }

    [Fact]
    public async Task Stories_are_listed_once_with_their_versions()
    {
        var (host, _) = await app.RegisterHostAsync($"v{Guid.NewGuid():N}@example.com");
        var themes = await Read<List<ThemeCard>>(await host.GetAsync("/api/themes"));
        var all = themes.SelectMany(t => t.Scenarios).ToList();

        Assert.DoesNotContain(all, s => s.Id.Contains("--")); // versions aren't separate mysteries
        var blackwood = all.Single(s => s.Id == Blackwood);
        Assert.Equal(["Version A", "Version B", "Version C"], blackwood.Versions.Select(v => v.Label));
        Assert.Equal(Blackwood, blackwood.Versions[0].Id);
        Assert.All(blackwood.Versions, v => Assert.False(v.PlayedByMe));
    }

    [Fact]
    public async Task Surprise_me_deals_a_different_version_each_time_until_all_are_played()
    {
        var (host, cookie) = await app.RegisterHostAsync($"s{Guid.NewGuid():N}@example.com");
        var dealt = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var party = await Read<PartyInfo>(await CreateAsync(host, VersionPicker.Surprise));
            dealt.Add(party.ScenarioId); // the host is told the real version
            await StartAsync(party.Code);
        }
        Assert.Equal(3, dealt.Distinct().Count());
        Assert.All(dealt, id => Assert.StartsWith(Blackwood, id));

        var themes = await Read<List<ThemeCard>>(await host.GetAsync("/api/themes"));
        Assert.All(themes.SelectMany(t => t.Scenarios).Single(s => s.Id == Blackwood).Versions, v => Assert.True(v.PlayedByMe));

        // Guests and the big screen only ever see the shared story, never which version it is.
        var party2 = await Read<PartyInfo>(await CreateAsync(host, ScenarioVariants.VariantId(Blackwood, "C")));
        Assert.Equal(ScenarioVariants.VariantId(Blackwood, "C"), party2.ScenarioId);
        var asGuest = await Read<PartyInfo>(await app.CreateClient().GetAsync($"/api/parties/{party2.Code}"));
        Assert.Equal(Blackwood, asGuest.ScenarioId);
        await using var stage = await app.ConnectAsync(cookie: cookie);
        Assert.Equal(Blackwood, (await stage.InvokeAsync<StageView>("WatchParty", party2.Code)).Scenario.Id);
    }

    [Fact]
    public async Task A_specific_version_can_be_chosen_but_only_from_the_same_story()
    {
        var (host, _) = await app.RegisterHostAsync($"c{Guid.NewGuid():N}@example.com");
        var chosen = ScenarioVariants.VariantId(Blackwood, "B");
        Assert.Equal(chosen, (await Read<PartyInfo>(await CreateAsync(host, chosen))).ScenarioId);
        Assert.Equal(Blackwood, (await Read<PartyInfo>(await CreateAsync(host, version: null))).ScenarioId); // no choice: the original

        var wrong = await CreateAsync(host, ScenarioVariants.VariantId("death-among-the-vines", "B"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("doesn't belong", await wrong.Content.ReadAsStringAsync());
    }
}
