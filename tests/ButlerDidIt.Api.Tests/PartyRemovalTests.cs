using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>The host's "Remove" button on their list of parties.</summary>
public class PartyRemovalTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, PartyInfo Party)> PartyAsync(string scenario = "death-at-blackwood-manor")
    {
        var (host, _) = await app.RegisterHostAsync($"rm{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(scenario, PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
        return (host, party);
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = app.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task An_unfinished_party_is_deleted_with_its_seats_and_its_code_stops_working()
    {
        var (host, party) = await PartyAsync();
        await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));

        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/parties/{party.Code}")).StatusCode);

        Assert.Empty(await Read<List<PartyInfo>>(await host.GetAsync("/api/parties")));
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/api/parties/{party.Code}")).StatusCode);
        Assert.False(await DbAsync(db => db.Parties.AnyAsync(p => p.Code == party.Code)));
    }

    [Fact]
    public async Task A_finished_party_is_only_hidden_so_its_recap_and_played_version_are_kept()
    {
        var (host, party) = await PartyAsync("the-captains-last-cocoa");
        await DbAsync(db => db.Parties.Where(p => p.Code == party.Code).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PartyStatus.Finished)));

        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/parties/{party.Code}")).StatusCode);

        Assert.Empty(await Read<List<PartyInfo>>(await host.GetAsync("/api/parties")));
        Assert.NotNull(await DbAsync(db => db.Parties.Where(p => p.Code == party.Code).Select(p => p.HiddenAt).SingleAsync()));
        // "Surprise me" still knows this version has been played.
        Assert.Contains("\"playedByMe\":true", await host.GetStringAsync("/api/themes"));
    }

    [Fact]
    public async Task Only_the_host_can_remove_their_party()
    {
        var (_, party) = await PartyAsync();
        var (other, _) = await app.RegisterHostAsync($"rmo{Guid.NewGuid():N}@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"/api/parties/{party.Code}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().DeleteAsync($"/api/parties/{party.Code}")).StatusCode);
        Assert.True(await DbAsync(db => db.Parties.AnyAsync(p => p.Code == party.Code)));
    }
}
