using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;

namespace ButlerDidIt.Api.Tests;

/// <summary>A host's usual party settings (#102): the host page starts from them.</summary>
public class HostPreferencesTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static readonly HostPreferences Mine = new()
    {
        Game = GameKind.EscapeRoom,
        Mystery = new() { Mode = PartyMode.PassAndPlay, Shelf = ContentRating.Family, Tone = Tone.Playful, UseAi = false, Tailor = false },
        Escape = new() { Mode = PartyMode.Remote, Shelf = ContentRating.Family, Minutes = 30, Difficulty = EscapeDifficulty.Hard, Puzzles = PuzzleChoice.Daily },
    };

    private static async Task<HostPreferences> Read(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<HostPreferences>(GameJson.Options))!;
    }

    [Fact]
    public async Task A_host_saves_their_usual_settings_and_gets_them_back_and_nobody_else_does()
    {
        var (host, _) = await app.RegisterHostAsync($"p{Guid.NewGuid():N}@example.com");
        // Nothing saved yet: the same defaults the host page has always had.
        Assert.Equal(new HostPreferences(), await Read(await host.GetAsync("/api/account/preferences")));

        Assert.Equal(Mine, await Read(await host.PutAsJsonAsync("/api/account/preferences", Mine, GameJson.Options)));
        Assert.Equal(Mine, await Read(await host.GetAsync("/api/account/preferences")));
        // Every endpoint acts on the signed-in host: another host has their own (here, the defaults).
        var (other, _) = await app.RegisterHostAsync($"o{Guid.NewGuid():N}@example.com");
        Assert.Equal(new HostPreferences(), await Read(await other.GetAsync("/api/account/preferences")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/account/preferences")).StatusCode);

        // "Download my data" includes them.
        var export = JsonNode.Parse(await host.GetStringAsync("/api/account/export"))!;
        Assert.Equal("passAndPlay", export["partySettings"]!["mystery"]!["mode"]!.GetValue<string>());
        Assert.Equal(30, export["partySettings"]!["escape"]!["minutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task Settings_the_host_page_could_never_offer_are_refused()
    {
        var (host, _) = await app.RegisterHostAsync($"v{Guid.NewGuid():N}@example.com");
        async Task Refused(HostPreferences p, string why)
        {
            var res = await host.PutAsJsonAsync("/api/account/preferences", p, GameJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains(why, await res.Content.ReadAsStringAsync());
        }
        await Refused(new() { Mystery = new() { Shelf = ContentRating.Mature, Tone = Tone.Playful } }, "Funny is a tone for Family");
        await Refused(new() { Mystery = new() { Shelf = ContentRating.Family, Tone = Tone.Clean } }, "Mixed company");
        await Refused(new() { Escape = new() { Minutes = 50 } }, "30, 45 or 60");
        await Refused(new() { Escape = new() { Puzzles = PuzzleChoice.Replay } }, "today's challenge");
        await Refused(new() { Escape = new() { Mode = PartyMode.PassAndPlay } }, "together or on a video call");
        // A made-up value never reaches the account.
        var junk = await host.PutAsync("/api/account/preferences",
            new StringContent("""{"game":"bingo"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
        Assert.Equal(new HostPreferences(), await Read(await host.GetAsync("/api/account/preferences")));
    }
}
