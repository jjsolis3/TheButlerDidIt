using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>Escape rooms written by AI from a theme, against the Fake AI: generated, kept to their host, played, deleted.</summary>
public class EscapeGenerationTests(FakeAiFactory app) : IClassFixture<FakeAiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static Task<HttpResponseMessage> Generate(HttpClient host, string theme = "a haunted lighthouse") =>
        host.PostAsJsonAsync("/api/escape-rooms/generate", new EscapeRoomGenerateRequest(theme, ContentRating.Family, 30), GameJson.Options);

    private static async Task<GenerationJobView> WrittenAsync(HttpClient host)
    {
        var job = await Read<GenerationJobView>(await Generate(host));
        // The background worker picks the job up within a couple of seconds.
        for (var i = 0; i < 100 && job.Status is not (GenerationStatus.Succeeded or GenerationStatus.Failed); i++)
        {
            await Task.Delay(200);
            job = await Read<GenerationJobView>(await host.GetAsync($"/api/generation/{job.Id}"));
        }
        Assert.Equal(GenerationStatus.Succeeded, job.Status);
        return job;
    }

    [Fact]
    public async Task A_room_written_for_a_host_is_on_their_shelf_only_and_plays_like_any_other()
    {
        var (host, cookie) = await app.RegisterHostAsync($"room{Guid.NewGuid():N}@example.com");
        var job = await WrittenAsync(host);
        var roomId = job.ScenarioId!;
        Assert.StartsWith("ai-escape-the-fake-lighthouse-", roomId);

        // On the owner's shelf, first and marked as theirs; not on anyone else's.
        var shelf = await Read<List<EscapeRoomSummary>>(await host.GetAsync("/api/escape-rooms"));
        var card = shelf[0];
        Assert.Equal(roomId, card.Id);
        Assert.True(card.Generated);
        Assert.Equal(30, card.TimeLimitMinutes);
        Assert.Equal("Keeper Barnacle", card.GameMaster);
        Assert.Contains(shelf, r => r.Id == "the-workshop" && !r.Generated);
        Assert.DoesNotContain(roomId, await app.CreateClient().GetStringAsync("/api/escape-rooms"));
        var (other, _) = await app.RegisterHostAsync($"other{Guid.NewGuid():N}@example.com");
        Assert.DoesNotContain(roomId, await other.GetStringAsync("/api/escape-rooms"));

        // Nobody else can start a party with it.
        var stolen = await other.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(roomId, PartyMode.SharedScreen, UseAi: false), GameJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);

        // The owner can, and a guest joins and solves its first riddle.
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest(roomId, PartyMode.SharedScreen, UseAi: false), GameJson.Options));
        Assert.Equal("The Fake Lighthouse", party.Title);
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await using var phone = await app.ConnectAsync(seat.Token);
        await phone.InvokeAsync<EscapePlayerView>("JoinSeat");
        await tv.InvokeAsync("EscapeStart", party.Code);

        var stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        var riddle = stage.Puzzles[0];
        Assert.Equal("puzzle-1", riddle.Id); // the model's own names ("echo-riddle") never reach a browser
        Assert.True(await phone.InvokeAsync<bool>("EscapeAnswer", riddle.Id, "Echo!"));
        var after = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(1, after.SolvedCount);

        // Its leaderboard exists (empty so far).
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/escape-rooms/{roomId}/leaderboard")).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_can_delete_a_written_room()
    {
        var (host, _) = await app.RegisterHostAsync($"del{Guid.NewGuid():N}@example.com");
        var roomId = (await WrittenAsync(host)).ScenarioId!;
        var (other, _) = await app.RegisterHostAsync($"other{Guid.NewGuid():N}@example.com");

        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/escape-rooms/{roomId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.DeleteAsync("/api/escape-rooms/the-workshop")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/escape-rooms/{roomId}")).StatusCode);

        Assert.DoesNotContain(roomId, await host.GetStringAsync("/api/escape-rooms"));
        var gone = await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(roomId, PartyMode.SharedScreen), GameJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);
    }

    [Fact]
    public async Task A_theme_is_required_and_one_job_runs_at_a_time()
    {
        var email = $"busy{Guid.NewGuid():N}@example.com";
        var (host, _) = await app.RegisterHostAsync(email);
        Assert.Equal(HttpStatusCode.BadRequest, (await Generate(host, "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsJsonAsync("/api/escape-rooms/generate",
            new EscapeRoomGenerateRequest("a castle", ContentRating.Family, 15), GameJson.Options)).StatusCode);

        // A mystery still being written for this host blocks a room, and the other way round.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = db.Users.Single(u => u.Email == email).Id;
            db.GenerationJobs.Add(new GenerationJobEntity
            {
                Id = Guid.NewGuid(), HostUserId = userId, ThemeSlug = "speakeasy", Request = "{}", Status = GenerationStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Generate(host)).StatusCode);
    }
}

public class EscapeGenerationWithoutAiTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Writing_a_room_needs_a_storyteller()
    {
        var (host, _) = await app.RegisterHostAsync($"noai{Guid.NewGuid():N}@example.com");
        var res = await host.PostAsJsonAsync("/api/escape-rooms/generate", new EscapeRoomGenerateRequest("a castle", ContentRating.Family, 45), GameJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Storyteller", await res.Content.ReadAsStringAsync());
    }
}
