using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Escape.Rooms;
using Soundscape = ButlerDidIt.Game.Scenarios.Soundscape;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

public class EscapeFlowTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Host, string Cookie, PartyInfo Party)> EscapePartyAsync(string room, PuzzleChoice puzzles = PuzzleChoice.Fresh, long? set = null, HttpClient? host = null, string? cookie = null)
    {
        if (host is null) (host, cookie) = await app.RegisterHostAsync($"escape{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest(room, PartyMode.SharedScreen, null, puzzles, set), GameJson.Options));
        return (host, cookie!, party);
    }

    private EscapeState StateOf(string code)
    {
        using var scope = app.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == code);
        return GameJson.Deserialize<EscapeState>(row.State);
    }

    /// <summary>The room as this party plays it: its puzzle set's codes and riddles.</summary>
    private EscapeRoom PlayedRoom(string code, string roomId) =>
        EscapeEngine.RoomFor(StateOf(code), app.Services.GetRequiredService<EscapeCatalog>().Find(roomId)!);

    /// <summary>Joins three guests, starts the clock and solves everything.</summary>
    private async Task EscapeAsync(PartyInfo party, string cookie, string roomId)
    {
        var seats = new List<SeatResponse>();
        foreach (var name in new[] { "Ada", "Ben", "Cy" })
            seats.Add(await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name))));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await using var phone = await app.ConnectAsync(seats[0].Token);
        await tv.InvokeAsync("EscapeStart", party.Code);
        var template = app.Services.GetRequiredService<EscapeCatalog>().Find(roomId)!;
        await EscapeHubBot.PlayToEndAsync([phone], [seats[0].SeatId], template, () => StateOf(party.Code));
    }

    [Fact]
    public async Task The_shelf_lists_both_rooms_without_giving_anything_away()
    {
        var json = await app.CreateClient().GetStringAsync("/api/escape-rooms");
        var rooms = GameJson.Deserialize<List<EscapeRoomSummary>>(json);
        Assert.Contains(rooms, r => r.Id == "the-workshop");
        Assert.Contains(rooms, r => r.Id == "the-funhouse");
        Assert.DoesNotContain("3728", json);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_shelf_shows_a_room_s_cover_once_one_is_painted()
    {
        var before = GameJson.Deserialize<List<EscapeRoomSummary>>(await app.CreateClient().GetStringAsync("/api/escape-rooms"));
        var workshop = before.Single(r => r.Id == "the-workshop");
        Assert.Null(workshop.CoverUrl);
        Assert.Equal(Soundscape.Workshop, workshop.Soundscape);

        // The media pipeline paints a room's pictures under its own job id (escape:{room}).
        var asset = Guid.NewGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ScenarioMedia.Add(new ScenarioMediaEntity { ScenarioId = EscapeMedia.JobId("the-workshop"), Key = EscapeArt.Cover, AssetId = asset });
            db.ScenarioMedia.Add(new ScenarioMediaEntity { ScenarioId = EscapeMedia.JobId("the-workshop"), Key = EscapeArt.Stage("stage-1"), AssetId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        var after = GameJson.Deserialize<List<EscapeRoomSummary>>(await app.CreateClient().GetStringAsync("/api/escape-rooms"));
        Assert.Equal($"/media/assets/{asset}", after.Single(r => r.Id == "the-workshop").CoverUrl);
        Assert.All(after.Where(r => r.Id != "the-workshop"), r => Assert.Null(r.CoverUrl)); // only the cover, only for its own room
    }

    [Fact]
    public async Task Three_phones_escape_the_workshop_together()
    {
        var (_, cookie, party) = await EscapePartyAsync("the-workshop");
        Assert.Equal(GameKind.EscapeRoom, party.Kind);
        Assert.Equal("The Workshop", party.Title);

        var seats = new List<SeatResponse>();
        foreach (var name in new[] { "Ada", "Ben", "Cy" })
            seats.Add(await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name))));

        await using var tv = await app.ConnectAsync(cookie: cookie);
        var phones = new List<HubConnection>();
        foreach (var seat in seats) phones.Add(await app.ConnectAsync(seat.Token));
        try
        {
            // A mystery action can't run in an escape room.
            await Assert.ThrowsAsync<HubException>(() => tv.InvokeAsync("StartGame", party.Code));
            await tv.InvokeAsync("EscapeStart", party.Code);

            // Every phone holds some clue pieces, and together they cover the toolbox code.
            var stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
            Assert.Equal(EscapePhase.Playing, stage.Phase);
            Assert.Equal("The Chains", stage.Stage!.Title);

            // Wrong answers don't open anything and show on the TV.
            Assert.False(await phones[0].InvokeAsync<bool>("EscapeAnswer", "tape", "kettle"));
            Assert.Contains((await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).Feed, f => f.Text.Contains("kettle"));
            await Task.Delay(EscapeEngine.WrongAnswerCooldown); // the lock resets

            var room = PlayedRoom(party.Code, "the-workshop");
            // Taking turns on the phones: searching, looking closely, combining and solving.
            await EscapeHubBot.PlayToEndAsync(phones, seats.Select(x => x.SeatId).ToList(), app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!,
                () => StateOf(party.Code));
            stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);

            Assert.Equal(EscapePhase.Escaped, stage.Phase);
            Assert.Equal(room.EscapedText, stage.EndText);
            Assert.Equal(room.Puzzles.Count, stage.SolvedCount);

            using var scope = app.Services.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().SingleAsync(p => p.Code == party.Code);
            Assert.Equal(PartyStatus.Finished, row.Status);
            Assert.Null(row.NextDueAt);
        }
        finally
        {
            foreach (var phone in phones) await phone.DisposeAsync();
        }
    }

    [Fact]
    public async Task Each_phone_sees_only_its_own_clue_pieces_and_nobody_joins_mid_game()
    {
        var (_, cookie, party) = await EscapePartyAsync("the-funhouse");
        var ada = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        var ben = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ben")));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await tv.InvokeAsync("EscapeStart", party.Code);

        await using var adaPhone = await app.ConnectAsync(ada.Token);
        await using var benPhone = await app.ConnectAsync(ben.Token);
        var adaView = await adaPhone.InvokeAsync<EscapePlayerView>("JoinSeat");
        var benView = await benPhone.InvokeAsync<EscapePlayerView>("JoinSeat");
        Assert.Equal("Ada", adaView.Name);
        Assert.Empty(adaView.Pieces.Select(p => p.Text).Intersect(benView.Pieces.Select(p => p.Text)));

        var late = await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Late"));
        Assert.False(late.IsSuccessStatusCode);

        // A hint from the TV costs time for everyone.
        var before = (await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).Deadline;
        await tv.InvokeAsync("EscapeHostHint", party.Code, "mirror-riddle");
        var after = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(before!.Value.AddSeconds(-after.HintPenaltySeconds), after.Deadline);
        Assert.Single(after.Puzzles.Single(p => p.Id == "mirror-riddle").Hints);
    }

    [Fact]
    public async Task Escape_actions_refuse_a_mystery_party()
    {
        var (host, cookie) = await app.RegisterHostAsync($"mystery{Guid.NewGuid():N}@example.com");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties",
            new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null), GameJson.Options));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        var ex = await Assert.ThrowsAsync<HubException>(() => tv.InvokeAsync("EscapeStart", party.Code));
        Assert.Contains("isn't part of this party's game", ex.Message);
    }

    [Fact]
    public async Task Todays_challenge_is_the_same_for_everyone_and_a_puzzle_set_can_be_replayed()
    {
        var (_, _, a) = await EscapePartyAsync("the-funhouse", PuzzleChoice.Daily);
        var (_, _, b) = await EscapePartyAsync("the-funhouse", PuzzleChoice.Daily);
        Assert.Equal(StateOf(a.Code).Seed, StateOf(b.Code).Seed);
        Assert.True(StateOf(a.Code).Daily);

        var (_, _, replay) = await EscapePartyAsync("the-funhouse", PuzzleChoice.Replay, 4242);
        Assert.Equal(4242, StateOf(replay.Code).Seed);
        Assert.False(StateOf(replay.Code).Daily);

        var (host, _) = await app.RegisterHostAsync($"bad{Guid.NewGuid():N}@example.com");
        var bad = await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest("the-funhouse", PartyMode.SharedScreen, null, PuzzleChoice.Replay, 5_000_000), GameJson.Options);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task An_escape_is_recorded_once_and_ranked_and_only_its_host_sees_the_names()
    {
        var (host, cookie, party) = await EscapePartyAsync("the-funhouse", PuzzleChoice.Replay, 777);
        await EscapeAsync(party, cookie, "the-funhouse");

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var partyId = (await db.Parties.SingleAsync(p => p.Code == party.Code)).Id;
            var result = Assert.Single(await db.EscapeResults.Where(r => r.PartyId == partyId).ToListAsync());
            Assert.True(result.Escaped);
            Assert.Equal(777, result.Seed);
            Assert.Equal("Ada, Ben, Cy", result.Team);
            Assert.Equal(result.ElapsedSeconds + result.HintsUsed * 90, result.Score);
        }

        // The host sees their team's names and where this party ranked; everyone else sees only times.
        var mine = await Read<Leaderboard>(await host.GetAsync($"/api/escape-rooms/the-funhouse/leaderboard?party={party.Code}"));
        Assert.NotNull(mine.ThisParty);
        Assert.Equal("Ada, Ben, Cy", mine.ThisParty!.Team);
        Assert.Contains(mine.Top, e => e.ThisParty && e.Mine);
        Assert.Contains(mine.MyBest, e => e.Team == "Ada, Ben, Cy");

        var anyone = await app.CreateClient().GetStringAsync($"/api/escape-rooms/the-funhouse/leaderboard?party={party.Code}");
        Assert.DoesNotContain("Ada", anyone);
        Assert.Contains("\"thisParty\":null", anyone);

        // The shelf shows the best score so far.
        var shelf = GameJson.Deserialize<List<EscapeRoomSummary>>(await app.CreateClient().GetStringAsync("/api/escape-rooms"));
        Assert.NotNull(shelf.Single(r => r.Id == "the-funhouse").BestScore);
    }

    [Fact]
    public async Task Running_out_of_time_is_recorded_but_never_ranked()
    {
        var (host, cookie, party) = await EscapePartyAsync("the-workshop");
        await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Solo")));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await tv.InvokeAsync("EscapeStart", party.Code);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var partyId = (await db.Parties.SingleAsync(p => p.Code == party.Code)).Id;
        // The ticker's command, an hour late: the clock has run out.
        await scope.ServiceProvider.GetRequiredService<ButlerDidIt.Api.Parties.PartyRuntime>().ExecuteAsync(partyId, (s, now) => s.Tick(now.AddHours(1)));
        await scope.ServiceProvider.GetRequiredService<ButlerDidIt.Api.Parties.PartyRuntime>().ExecuteAsync(partyId, (s, now) => s.Tick(now.AddHours(2))); // nothing more to record

        var result = Assert.Single(await db.EscapeResults.AsNoTracking().Where(r => r.PartyId == partyId).ToListAsync());
        Assert.False(result.Escaped);
        var board = await Read<Leaderboard>(await host.GetAsync($"/api/escape-rooms/the-workshop/leaderboard?party={party.Code}"));
        Assert.Null(board.ThisParty);
        Assert.DoesNotContain(board.Top, e => e.ThisParty);
    }

    [Fact]
    public async Task The_shelf_offers_each_rooms_lengths_and_seasons()
    {
        var rooms = GameJson.Deserialize<List<EscapeRoomSummary>>(await app.CreateClient().GetStringAsync("/api/escape-rooms"));
        var asylum = rooms.Single(r => r.Id == "the-asylum");
        Assert.Equal([30, 45, 60], asylum.Lengths.Select(l => l.Minutes));
        Assert.True(asylum.Lengths[0].PuzzleCount < asylum.Lengths[^1].PuzzleCount);
        Assert.Contains("halloween", asylum.Seasons);
        Assert.Equal(asylum.Lengths.Single(l => l.Minutes == 45).PuzzleCount, asylum.PuzzleCount); // the card shows the standard game
        Assert.Contains(rooms, r => r.Id == "the-toy-factory" && r.ContentRating == ButlerDidIt.Game.Scenarios.ContentRating.Family);
        Assert.Contains(rooms, r => r.Id == "the-bunker" && r.ContentRating == ButlerDidIt.Game.Scenarios.ContentRating.Mature);
        Assert.Contains(rooms, r => r.Id == "the-wizards-tower" && r.Seasons.Contains("halloween"));
        Assert.Contains(rooms, r => r.Id == "the-pirate-ship" && r.ContentRating == ButlerDidIt.Game.Scenarios.ContentRating.Family);
    }

    [Fact]
    public async Task A_shorter_game_plays_fewer_puzzles_on_a_shorter_clock_and_has_its_own_leaderboard()
    {
        var (host, cookie) = await app.RegisterHostAsync($"short{Guid.NewGuid():N}@example.com");
        // A length the room doesn't offer is refused.
        var bad = await host.PostAsJsonAsync("/api/parties/escape", new CreateEscapePartyRequest("the-funhouse", PartyMode.SharedScreen, Minutes: 90), GameJson.Options);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);

        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, Minutes: 30), GameJson.Options));
        await EscapeAsync(party, cookie, "the-workshop");

        await using var tv = await app.ConnectAsync(cookie: cookie);
        var end = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(EscapePhase.Escaped, end.Phase);
        Assert.Equal(30, end.TimeLimitMinutes);
        Assert.True(end.PuzzleCount < app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!.Puzzles.Count);

        using (var scope = app.Services.CreateScope())
        {
            var result = scope.ServiceProvider.GetRequiredService<AppDbContext>().EscapeResults.AsNoTracking().Single(r => r.PartyId == StateParty(party.Code));
            Assert.Equal(30, result.Minutes);
        }
        // On the 30-minute board, not on the standard one.
        var thirty = await Read<Leaderboard>(await host.GetAsync($"/api/escape-rooms/the-workshop/leaderboard?minutes=30&party={party.Code}"));
        Assert.Equal(30, thirty.Minutes);
        Assert.NotNull(thirty.ThisParty);
        var standard = await Read<Leaderboard>(await host.GetAsync($"/api/escape-rooms/the-workshop/leaderboard?party={party.Code}"));
        Assert.Equal(45, standard.Minutes);
        Assert.Null(standard.ThisParty);
    }

    [Fact]
    public async Task With_take_it_a_puzzle_is_answered_only_by_whoever_took_it_and_the_host_can_free_it()
    {
        var (host, cookie) = await app.RegisterHostAsync($"turns{Guid.NewGuid():N}@example.com");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await host.PostAsJsonAsync("/api/parties/escape",
            new { roomId = "the-workshop", mode = "sharedScreen", answering = "everyoneAtOnce" }, GameJson.Options)).StatusCode);
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, Answering: AnswerRule.TakeIt), GameJson.Options));
        var ada = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        var ben = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ben")));
        await using var tv = await app.ConnectAsync(cookie: cookie);
        await using var adaPhone = await app.ConnectAsync(ada.Token);
        await using var benPhone = await app.ConnectAsync(ben.Token);
        await tv.InvokeAsync("EscapeStart", party.Code);

        var stage = await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code);
        Assert.Equal(AnswerRule.TakeIt, stage.Answering);
        var puzzle = stage.Puzzles.First(p => p.Kind is PuzzleKind.Code or PuzzleKind.Text && p.Needs.Count == 0);
        var answer = PlayedRoom(party.Code, "the-workshop").FindPuzzle(puzzle.Id)!.Answers[0];

        var refused = await Assert.ThrowsAsync<HubException>(() => adaPhone.InvokeAsync<bool>("EscapeAnswer", puzzle.Id, answer));
        Assert.Contains("Take this puzzle first", refused.Message);
        await adaPhone.InvokeAsync("EscapeTake", puzzle.Id);
        Assert.Equal("Ada", (await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).Puzzles.Single(p => p.Id == puzzle.Id).HeldBy?.Name);
        await Assert.ThrowsAsync<HubException>(() => benPhone.InvokeAsync<bool>("EscapeAnswer", puzzle.Id, answer));

        // Ada passes it to Ben; the host frees it from the TV; Ben takes it back and opens it.
        await adaPhone.InvokeAsync("EscapePass", puzzle.Id, ben.SeatId);
        Assert.Equal(ben.SeatId, StateOf(party.Code).Holds[puzzle.Id].SeatId);
        await Assert.ThrowsAsync<HubException>(() => benPhone.InvokeAsync("EscapeHostFree", party.Code, puzzle.Id)); // a phone isn't the host
        await tv.InvokeAsync("EscapeHostFree", party.Code, puzzle.Id);
        Assert.False(StateOf(party.Code).Holds.ContainsKey(puzzle.Id));
        await benPhone.InvokeAsync("EscapeTake", puzzle.Id);
        await benPhone.InvokeAsync("EscapeRelease", puzzle.Id);
        await benPhone.InvokeAsync("EscapeTake", puzzle.Id);
        Assert.True(await benPhone.InvokeAsync<bool>("EscapeAnswer", puzzle.Id, answer));
    }

    private Guid StateParty(string code)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == code).Id;
    }
}
