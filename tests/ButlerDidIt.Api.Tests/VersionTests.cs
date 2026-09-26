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
            new CreatePartyRequest(scenario, PartyMode.SharedScreen, null, UseAi: false, Version: version), GameJson.Options);

    /// <summary>Marks a party as played, which is what "played by me" counts.</summary>
    private async Task MarkPlayedAsync(string code)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.Where(p => p.Code == code)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PartyStatus.Finished));
    }

    /// <summary>Guests join and take these characters, then the host begins the evening.</summary>
    private async Task<(string ScenarioId, List<PlayerView> Guests)> PlayAsync(string cookie, string code, params string[] characters)
    {
        var hubs = new List<HubConnection>();
        try
        {
            foreach (var (character, i) in characters.Select((c, i) => (c, i)))
            {
                var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/join", new JoinRequest($"Guest {i}")));
                var hub = await app.ConnectAsync(seat.Token);
                hubs.Add(hub);
                await hub.InvokeAsync<PlayerView>("JoinSeat");
                await hub.InvokeAsync("ChooseCharacter", character);
            }
            await using var stage = await app.ConnectAsync(cookie: cookie);
            await stage.InvokeAsync("StartGame", code);

            var views = new List<PlayerView>();
            foreach (var hub in hubs) views.Add(await hub.InvokeAsync<PlayerView>("JoinSeat"));
            using var scope = app.Services.CreateScope();
            var scenarioId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties
                .Where(p => p.Code == code).Select(p => p.ScenarioId).SingleAsync();
            return (scenarioId, views);
        }
        finally
        {
            foreach (var hub in hubs) await hub.DisposeAsync();
        }
    }

    [Fact]
    public void The_picker_puts_unplayed_versions_first_then_a_guest_killer_then_chance()
    {
        VersionPicker.Candidate[] versions = [new("a", "finch"), new("b", "hargrove"), new("c", "evelyn")];
        HashSet<string> cast = ["hargrove", "violet", "zelda"];
        for (var seed = 0; seed < 20; seed++)
        {
            // All unplayed: the one whose killer is a guest.
            Assert.Equal(new VersionPicker.Deal("b", true), VersionPicker.Pick(versions, new Dictionary<string, int>(), cast, new Random(seed)));
            // Unplayed beats a guest killer: B was played, so A (the narrator plays Finch) it is... or C.
            var deal = VersionPicker.Pick(versions, new Dictionary<string, int> { ["b"] = 1 }, cast, new Random(seed));
            Assert.Contains(deal.Id, new[] { "a", "c" });
            Assert.False(deal.Fits);
            // Everything played equally often: the guest killer wins again.
            Assert.Equal("b", VersionPicker.Pick(versions, new Dictionary<string, int> { ["a"] = 1, ["b"] = 1, ["c"] = 1 }, cast, new Random(seed)).Id);
        }
        // Ties are broken at random.
        var picks = Enumerable.Range(0, 40).Select(seed => VersionPicker.Pick(versions, new Dictionary<string, int>(), new HashSet<string>(), new Random(seed)).Id).ToHashSet();
        Assert.Equal(3, picks.Count);
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
    public async Task Surprise_me_deals_the_version_at_the_start_so_a_guest_is_the_killer()
    {
        var (host, cookie) = await app.RegisterHostAsync($"s{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await CreateAsync(host, VersionPicker.Surprise));
        Assert.Equal(Blackwood, party.ScenarioId); // nothing is dealt yet

        // Finch (version A's killer) is left to the narrator; Hargrove (B) and Evelyn (C) are guests.
        var (dealt, guests) = await PlayAsync(cookie, party.Code, "hargrove", "evelyn", "violet");
        Assert.Contains(dealt, new[] { ScenarioVariants.VariantId(Blackwood, "B"), ScenarioVariants.VariantId(Blackwood, "C") });
        Assert.Single(guests, g => g.Dossier!.IsMurderer);
    }

    [Fact]
    public async Task Unplayed_versions_come_before_a_guest_killer()
    {
        var (host, cookie) = await app.RegisterHostAsync($"u{Guid.NewGuid():N}@example.com");
        foreach (var v in new[] { "B", "C" })
            await MarkPlayedAsync((await Read<PartyInfo>(await CreateAsync(host, ScenarioVariants.VariantId(Blackwood, v)))).Code);

        // Only version A is unplayed. Its killer, Finch, isn't a guest, and the AI may not help
        // (not allowed and not set up here), so the narrator plays Finch rather than replaying B or C.
        var party = await Read<PartyInfo>(await CreateAsync(host, VersionPicker.Surprise));
        var (dealt, guests) = await PlayAsync(cookie, party.Code, "hargrove", "evelyn", "violet");
        Assert.Equal(Blackwood, dealt);
        Assert.DoesNotContain(guests, g => g.Dossier!.IsMurderer);
    }

    [Fact]
    public async Task Guests_and_the_big_screen_never_see_which_version_it_is()
    {
        var (host, cookie) = await app.RegisterHostAsync($"m{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await CreateAsync(host, ScenarioVariants.VariantId(Blackwood, "C")));
        Assert.Equal(ScenarioVariants.VariantId(Blackwood, "C"), party.ScenarioId); // the host is told
        var asGuest = await Read<PartyInfo>(await app.CreateClient().GetAsync($"/api/parties/{party.Code}"));
        Assert.Equal(Blackwood, asGuest.ScenarioId);
        await using var stage = await app.ConnectAsync(cookie: cookie);
        Assert.Equal(Blackwood, (await stage.InvokeAsync<StageView>("WatchParty", party.Code)).Scenario.Id);
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

    [Fact]
    public async Task Spoiler_booklets_wait_until_a_surprise_version_is_dealt()
    {
        var (host, cookie) = await app.RegisterHostAsync($"k{Guid.NewGuid():N}@example.com");
        var surprise = await Read<PartyInfo>(await CreateAsync(host, VersionPicker.Surprise));
        var early = await host.GetAsync($"/api/parties/{surprise.Code}/kit/booklets.pdf");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("specific version", await early.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/parties/{surprise.Code}/kit/invitations.pdf")).StatusCode);

        await PlayAsync(cookie, surprise.Code, "hargrove", "evelyn", "violet");
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/parties/{surprise.Code}/kit/booklets.pdf")).StatusCode);

        // A version chosen up front can be printed before the party.
        var chosen = await Read<PartyInfo>(await CreateAsync(host, ScenarioVariants.VariantId(Blackwood, "B")));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/parties/{chosen.Code}/kit/booklets.pdf")).StatusCode);
    }
}
