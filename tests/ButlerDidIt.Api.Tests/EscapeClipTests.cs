using System.Net.Http.Json;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>The app with the Fake provider for every AI role, the Filmmaker included (#110 step 3).</summary>
public sealed class FakeFilmmakerFactory : FakeAiFactory
{
    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
        [.. base.ExtraSettings, ("Ai:Roles:Filmmaker:Provider", "Fake"), ("Ai:Roles:Filmmaker:Model", "fake-video")];
}

/// <summary>
/// AI clips of an escape room's stages (#110 step 3): the room's media job brings each stage's picture to life after
/// painting it, the TV gets the clip of the stage in front of the group only, and a host's own video or picture of
/// a stage decides what's filmed.
/// </summary>
public class EscapeClipTests(FakeFilmmakerFactory app) : IClassFixture<FakeFilmmakerFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static async Task<PartyInfo> PartyAsync(HttpClient host, string roomId) =>
        await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(roomId, PartyMode.SharedScreen, UseAi: true), GameJson.Options));

    /// <summary>Waits for the room's media job to finish: pictures, readings, then clips.</summary>
    private async Task<Dictionary<string, Guid>> PreparedAsync(string roomId)
    {
        for (var i = 0; i < 200; i++)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = await db.MediaJobs.AsNoTracking().Where(j => j.ScenarioId == EscapeMedia.JobId(roomId)).ToListAsync();
            if (jobs.Count > 0 && jobs.All(j => j.Status is MediaJobStatus.Succeeded or MediaJobStatus.Failed))
                return await db.ScenarioMedia.AsNoTracking().Where(m => m.ScenarioId == EscapeMedia.JobId(roomId)).ToDictionaryAsync(m => m.Key, m => m.AssetId);
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"The media for {roomId} was never prepared.");
    }

    [Fact]
    public async Task Each_stage_is_filmed_from_its_picture_and_the_tv_gets_only_the_stage_in_front_of_it()
    {
        var (host, cookie) = await app.RegisterHostAsync($"clips{Guid.NewGuid():N}@example.com");
        var party = await PartyAsync(host, "the-workshop");
        await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada"));
        var media = await PreparedAsync("the-workshop");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = scope.ServiceProvider.GetRequiredService<EscapeCatalog>().Find("the-workshop")!;
        foreach (var stage in room.Stages)
        {
            // A clip for every stage, made by the Filmmaker from that stage's own picture.
            var clip = await db.MediaAssets.AsNoTracking().SingleAsync(a => a.Id == media[EscapeArt.StageFilm(stage.Id)]);
            Assert.Equal((MediaKind.Video, "video/webm"), (clip.Kind, clip.ContentType));
            Assert.Contains(stage.Title, clip.Prompt);
            Assert.Contains(EscapeArt.Stage(stage.Id), media.Keys);
        }
        Assert.True(await db.AiUsage.AnyAsync(u => u.Role == AiRole.Filmmaker && u.Purpose == "video" && u.Success));

        await using var tv = await app.ConnectAsync(cookie: cookie);
        var lobby = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Null(lobby.StageFilmUrl); // no stage before the clock starts
        await tv.InvokeAsync("EscapeStart", party.Code);
        var playing = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal($"/media/assets/{media[EscapeArt.StageFilm(room.Stages[0].Id)]}", playing.StageFilmUrl);
        // A later stage's clip is never sent, like its picture.
        foreach (var later in room.Stages.Skip(1))
            Assert.DoesNotContain(media[EscapeArt.StageFilm(later.Id)].ToString(), GameJson.Serialize(playing));
    }

    [Fact]
    public async Task A_hosts_own_stage_video_needs_no_clip_and_a_new_stage_picture_is_filmed_again()
    {
        var (host, _) = await app.RegisterHostAsync($"clipcopy{Guid.NewGuid():N}@example.com");
        var copy = await RoomMedia.CopyAsync(host, "the-workshop");
        // A copy has the original's stages.
        var stages = app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!.Stages.Select(s => s.Id).ToList();
        Assert.True((await RoomMedia.UploadAsync(host, copy, EscapeArt.StageVideo(stages[0]), RoomMedia.Mp4(), "video/mp4")).IsSuccessStatusCode);

        await PartyAsync(host, copy);
        var media = await PreparedAsync(copy);
        // The host's video tells the first stage: no clip of the AI's for it (not even one the copy started with).
        Assert.DoesNotContain(EscapeArt.StageFilm(stages[0]), media.Keys);
        var oldClip = media[EscapeArt.StageFilm(stages[1])];

        // A picture of their own for the second stage: the clip of the old one goes, and the next game films the new one.
        Assert.True((await RoomMedia.UploadAsync(host, copy, EscapeArt.Stage(stages[1]), RoomMedia.Png(640, 360), "image/png")).IsSuccessStatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScenarioMedia
                .AnyAsync(m => m.ScenarioId == EscapeMedia.JobId(copy) && m.Key == EscapeArt.StageFilm(stages[1])));

        await PartyAsync(host, copy);
        var again = await PreparedAsync(copy);
        Assert.NotEqual(oldClip, again[EscapeArt.StageFilm(stages[1])]);
    }
}
