using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>The escape room's recap (#111): private until shared, a random link, and a preview card for chat apps.</summary>
public class EscapeRecapTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private EscapeState StateOf(string code)
    {
        using var scope = app.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == code);
        return GameJson.Deserialize<EscapeState>(row.State);
    }

    /// <summary>A workshop party with three guests, the clock started.</summary>
    private async Task<(HttpClient Host, PartyInfo Party, HubConnection Tv, HubConnection Phone, Guid Seat)> StartedAsync(params string[] names)
    {
        var (host, cookie) = await app.RegisterHostAsync($"recap{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        var seats = new List<SeatResponse>();
        foreach (var name in names.Length > 0 ? names : ["Ada", "Ben", "Cy"])
            seats.Add(await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name))));
        var tv = await app.ConnectAsync(cookie: cookie);
        var phone = await app.ConnectAsync(seats[0].Token);
        await tv.InvokeAsync("EscapeStart", party.Code);
        return (host, party, tv, phone, seats[0].SeatId);
    }

    private async Task EscapeAsync(PartyInfo party, HubConnection phone, Guid seat) =>
        await EscapeHubBot.PlayToEndAsync([phone], [seat], app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!, () => StateOf(party.Code));

    [Fact]
    public async Task The_host_previews_shares_and_unshares_an_escape_recap()
    {
        var (host, party, tv, phone, seat) = await StartedAsync();
        await using var _ = tv;
        await using var __ = phone;

        // Mid-game: no recap, so nothing can be spoiled.
        Assert.Equal(HttpStatusCode.Conflict, (await host.GetAsync($"/api/parties/{party.Code}/escape-recap")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync($"/api/parties/{party.Code}/recap/share", null)).StatusCode);

        await EscapeAsync(party, phone, seat);

        // Private until the host shares it.
        var preview = await Read<EscapeRecapSharing>(await host.GetAsync($"/api/parties/{party.Code}/escape-recap"));
        Assert.False(preview.Shared);
        Assert.Null(preview.Url);
        Assert.True(preview.Page.Recap.Escaped);
        Assert.Equal(["Ada", "Ben", "Cy"], preview.Page.Recap.Team.Select(p => p.Name));
        Assert.Equal(preview.Page.Recap.SolvedCount, preview.Page.Recap.Team.Sum(p => p.Solved));
        Assert.Equal("The Host", preview.Page.HostName);
        Assert.Equal(1, preview.Page.Rank); // the first escape on a fresh board

        var (other, _) = await app.RegisterHostAsync($"o{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/parties/{party.Code}/escape-recap")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/parties/{party.Code}/recap/share", null)).StatusCode);
        // The mystery's recap isn't this party's.
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/parties/{party.Code}/recap")).StatusCode);

        // Shared: anyone with the link can read it, without signing in.
        var url = (await Read<Dictionary<string, string>>(await host.PostAsync($"/api/parties/{party.Code}/recap/share", null)))["url"];
        Assert.Matches("^/escape/recap/[A-Za-z0-9_-]{22}$", url);
        var slug = url["/escape/recap/".Length..];
        Assert.Equal(url, (await Read<EscapeRecapSharing>(await host.GetAsync($"/api/parties/{party.Code}/escape-recap"))).Url);
        var anonymous = await app.CreateClient().GetAsync($"/api/escape-recap/{slug}");
        var page = await Read<EscapeRecapPage>(anonymous);
        Assert.Equal("The Workshop", page.Recap.RoomTitle);
        Assert.Equal("noindex", anonymous.Headers.GetValues("X-Robots-Tag").Single());
        // Each game's page reads only its own kind of party.
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/api/recap/{slug}")).StatusCode);

        // Stopping kills the link.
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/parties/{party.Code}/recap/share")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/api/escape-recap/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/api/escape-recap/not-a-real-link")).StatusCode);
    }

    [Fact]
    public async Task A_trapped_group_has_a_recap_but_no_rank()
    {
        var (host, party, tv, phone, _) = await StartedAsync();
        await using var __ = tv;
        await using var ___ = phone;
        using (var scope = app.Services.CreateScope())
        {
            var partyId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.SingleAsync(p => p.Code == party.Code)).Id;
            // The ticker's command, an hour late: the clock has run out.
            await scope.ServiceProvider.GetRequiredService<ButlerDidIt.Api.Parties.PartyRuntime>().ExecuteAsync(partyId, (s, now) => s.Tick(now.AddHours(1)));
        }

        var recap = (await Read<EscapeRecapSharing>(await host.GetAsync($"/api/parties/{party.Code}/escape-recap"))).Page;
        Assert.False(recap.Recap.Escaped);
        Assert.Null(recap.Rank);
        Assert.Equal(0, recap.Recap.SecondsLeft);
        Assert.Single(recap.Recap.Stages); // only the stage they were in
    }

    [Fact]
    public async Task A_shared_link_shows_a_preview_card_in_chat_apps()
    {
        // The room's cover, painted before the party loads it.
        var asset = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ScenarioMedia.Add(new ScenarioMediaEntity { ScenarioId = EscapeMedia.JobId("the-workshop"), Key = EscapeArt.Cover, AssetId = asset });
            await db.SaveChangesAsync();
        }
        app.Services.GetRequiredService<EscapeCatalog>().ForgetArt("the-workshop"); // as the media worker does once it has painted
        // Guests type their own names, so the page must encode them.
        var (host, party, tv, phone, seat) = await StartedAsync("Ada", "<b>Ben\"</b>");
        await using var _ = tv;
        await using var __ = phone;
        await EscapeAsync(party, phone, seat);
        var url = (await Read<Dictionary<string, string>>(await host.PostAsync($"/api/parties/{party.Code}/recap/share", null)))["url"];

        var res = await app.CreateClient().GetAsync(url);
        var html = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType!.MediaType);
        Assert.Matches("<meta property=\"og:title\" content=\"Escaped The Workshop in \\d+:\\d\\d\" />", html);
        Assert.Contains($"<meta property=\"og:image\" content=\"http://localhost/media/assets/{asset}\" />", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"http://localhost{url}\" />", html);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\" />", html);
        Assert.Contains("Ada, &lt;b&gt;Ben&quot;&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Ben", html);
        Assert.Matches("<title>Escaped The Workshop in \\d+:\\d\\d · The Butler Did It</title>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<title>"));

        // An unknown or unshared link still gets the app (which says the recap isn't available), with no card.
        var unknown = await (await app.CreateClient().GetAsync("/escape/recap/not-a-real-link")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("og:title", unknown);
        Assert.Contains("<div id=\"root\"></div>", unknown);
    }
}
