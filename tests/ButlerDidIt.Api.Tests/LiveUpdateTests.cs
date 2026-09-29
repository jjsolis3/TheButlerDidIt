using System.Net.Http.Json;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>Things the server pushes as they happen: NPC answers being typed, job progress, and verdicts after a restart.</summary>
public class LiveUpdateTests(FakeAiFactory app) : IClassFixture<FakeAiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(string Cookie, PartyInfo Party, List<SeatResponse> Seats)> PartyAsync()
    {
        var (host, cookie) = await app.RegisterHostAsync($"live{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null), GameJson.Options));
        var seats = new List<SeatResponse>();
        foreach (var name in new[] { "Alice", "Bob", "Cara" })
            seats.Add(await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name))));
        return (cookie, party, seats);
    }

    private static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    [Fact]
    public async Task Npc_answers_are_typed_live_on_the_stage_and_every_phone()
    {
        var (cookie, party, seats) = await PartyAsync();
        await using var stage = await app.ConnectAsync(cookie: cookie);
        await using var alice = await app.ConnectAsync(seats[0].Token);
        await using var bob = await app.ConnectAsync(seats[1].Token);

        var stageTyping = new List<NpcTypingEvent>();
        var bobTyping = new List<NpcTypingEvent>();
        stage.On<NpcTypingEvent>("npcTyping", e => { lock (stageTyping) stageTyping.Add(e); });
        bob.On<NpcTypingEvent>("npcTyping", e => { lock (bobTyping) bobTyping.Add(e); });

        await stage.InvokeAsync("StartGame", party.Code);
        foreach (var _ in Enumerable.Range(0, 3)) await stage.InvokeAsync("Advance", party.Code);
        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        await bob.InvokeAsync<PlayerView>("JoinSeat");
        var npc = view.Cast.First(c => c.IsNpc);

        await alice.InvokeAsync("AskNpc", npc.CharacterId, "Where were you at 9:47?");
        var answered = Assert.Single((await stage.InvokeAsync<StageView>("WatchParty", party.Code)).Interrogations);

        // At least the first words were pushed while the AI was still writing, to the stage and to other guests.
        await Eventually(() => Task.FromResult(stageTyping.Count > 0 && bobTyping.Count > 0), "typing events");
        foreach (var e in stageTyping.Concat(bobTyping))
        {
            Assert.Equal(answered.Id, e.InterrogationId);
            Assert.StartsWith(e.Text.Trim(), answered.Answer);
        }
    }

    [Fact]
    public async Task Verdicts_that_were_waiting_when_the_server_stopped_are_written_after_a_restart()
    {
        var (cookie, party, _) = await PartyAsync();
        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);
        StageView view;
        do
        {
            await stage.InvokeAsync("Advance", party.Code);
            view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        } while (view.Phase != Phase.Reveal);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        async Task<GameState> State() => GameJson.Deserialize<GameState>((await db.Parties.AsNoTracking().FirstAsync(p => p.Code == party.Code)).State);
        await Eventually(async () => (await State()).Verdicts.Count > 0, "the first verdicts");

        // Simulate a restart that lost the in-memory queue: the party is at the reveal with no verdicts.
        var row = await db.Parties.FirstAsync(p => p.Code == party.Code);
        var state = GameJson.Deserialize<GameState>(row.State);
        state.Verdicts.Clear();
        row.State = GameJson.Serialize(state);
        await db.SaveChangesAsync();

        var requeued = await scope.ServiceProvider.GetRequiredService<AiGameService>().RequeueWaitingVerdictsAsync(CancellationToken.None);
        Assert.True(requeued >= 1);
        await Eventually(async () => (await State()).Verdicts.Count > 0, "verdicts after the restart");
    }

    [Fact]
    public async Task A_host_hears_about_job_progress_without_polling()
    {
        var (host, cookie) = await app.RegisterHostAsync($"jobs{Guid.NewGuid():N}@example.com");
        await using var hub = await app.ConnectAsync(cookie: cookie);
        var signals = 0;
        hub.On("jobs", () => Interlocked.Increment(ref signals));
        await hub.InvokeAsync("WatchMyJobs");

        // Another host's jobs are none of this host's business.
        var (other, otherCookie) = await app.RegisterHostAsync($"jobs-other{Guid.NewGuid():N}@example.com");
        await using var otherHub = await app.ConnectAsync(cookie: otherCookie);
        var otherSignals = 0;
        otherHub.On("jobs", () => Interlocked.Increment(ref otherSignals));
        await otherHub.InvokeAsync("WatchMyJobs");

        var job = await Read<GenerationJobView>(await host.PostAsJsonAsync("/api/generation",
            new GenerateRequest("speakeasy", 4, ContentRating.Family, MysteryLength.Short, null), GameJson.Options));
        await Eventually(async () =>
            (await Read<GenerationJobView>(await host.GetAsync($"/api/generation/{job.Id}"))).Status is GenerationStatus.Succeeded or GenerationStatus.Failed,
            "the job to finish");

        await Eventually(() => Task.FromResult(Volatile.Read(ref signals) >= 2), "job signals"); // started, …, finished
        Assert.Equal(0, Volatile.Read(ref otherSignals));
        _ = other;
    }

    [Fact]
    public async Task Guests_cannot_listen_for_job_progress()
    {
        var (_, _, seats) = await PartyAsync();
        await using var guest = await app.ConnectAsync(seats[0].Token);
        var ex = await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("WatchMyJobs"));
        Assert.Contains("Sign in", ex.Message);
    }

    [Fact]
    public void Throttle_lets_one_call_through_per_interval()
    {
        var clock = new ManualClock();
        var throttle = new Throttle(TimeSpan.FromMilliseconds(150), clock);
        Assert.True(throttle.Ready());
        Assert.False(throttle.Ready());
        clock.Ticks += TimeSpan.FromMilliseconds(100).Ticks;
        Assert.False(throttle.Ready());
        clock.Ticks += TimeSpan.FromMilliseconds(60).Ticks;
        Assert.True(throttle.Ready());
    }

    /// <summary>A clock that only moves when the test says so.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
