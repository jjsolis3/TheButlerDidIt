using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>The escape room's AI game master against the Fake AI: lines on the TV, and hints written for the group.</summary>
public class EscapeGameMasterTests(FakeAiFactory app) : IClassFixture<FakeAiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(string Cookie, PartyInfo Party, SeatResponse Seat)> StartedPartyAsync(bool useAi)
    {
        var (host, cookie) = await app.RegisterHostAsync($"gm{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, UseAi: useAi), GameJson.Options));
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        return (cookie, party, seat);
    }

    private static async Task<EscapeStageView> WaitForAsync(HubConnection tv, string code, Func<EscapeStageView, bool> ready, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            var stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", code);
            if (ready(stage)) return stage;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}.");
    }

    [Fact]
    public async Task The_game_master_welcomes_the_group_out_loud_when_the_clock_starts()
    {
        var (cookie, party, _) = await StartedPartyAsync(useAi: true);
        await using var tv = await app.ConnectAsync(cookie: cookie);

        var lobby = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal("The Tinkerer", lobby.GameMaster?.Name);
        Assert.True(lobby.GameMaster is { Narrates: true, WritesHints: true, Voiced: true });

        await tv.InvokeAsync("EscapeStart", party.Code);
        var stage = await WaitForAsync(tv, party.Code, s => s.Narration.Any(n => n.AudioUrl is not null), "the welcome line and its recording");
        var line = Assert.Single(stage.Narration);
        Assert.Equal("Fake game master line for Start: tick tock, my little guests.", line.Text);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AiUsage.AnyAsync(u => u.Purpose == "escape-narration"));
    }

    [Fact]
    public async Task A_hint_is_written_by_the_game_master_and_still_costs_time()
    {
        var (cookie, party, seat) = await StartedPartyAsync(useAi: true);
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await using var phone = await app.ConnectAsync(seat.Token);
        await tv.InvokeAsync("EscapeStart", party.Code);
        var before = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        var puzzle = before.Puzzles.First(p => p.Needs.Count == 0);

        await phone.InvokeAsync("EscapeHint", puzzle.Id);

        var after = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        var shown = after.Puzzles.Single(p => p.Id == puzzle.Id);
        Assert.False(shown.HintPending);
        Assert.Equal(["The game master whispers: look again at what the phones in your hands are telling you."], shown.Hints);
        Assert.Equal(before.Deadline!.Value.AddSeconds(-before.HintPenaltySeconds), after.Deadline);
        Assert.Equal(1, after.HintsUsed);
    }

    [Fact]
    public async Task Without_the_ai_the_room_plays_exactly_as_written()
    {
        var (cookie, party, seat) = await StartedPartyAsync(useAi: false);
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await using var phone = await app.ConnectAsync(seat.Token);
        await tv.InvokeAsync("EscapeStart", party.Code);
        var stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        var puzzle = stage.Puzzles.First(p => p.Needs.Count == 0);

        await phone.InvokeAsync("EscapeHint", puzzle.Id);

        var after = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Null(after.GameMaster);
        Assert.Empty(after.Narration);
        var room = EscapeEngine.RoomFor(StateOf(party.Code), app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!);
        Assert.Equal([room.FindPuzzle(puzzle.Id)!.Hints[0]], after.Puzzles.Single(p => p.Id == puzzle.Id).Hints);
        Assert.Empty(StateOf(party.Code).Cues);
    }

    private EscapeState StateOf(string code)
    {
        using var scope = app.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == code);
        return GameJson.Deserialize<EscapeState>(row.State);
    }

    [Fact]
    public async Task The_room_is_painted_once_and_the_tv_shows_each_stage_picture()
    {
        var (cookie, party, _) = await StartedPartyAsync(useAi: true);
        await using var tv = await app.ConnectAsync(cookie: cookie);

        // The cover shows in the lobby once the (fake) Illustrator has painted it.
        var lobby = await WaitForAsync(tv, party.Code, s => s.ArtUrl is not null, "the room's cover");
        Assert.StartsWith("/media/assets/", lobby.ArtUrl);
        Assert.Equal(Soundscape.Workshop, lobby.Soundscape);

        await tv.InvokeAsync("EscapeStart", party.Code);
        var playing = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.NotEqual(lobby.ArtUrl, playing.ArtUrl); // the first stage's own picture

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = scope.ServiceProvider.GetRequiredService<EscapeCatalog>().Find("the-workshop")!;
        Assert.Equal(room.Stages.Count + 1, await db.ScenarioMedia.CountAsync(m => m.ScenarioId == EscapeMedia.JobId("the-workshop")));

        // A second party of the same room reuses the pictures: nothing new is painted.
        var images = await db.MediaAssets.CountAsync(a => a.Kind == MediaKind.Image);
        var (secondCookie, second, _) = await StartedPartyAsync(useAi: true); // another host
        await using var tv2 = await app.ConnectAsync(cookie: secondCookie);
        await WaitForAsync(tv2, second.Code, s => s.ArtUrl is not null, "the second party's cover");
        Assert.Equal(images, await db.MediaAssets.CountAsync(a => a.Kind == MediaKind.Image));
    }

    [Fact]
    public async Task Without_the_ai_nothing_is_painted()
    {
        var (host, _) = await app.RegisterHostAsync($"noart{Guid.NewGuid():N}@example.com");
        await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-funhouse", PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.MediaJobs.AnyAsync(j => j.ScenarioId == EscapeMedia.JobId("the-funhouse")));
    }
}
