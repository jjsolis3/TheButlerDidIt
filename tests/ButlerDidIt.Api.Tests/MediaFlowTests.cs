using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ButlerDidIt.Api.Tests;

public class MediaFlowTests(FakeAiFactory app) : IClassFixture<FakeAiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> PartyAsync(ContentRating level = ContentRating.Mature, bool drinking = false)
    {
        var (host, cookie) = await app.RegisterHostAsync($"m{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, level, null, UseAi: true, DrinkingPrompts: drinking), GameJson.Options));
        return (host, cookie, party);
    }

    private async Task WaitForMediaAsync(string scenarioId)
    {
        for (var i = 0; i < 150; i++)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.MediaJobs.AsNoTracking().Where(j => j.ScenarioId == scenarioId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync();
            if (job is { Status: MediaJobStatus.Succeeded or MediaJobStatus.Failed }) return;
            await Task.Delay(200);
        }
        Assert.Fail("Media preparation did not finish in time.");
    }

    [Fact]
    public async Task Creating_a_party_prepares_portraits_art_and_voices_once_and_shares_them()
    {
        var (host, cookie, party) = await PartyAsync();
        await WaitForMediaAsync(party.ScenarioId);

        var status = await host.GetStringAsync($"/api/parties/{party.Code}/media");
        Assert.Contains("\"status\":\"succeeded\"", status);

        await using var stage = await app.ConnectAsync(cookie: cookie);
        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.All(view.Cast, c => Assert.StartsWith("/media/assets/", c.Portrait));
        Assert.StartsWith("/media/assets/", view.Scenario.VictimPortrait);

        // The portrait is a real image file.
        var portrait = await app.CreateClient().GetAsync(view.Cast[0].Portrait);
        Assert.Equal(HttpStatusCode.OK, portrait.StatusCode);
        Assert.Equal("image/png", portrait.Content.Headers.ContentType!.MediaType);

        // A second party with the same mystery reuses the files instead of paying again:
        // its preparation job finds nothing left to create.
        var (_, _, second) = await PartyAsync();
        await WaitForMediaAsync(second.ScenarioId);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var latest = await db.MediaJobs.AsNoTracking().Where(j => j.ScenarioId == second.ScenarioId).OrderByDescending(j => j.CreatedAt).FirstAsync();
        Assert.Equal(0, latest.Total);
    }

    [Fact]
    public async Task Narration_and_npc_lines_play_recorded_voices()
    {
        var (_, cookie, party) = await PartyAsync();
        await WaitForMediaAsync(party.ScenarioId);
        foreach (var name in new[] { "Alice", "Bob", "Cara" })
            await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name));

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await stage.InvokeAsync("StartGame", party.Code);
        await stage.InvokeAsync("Advance", party.Code); // prologue
        var prologue = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.Contains(prologue.Cues, c => c.Type == CueType.Narration && c.Src!.StartsWith("/media/assets/"));
        Assert.Contains(prologue.Cues, c => c.Type == CueType.Image && c.Src!.StartsWith("/media/assets/"));

        await stage.InvokeAsync("Advance", party.Code); // act 1 cinematic: NPC lines
        var act = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.All(act.Cues.Where(c => c.Type == CueType.Line), c => Assert.StartsWith("/media/assets/", c.Src));

        var audio = await app.CreateClient().GetAsync(prologue.Cues.First(c => c.Type == CueType.Narration).Src);
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Npc_answers_are_voiced()
    {
        var (_, cookie, party) = await PartyAsync();
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Alice")));
        await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Bob"));
        await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Cara"));

        await using var stage = await app.ConnectAsync(cookie: cookie);
        await using var alice = await app.ConnectAsync(seat.Token);
        await stage.InvokeAsync("StartGame", party.Code);
        foreach (var _ in Enumerable.Range(0, 3)) await stage.InvokeAsync("Advance", party.Code);
        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.True(view.Ai.Voices);

        await alice.InvokeAsync("AskNpc", view.Cast.First(c => c.IsNpc).CharacterId, "Where were you?");
        var entry = (await stage.InvokeAsync<StageView>("WatchParty", party.Code)).Interrogations.Single();
        Assert.StartsWith("/media/assets/", entry.AudioUrl);
    }

    [Fact]
    public async Task Selfies_are_resized_stripped_and_shown_as_avatars()
    {
        var (_, cookie, party) = await PartyAsync();
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Alice")));

        using var bitmap = new SKBitmap(2000, 1500);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.DarkRed);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 90);

        var guest = app.CreateClient();
        guest.DefaultRequestHeaders.Add("X-Seat-Token", seat.Token);
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(png.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "photo", "costume.png");
        var result = GameJson.Deserialize<Dictionary<string, string>>(await (await guest.PostAsync("/api/seat/photo", form)).Content.ReadAsStringAsync());

        var photo = await app.CreateClient().GetAsync(result["photoUrl"]);
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType!.MediaType);
        using var decoded = SKBitmap.Decode(await photo.Content.ReadAsByteArrayAsync());
        Assert.Equal(640, Math.Max(decoded.Width, decoded.Height)); // shrunk to fit 640px

        await using var stage = await app.ConnectAsync(cookie: cookie);
        var view = await stage.InvokeAsync<StageView>("WatchParty", party.Code);
        Assert.Equal(result["photoUrl"], view.Players.Single().PhotoUrl);

        // Not a picture: rejected with a friendly message. Without a seat token: refused.
        using var junk = new MultipartFormDataContent { { new ByteArrayContent("hello"u8.ToArray()), "photo", "x.jpg" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsync("/api/seat/photo", junk)).StatusCode);
        using var anon = new MultipartFormDataContent { { new ByteArrayContent(png.ToArray()), "photo", "x.png" } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsync("/api/seat/photo", anon)).StatusCode);
    }

    [Fact]
    public async Task Printable_kit_is_host_only_and_produces_pdfs()
    {
        var (host, _, party) = await PartyAsync();
        await WaitForMediaAsync(party.ScenarioId);
        foreach (var kind in new[] { "invitations", "booklets", "nametags", "clues" })
        {
            var res = await host.GetAsync($"/api/parties/{party.Code}/kit/{kind}.pdf");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        }

        // Guests and other hosts can't download the secrets.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync($"/api/parties/{party.Code}/kit/booklets.pdf")).StatusCode);
        var (other, _) = await app.RegisterHostAsync($"o{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/parties/{party.Code}/kit/booklets.pdf")).StatusCode);
    }

    [Fact]
    public async Task Drinking_prompts_follow_the_host_choice_and_are_never_on_for_family_parties()
    {
        var (_, cookieOn, on) = await PartyAsync(drinking: true);
        await using var stage = await app.ConnectAsync(cookie: cookieOn);
        Assert.True((await stage.InvokeAsync<StageView>("WatchParty", on.Code)).Options.DrinkingPrompts);

        var (host, cookieFamily) = await app.RegisterHostAsync($"f{Guid.NewGuid():N}@example.com");
        // A family party can't pick Blackwood (mature), so check the flag through the API with a generated family mystery.
        var gen = await Read<ButlerDidIt.Api.Ai.GenerationJobView>(await host.PostAsJsonAsync("/api/generation",
            new ButlerDidIt.Api.Ai.GenerateRequest("speakeasy", 4, ContentRating.Family, ButlerDidIt.Ai.Generation.MysteryLength.Short, null), GameJson.Options));
        ButlerDidIt.Api.Ai.GenerationJobView done = gen;
        for (var i = 0; i < 100 && done.Status is not (GenerationStatus.Succeeded or GenerationStatus.Failed); i++)
        {
            await Task.Delay(200);
            done = await Read<ButlerDidIt.Api.Ai.GenerationJobView>(await host.GetAsync($"/api/generation/{gen.Id}"));
        }
        Assert.Equal(GenerationStatus.Succeeded, done.Status);
        var family = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest(done.ScenarioId!, PartyMode.SharedScreen, ContentRating.Family, null, UseAi: true, DrinkingPrompts: true), GameJson.Options));
        await using var familyStage = await app.ConnectAsync(cookie: cookieFamily);
        Assert.False((await familyStage.InvokeAsync<StageView>("WatchParty", family.Code)).Options.DrinkingPrompts);
    }
}
