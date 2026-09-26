using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR.Client;

namespace ButlerDidIt.Api.Tests;

public class RecapTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    [Fact]
    public async Task Host_can_share_and_unshare_the_recap_of_a_finished_party()
    {
        var (host, cookie) = await app.RegisterHostAsync($"r{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
        foreach (var name in new[] { "Alice", "Bob", "Cara" })
            await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name));

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);

        // Mid-game: no recap, so nothing can be spoiled.
        Assert.Equal(HttpStatusCode.Conflict, (await host.GetAsync($"/api/parties/{party.Code}/recap")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync($"/api/parties/{party.Code}/recap/share", null)).StatusCode);

        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        for (var i = 0; i < 40 && view.Phase != Phase.Finished; i++)
        {
            await stage.InvokeAsync("Advance", party.Code);
            view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        }
        Assert.Equal(Phase.Finished, view.Phase);

        // Private until the host shares it.
        var preview = await Read<RecapSharing>(await host.GetAsync($"/api/parties/{party.Code}/recap"));
        Assert.False(preview.Shared);
        Assert.Equal("Dr. Cornelius Finch", preview.Page.Recap.Cast.Single(c => c.IsMurderer).Name);
        Assert.Equal("The Host", preview.Page.HostName);

        var (other, _) = await app.RegisterHostAsync($"o{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/parties/{party.Code}/recap")).StatusCode);

        // Shared: anyone with the link can read it, without signing in.
        var shared = await Read<Dictionary<string, string>>(await host.PostAsync($"/api/parties/{party.Code}/recap/share", null));
        var url = shared["url"];
        Assert.Matches("^/recap/[A-Za-z0-9_-]{22}$", url);
        var slug = url["/recap/".Length..];
        var anonymous = await app.CreateClient().GetAsync($"/api/recap/{slug}");
        var page = await Read<RecapPage>(anonymous);
        Assert.Contains(page.Recap.Cast, c => c.PlayedBy == "Alice");
        Assert.Equal("noindex", anonymous.Headers.GetValues("X-Robots-Tag").Single());

        // Sharing again keeps the same link; stopping kills it.
        Assert.Equal(url, (await Read<Dictionary<string, string>>(await host.PostAsync($"/api/parties/{party.Code}/recap/share", null)))["url"]);
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/parties/{party.Code}/recap/share")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/api/recap/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/api/recap/not-a-real-link")).StatusCode);
    }
}
