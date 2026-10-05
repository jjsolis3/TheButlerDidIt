using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Insights;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>Guest feedback and the insights it feeds (#130).</summary>
public class InsightsTests(MediaFactory app) : IClassFixture<MediaFactory>
{
    private const string Blackwood = "death-at-blackwood-manor";

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    /// <summary>The admin first: whoever signs up first runs the site.</summary>
    private async Task<(HttpClient Client, string Cookie)> HostAsync(string prefix)
    {
        await app.AdminAsync();
        return await app.RegisterHostAsync($"{prefix}{Guid.NewGuid():N}@example.com");
    }

    private HttpClient Seat(string token)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Seat-Token", token);
        return client;
    }

    private T StateOf<T>(string code)
    {
        using var scope = app.Services.CreateScope();
        return GameJson.Deserialize<T>(scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == code).State);
    }

    /// <summary>A copy of Blackwood that this host owns (as if they'd had the AI write it): only the admin may copy a built-in one.</summary>
    private async Task<string> OwnMysteryAsync(HttpClient host)
    {
        var me = System.Text.Json.Nodes.JsonNode.Parse(await host.GetStringAsync("/api/auth/me"))!["id"]!.GetValue<string>();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.Scenarios.AsNoTracking().SingleAsync(s => s.Id == Blackwood);
        var id = $"blackwood-{Guid.NewGuid():N}"[..20];
        var doc = System.Text.Json.Nodes.JsonNode.Parse(original.Document)!;
        doc["id"] = id;
        db.Scenarios.Add(new ScenarioEntity
        {
            Id = id, ThemeSlug = original.ThemeSlug, Title = original.Title, MinPlayers = original.MinPlayers, MaxPlayers = original.MaxPlayers,
            ContentRating = original.ContentRating, Source = ScenarioSource.Custom, OwnerUserId = me, Document = doc.ToJsonString(), UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private Guid PartyIdOf(string code)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Parties.AsNoTracking().Single(p => p.Code == code).Id;
    }

    private async Task<List<PlayRecord>> RecordsAsync(string contentId)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PlayRecords.AsNoTracking().Where(r => r.ContentId == contentId).ToListAsync();
    }

    /// <summary>A Blackwood party walked to its reveal: Alice (Finch, the killer) and Bob accuse Finch, Cara accuses Hargrove.</summary>
    private async Task<(PartyInfo Party, List<SeatResponse> Seats, HttpClient Host)> MysteryAtTheRevealAsync(string scenarioId = Blackwood, HttpClient? host = null, string? cookie = null)
    {
        if (host is null) (host, cookie) = await HostAsync("m");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties", new CreatePartyRequest(scenarioId, PartyMode.SharedScreen, null, Version: scenarioId), GameJson.Options));
        var seats = new List<SeatResponse>();
        foreach (var name in new[] { "Alice", "Bob", "Cara" })
            seats.Add(await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name))));
        await using var stage = await app.ConnectAsync(cookie: cookie);
        var phones = new List<HubConnection>();
        foreach (var s in seats) phones.Add(await app.ConnectAsync(s.Token));
        try
        {
            await phones[0].InvokeAsync("ChooseCharacter", "finch");
            await phones[1].InvokeAsync("ChooseCharacter", "hargrove");
            await stage.InvokeAsync("StartGame", party.Code);
            while (StateOf<GameState>(party.Code).Phase != Phase.Accusation) await stage.InvokeAsync("Advance", party.Code);
            await phones[0].InvokeAsync("SubmitAccusation", "finch", "exposure", "digitalis");
            await phones[1].InvokeAsync("SubmitAccusation", "finch", "debt", "candlestick");
            await phones[2].InvokeAsync("SubmitAccusation", "hargrove", "inheritance", "candlestick");
            await stage.InvokeAsync("Advance", party.Code); // the reveal
        }
        finally
        {
            foreach (var p in phones) await p.DisposeAsync();
        }
        return (party, seats, host);
    }

    [Fact]
    public async Task A_mystery_s_reveal_records_who_named_the_killer_and_guests_rate_the_game()
    {
        var (party, seats, host) = await MysteryAtTheRevealAsync();
        var record = Assert.Single(await RecordsAsync(Blackwood), r => r.PartyId == PartyIdOf(party.Code));
        Assert.Equal(GameKind.Mystery, record.Kind);
        Assert.Equal((3, 3, 2), (record.PlayerCount, record.Accusers!.Value, record.Correct!.Value));
        Assert.Equal(new Dictionary<string, int> { ["finch"] = 2, ["hargrove"] = 1 }, GameJson.Deserialize<PlayDetails>(record.Details).Accused);

        // The guests rate it from their phones. Blackwood is an Adults story: comments are welcome.
        var alice = Seat(seats[0].Token);
        var view = await Read<SeatFeedbackView>(await alice.GetAsync("/api/seat/feedback"));
        Assert.True(view.Open && view.CommentsAllowed && view.Given is null);
        await Read<SeatFeedbackView>(await alice.PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(4, FeedbackDifficulty.JustRight, "  Loved the séance!  "), GameJson.Options));
        // Changing their mind replaces the answer rather than adding a second.
        var changed = await Read<SeatFeedbackView>(await alice.PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(5, FeedbackDifficulty.TooHard, "Loved the séance!"), GameJson.Options));
        Assert.Equal(new FeedbackRequest(5, FeedbackDifficulty.TooHard, "Loved the séance!"), changed.Given);
        await Read<SeatFeedbackView>(await Seat(seats[1].Token).PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(3, FeedbackDifficulty.JustRight, null), GameJson.Options));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(6, FeedbackDifficulty.JustRight, null), GameJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await alice.PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(4, FeedbackDifficulty.JustRight, new string('x', 281)), GameJson.Options)).StatusCode);

        // A hand-written mystery's insights are the admin's: the host who played it can't see them, and nobody can probe.
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/insights/mystery/{Blackwood}")).StatusCode);
        var admin = await app.AdminAsync();
        var insights = await Read<InsightsView>(await admin.GetAsync($"/api/insights/mystery/{Blackwood}"));
        Assert.True(insights.Plays >= 1);
        Assert.True(insights.Rating.Count >= 2);
        Assert.Contains(insights.Comments, c => c.Comment == "Loved the séance!" && c.Rating == 5);
        var asWritten = Assert.Single(insights.Mystery!.Versions, v => v.Id == Blackwood);
        Assert.Equal("Dr. Cornelius Finch", asWritten.KillerName);
        Assert.Contains(asWritten.Accused, a => a.CharacterId == "finch" && a.Killer && a.Count >= 2);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/insights/mystery/no-such-mystery")).StatusCode);

        // The admin's My mysteries card sums it up.
        var mine = await Read<List<MyMystery>>(await admin.GetAsync("/api/scenarios/mine"));
        Assert.True(mine.Single(m => m.Id == Blackwood).Insights!.Plays >= 1);
    }

    [Fact]
    public async Task Feedback_waits_for_the_end_and_a_family_game_never_asks_for_words_and_watchers_never_rate()
    {
        var (host, cookie) = await HostAsync("f");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties", new CreatePartyRequest("who-crashed-the-reunion", PartyMode.SharedScreen, null), GameJson.Options));
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Kid")));
        foreach (var name in new[] { "Mum", "Dad" }) // the story needs three
            await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name)));
        var phone = Seat(seat.Token);
        var before = await Read<SeatFeedbackView>(await phone.GetAsync("/api/seat/feedback"));
        Assert.False(before.Open);
        Assert.False(before.CommentsAllowed); // a Family story
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(5, FeedbackDifficulty.JustRight, null), GameJson.Options)).StatusCode);

        // At the reveal it opens, and the comment a child might type is dropped.
        await using (var stage = await app.ConnectAsync(cookie: cookie))
        {
            await stage.InvokeAsync("StartGame", party.Code);
            while (StateOf<GameState>(party.Code).Phase != Phase.Reveal) await stage.InvokeAsync("Advance", party.Code);
        }
        var given = await Read<SeatFeedbackView>(await phone.PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(5, FeedbackDifficulty.TooEasy, "my name is Sam"), GameJson.Options));
        Assert.Equal(new FeedbackRequest(5, FeedbackDifficulty.TooEasy, null), given.Given);

        // A watcher's token has no seat.
        var watcher = await Read<WatchResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/watch", new WatchRequest("Grandma")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Seat(watcher.Token).PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(5, FeedbackDifficulty.JustRight, null), GameJson.Options)).StatusCode);
    }

    [Fact]
    public async Task An_escape_game_records_how_long_each_puzzle_held_the_group_up()
    {
        var (host, cookie) = await HostAsync("e");
        var party = await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, null, PuzzleChoice.Fresh, null), GameJson.Options));
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));
        EscapePuzzleView puzzle;
        await using (var tv = await app.ConnectAsync(cookie: cookie))
        await using (var phone = await app.ConnectAsync(seat.Token))
        {
            await tv.InvokeAsync("EscapeStart", party.Code);
            puzzle = (await tv.InvokeAsync<EscapeStageView>("WatchParty", party.Code)).Puzzles.First(p => p.Needs.Count == 0);
            await phone.InvokeAsync("EscapeHint", puzzle.Id); // one hint, on the first puzzle
            await EscapeHubBot.PlayToEndAsync([phone], [seat.SeatId], app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!,
                () => StateOf<EscapeState>(party.Code));
        }

        var record = Assert.Single(await RecordsAsync("the-workshop"), r => r.PartyId == PartyIdOf(party.Code));
        Assert.True(record.Escaped);
        Assert.Equal(1, record.HintsUsed);
        var played = EscapeEngine.RoomFor(StateOf<EscapeState>(party.Code), app.Services.GetRequiredService<EscapeCatalog>().Find("the-workshop")!);
        var times = GameJson.Deserialize<PlayDetails>(record.Details).Puzzles!;
        Assert.Equal(played.Stages.SelectMany(s => s.Puzzles), times.Select(t => t.PuzzleId)); // every puzzle the game played, in order
        Assert.All(times, t => Assert.True(t.Solved && t.Seconds >= 0 && !t.Stuck));
        Assert.Equal(1, times.Single(t => t.PuzzleId == puzzle.Id).Hints);

        // A built-in room's insights are the admin's; its card on the admin's library sums them up.
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/api/insights/escape/the-workshop")).StatusCode);
        var admin = await app.AdminAsync();
        var insights = await Read<InsightsView>(await admin.GetAsync("/api/insights/escape/the-workshop"));
        Assert.True(insights.Escape!.Escaped >= 1);
        var first = insights.Escape.Puzzles.Single(p => p.PuzzleId == puzzle.Id);
        Assert.True(first.Plays >= 1 && first.HintRate > 0);
        var library = await Read<List<EscapeLibraryItem>>(await admin.GetAsync("/api/escape-rooms/library"));
        Assert.True(library.Single(i => i.Room.Id == "the-workshop").Insights!.Plays >= 1);
        // …and a host's library never carries a built-in room's.
        Assert.Null((await Read<List<EscapeLibraryItem>>(await host.GetAsync("/api/escape-rooms/library"))).Single(i => i.Room.Id == "the-workshop").Insights);
    }

    [Fact]
    public async Task A_host_sees_their_own_copy_s_insights_and_deleting_their_account_takes_them_away()
    {
        var (host, cookie) = await HostAsync("c");
        var copy = await OwnMysteryAsync(host);
        var (_, seats, _) = await MysteryAtTheRevealAsync(copy, host, cookie);
        await Read<SeatFeedbackView>(await Seat(seats[0].Token).PostAsJsonAsync("/api/seat/feedback", new FeedbackRequest(2, FeedbackDifficulty.TooHard, "Too many clues"), GameJson.Options));

        var insights = await Read<InsightsView>(await host.GetAsync($"/api/insights/mystery/{copy}"));
        Assert.Equal((1, 1, 2.0), (insights.Plays, insights.Rating.Count, insights.Rating.Average!.Value));
        Assert.Equal(new[] { 0, 1, 0, 0, 0 }, insights.Rating.Stars);
        Assert.Equal(2, insights.Mystery!.Correct);
        // Another host can't see them.
        var (other, _) = await HostAsync("o");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/insights/mystery/{copy}")).StatusCode);

        Assert.True((await host.PostAsJsonAsync("/api/account/delete", new DeleteAccountRequest("password123"))).IsSuccessStatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The party is gone with the account, and so is everything recorded about their own mystery.
        Assert.False(await db.PlayRecords.AnyAsync(r => r.ContentId == copy));
        Assert.False(await db.PlayFeedback.AnyAsync(f => f.ContentId == copy));
    }
}
