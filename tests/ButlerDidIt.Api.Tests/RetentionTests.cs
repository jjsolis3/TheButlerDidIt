using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ButlerDidIt.Api.Tests;

public class RetentionTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<PartyInfo> PartyAsync()
    {
        var (host, _) = await app.RegisterHostAsync($"r{Guid.NewGuid():N}@example.com");
        return await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null, UseAi: false), GameJson.Options));
    }

    private async Task<(HttpClient Guest, string PhotoUrl)> GuestWithSelfieAsync(string code, string name)
    {
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/join", new JoinRequest(name)));
        var guest = app.CreateClient();
        guest.DefaultRequestHeaders.Add("X-Seat-Token", seat.Token);
        return (guest, await UploadAsync(guest));
    }

    private static async Task<string> UploadAsync(HttpClient guest)
    {
        using var bitmap = new SKBitmap(40, 40);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Teal);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 90);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(png.ToArray()), "photo", "me.png" } };
        return (await Read<Dictionary<string, string>>(await guest.PostAsync("/api/seat/photo", form)))["photoUrl"];
    }

    /// <summary>Pretends the party was last touched <paramref name="days"/> ago, without touching anyone else's parties.</summary>
    private async Task AgeAsync(string code, int days, PartyStatus? status = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var when = DateTimeOffset.UtcNow.AddDays(-days);
        await db.Parties.Where(p => p.Code == code).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.UpdatedAt, when)
            .SetProperty(p => p.Status, p => status ?? p.Status));
    }

    private Task<RetentionWorker.Result> RunAsync() => app.Services.GetRequiredService<RetentionWorker>().RunOnceAsync(CancellationToken.None);

    private async Task<HttpStatusCode> StatusOf(string url) => (await app.CreateClient().GetAsync(url)).StatusCode;

    [Fact]
    public async Task Abandoned_parties_are_deleted_with_their_seats_and_selfies()
    {
        var old = await PartyAsync();
        var (guest, photo) = await GuestWithSelfieAsync(old.Code, "Ada");
        var recent = await PartyAsync();
        var (recentGuest, recentPhoto) = await GuestWithSelfieAsync(recent.Code, "Bo");

        await AgeAsync(old.Code, days: 15);
        await RunAsync();

        Assert.Equal(HttpStatusCode.NotFound, await StatusOf($"/api/parties/{old.Code}"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(photo));
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.DeleteAsync("/api/seat/photo")).StatusCode); // the seat token is dead

        // A party in use is left alone.
        Assert.Equal(HttpStatusCode.OK, await StatusOf($"/api/parties/{recent.Code}"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(recentPhoto));
        Assert.Equal(HttpStatusCode.NoContent, (await recentGuest.DeleteAsync("/api/seat/photo")).StatusCode);
    }

    [Fact]
    public async Task Old_finished_parties_keep_their_record_but_lose_seats_notes_and_selfies()
    {
        var party = await PartyAsync();
        var (guest, photo) = await GuestWithSelfieAsync(party.Code, "Ada");
        await AgeAsync(party.Code, days: 31, PartyStatus.Finished);

        await RunAsync();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Parties.AsNoTracking().SingleAsync(p => p.Code == party.Code);
        Assert.NotNull(row.PrunedAt);
        Assert.Empty(await db.Seats.Where(s => s.PartyId == row.Id).ToListAsync());
        var state = GameJson.Deserialize<GameState>(row.State);
        Assert.Equal("Ada", state.Players.Single().Name); // kept for the recap
        Assert.Null(state.Players.Single().PhotoUrl);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(photo));
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.DeleteAsync("/api/seat/photo")).StatusCode);

        // Running again finds nothing more to do for this party. (Status is set again because this
        // test only fakes "finished": the saved game itself is still in its lobby phase.)
        await AgeAsync(party.Code, days: 31, PartyStatus.Finished);
        await RunAsync();
        Assert.Equal(row.PrunedAt, (await db.Parties.AsNoTracking().SingleAsync(p => p.Code == party.Code)).PrunedAt);
    }

    [Fact]
    public async Task Retaking_or_removing_a_selfie_deletes_the_old_file()
    {
        var party = await PartyAsync();
        var (guest, first) = await GuestWithSelfieAsync(party.Code, "Ada");

        var second = await UploadAsync(guest);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(first));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(second));

        await guest.DeleteAsync("/api/seat/photo");
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(second));
    }

    [Fact]
    public async Task Selfies_that_belong_to_no_party_are_swept_up()
    {
        using var scope = app.Services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orphan = await media.SaveUploadAsync(MediaKind.Photo, [1, 2, 3], "image/jpeg", "jpg", partyId: null, CancellationToken.None);
        var fresh = await media.SaveUploadAsync(MediaKind.Photo, [1, 2, 3], "image/jpeg", "jpg", partyId: null, CancellationToken.None);
        await db.MediaAssets.Where(a => a.Id == orphan).ExecuteUpdateAsync(u => u.SetProperty(a => a.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)));

        await RunAsync();

        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(MediaStore.Url(orphan)));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(MediaStore.Url(fresh))); // just uploaded: might be mid-save, so kept for now
    }
}
