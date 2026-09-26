using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// The AI remix: when no version's killer is one of tonight's guests, the Storyteller writes
/// one where a guest is (the fake AI stands in for it here). The background worker runs it.
/// </summary>
public class TailoringTests(FakeAiFactory app) : IClassFixture<FakeAiFactory>
{
    private const string Pirates = "the-captains-last-cocoa";

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> SurprisePartyAsync(bool tailor = true)
    {
        var (host, cookie) = await app.RegisterHostAsync($"t{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(Pirates, PartyMode.SharedScreen, null, UseAi: false, Version: VersionPicker.Surprise, TailorWithAi: tailor), GameJson.Options));
        return (host, cookie, party);
    }

    private async Task<List<HubConnection>> SeatAsync(string code, params string[] characters)
    {
        var hubs = new List<HubConnection>();
        foreach (var (character, i) in characters.Select((c, i) => (c, i)))
        {
            var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/join", new JoinRequest($"Pirate {i}")));
            var hub = await app.ConnectAsync(seat.Token);
            await hub.InvokeAsync<PlayerView>("JoinSeat");
            await hub.InvokeAsync("ChooseCharacter", character);
            hubs.Add(hub);
        }
        return hubs;
    }

    private async Task<Party> PartyAsync(string code)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().SingleAsync(p => p.Code == code);
    }

    private async Task<Party> WaitUntilStartedAsync(string code)
    {
        for (var i = 0; i < 150; i++)
        {
            var party = await PartyAsync(code);
            if (party.Status != PartyStatus.Lobby) return party;
            await Task.Delay(200);
        }
        throw new TimeoutException("The party never started.");
    }

    [Fact]
    public async Task When_no_version_fits_the_ai_writes_one_where_a_guest_is_the_killer()
    {
        var (host, cookie, party) = await SurprisePartyAsync();
        // Flint, Grace and Cookie (the three killers) are left to the narrator; Pip may never be the killer.
        var hubs = await SeatAsync(party.Code, "pip", "nell", "beak");
        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);

        var started = await WaitUntilStartedAsync(party.Code);
        Assert.StartsWith($"{Pirates}--ai", started.ScenarioId);

        var views = new List<PlayerView>();
        foreach (var hub in hubs) views.Add(await hub.InvokeAsync<PlayerView>("JoinSeat"));
        var murderer = Assert.Single(views, v => v.Dossier!.IsMurderer);
        Assert.NotEqual("pip", murderer.Dossier!.Character.CharacterId);
        Assert.False(views.Single(v => v.Dossier!.IsMurderer).Stage.Tailoring);

        // The new version joins this host's list for replays, and only theirs.
        var mine = await Read<List<ThemeCard>>(await host.GetAsync("/api/themes"));
        var versions = mine.SelectMany(t => t.Scenarios).Single(s => s.Id == Pirates).Versions;
        Assert.Contains(versions, v => v.Id == started.ScenarioId && v.Label == "✨ AI version 1" && v.PlayedByMe);
        var (other, _) = await app.RegisterHostAsync($"o{Guid.NewGuid():N}@example.com");
        Assert.DoesNotContain(started.ScenarioId, await other.GetStringAsync("/api/themes"));

        foreach (var hub in hubs) await hub.DisposeAsync();
    }

    [Fact]
    public async Task Without_the_hosts_permission_the_narrator_plays_the_killer()
    {
        var (_, cookie, party) = await SurprisePartyAsync(tailor: false);
        var hubs = await SeatAsync(party.Code, "pip", "nell", "beak");
        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);

        var started = await PartyAsync(party.Code);
        Assert.Equal(PartyStatus.InProgress, started.Status); // started at once, no waiting
        Assert.DoesNotContain("--ai", started.ScenarioId);
        foreach (var hub in hubs) await hub.DisposeAsync();
    }

    [Fact]
    public async Task If_the_remix_fails_the_evening_starts_with_a_hand_written_version()
    {
        var (_, cookie, party) = await SurprisePartyAsync();
        var hubs = await SeatAsync(party.Code, "pip", "nell", "beak");

        var partyId = (await PartyAsync(party.Code)).Id;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parties = scope.ServiceProvider.GetRequiredService<PartyService>();

        // Freeze the lobby, as the dealer does: the cast can't change while the AI writes.
        await parties.ExecuteAsync(partyId, (_, now) => new BeginTailoring(now));
        var ex = await Assert.ThrowsAsync<HubException>(() => hubs[0].InvokeAsync("ChooseCharacter", "flint"));
        Assert.Contains("getting the evening ready", ex.Message);

        // Then queue a remix that can't succeed (its target doesn't exist).
        db.GenerationJobs.Add(new GenerationJobEntity
        {
            Id = Guid.NewGuid(), Kind = GenerationKind.Remix, HostUserId = (await PartyAsync(party.Code)).HostUserId, ThemeSlug = "pirate-cove",
            PartyId = partyId, SourceScenarioId = Pirates, TargetCharacterId = "nobody", Request = "{}",
            Status = GenerationStatus.Queued, CreatedAt = parties.Now, UpdatedAt = parties.Now,
        });
        await db.SaveChangesAsync();

        var started = await WaitUntilStartedAsync(party.Code);
        Assert.DoesNotContain("--ai", started.ScenarioId);
        foreach (var hub in hubs) await hub.DisposeAsync();
    }

    [Fact]
    public async Task The_host_can_skip_the_wait()
    {
        var (_, cookie, party) = await SurprisePartyAsync();
        var hubs = await SeatAsync(party.Code, "pip", "nell", "beak");
        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);
        // The worker may already have finished; either way the party must be running afterwards.
        await stage.InvokeAsync("SkipTailoring", party.Code);

        var started = await WaitUntilStartedAsync(party.Code);
        Assert.Equal(PartyStatus.InProgress, started.Status);
        foreach (var hub in hubs) await hub.DisposeAsync();
    }
}
