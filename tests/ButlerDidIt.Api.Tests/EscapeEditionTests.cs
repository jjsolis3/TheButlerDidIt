using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Game;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>A rebuilt room starts fresh leaderboards: results from its old edition stay stored but aren't ranked against the new one.</summary>
public class EscapeEditionTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private EscapeResult Result(string room, int score, int? edition) => new()
    {
        Id = Guid.NewGuid(), RoomId = room, PartyId = Guid.NewGuid(), HostUserId = "someone", Seed = 1, Minutes = 45,
        Escaped = true, ElapsedSeconds = score, Score = score, PlayerCount = 3, Team = "Old team", FinishedAt = DateTimeOffset.UtcNow, Edition = edition,
    };

    [Fact]
    public async Task Old_edition_times_drop_off_the_boards_and_the_shelf()
    {
        var workshop = app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!;
        Assert.Equal(4, workshop.Edition); // rebuilt (2), given decoy keys (3), then locks to find and a final lock (4)
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EscapeResults.AddRange(
                Result("the-workshop", 300, null), // from before editions: edition 1
                Result("the-workshop", 400, 1),
                Result("the-workshop", 900, 2),
                Result("the-workshop", 1200, 3),
                Result("the-workshop", 1500, 4));
            await db.SaveChangesAsync();
        }

        var board = GameJson.Deserialize<Leaderboard>(await app.CreateClient().GetStringAsync("/api/escape-rooms/the-workshop/leaderboard"));
        Assert.Equal(4, board.Edition);
        Assert.Equal([1500], board.Top.Select(e => e.Score)); // the fast old times aren't on the new board

        var shelf = GameJson.Deserialize<List<EscapeRoomSummary>>(await app.CreateClient().GetStringAsync("/api/escape-rooms"));
        Assert.Equal(1500, shelf.Single(r => r.Id == "the-workshop").BestScore);
    }
}
