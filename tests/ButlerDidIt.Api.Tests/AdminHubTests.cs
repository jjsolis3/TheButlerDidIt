using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Plans;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>A server whose admin is made once, first, and shared by the tests.</summary>
public class AdminHubFactory : ApiFactory
{
    private readonly Lazy<Task<HttpClient>> _admin;

    public AdminHubFactory() => _admin = new(async () => (await RegisterHostAsync("admin@example.com")).Client);

    /// <summary>The admin's client. Call it before anyone else signs up, or they would become the admin.</summary>
    public Task<HttpClient> AdminAsync() => _admin.Value;
}

/// <summary>The same, on a server whose configuration closes sign-ups.</summary>
public sealed class ClosedAdminHubFactory : AdminHubFactory
{
    protected override bool AllowRegistration => false;
}

/// <summary>
/// The admin hub (#102). The tests in a class share a database, so each one counts what it adds itself (a "before"
/// and an "after") rather than expecting totals.
/// </summary>
public class AdminHubTests(AdminHubFactory app) : IClassFixture<AdminHubFactory>
{
    private const string Blackwood = "death-at-blackwood-manor";

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Client, string Id)> HostAsync()
    {
        await app.AdminAsync();
        var (client, _) = await app.RegisterHostAsync($"host{Guid.NewGuid():N}@example.com");
        var id = System.Text.Json.Nodes.JsonNode.Parse(await client.GetStringAsync("/api/auth/me"))!["id"]!.GetValue<string>();
        return (client, id);
    }

    private async Task<AdminOverview> OverviewAsync() => await Read<AdminOverview>(await (await app.AdminAsync()).GetAsync("/api/admin/overview"));
    private async Task<List<AdminGameRow>> GamesAsync() => await Read<List<AdminGameRow>>(await (await app.AdminAsync()).GetAsync("/api/admin/games"));

    private async Task WithDbAsync(Func<AppDbContext, Task> change)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private static PlayRecord Record(GameKind kind, string contentId, DateTimeOffset at, int players = 4, int? accusers = null, int? correct = null, bool? escaped = null) => new()
    {
        PartyId = Guid.NewGuid(), Kind = kind, ContentId = contentId, HostUserId = "", FinishedAt = at, PlayerCount = players, DurationSeconds = 3600,
        Accusers = accusers, Correct = correct, Escaped = escaped, Details = "{}",
    };

    private static PlayFeedback Vote(GameKind kind, string contentId, int stars, FeedbackDifficulty feel, DateTimeOffset at) => new()
    {
        PartyId = Guid.NewGuid(), SeatId = Guid.NewGuid(), Kind = kind, ContentId = contentId, HostUserId = "", Rating = stars, Difficulty = feel, CreatedAt = at,
    };

    [Fact]
    public async Task Only_the_admin_opens_the_hub()
    {
        var (host, _) = await HostAsync();
        foreach (var url in new[] { "/api/admin/overview", "/api/admin/games", "/api/admin/signups" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }).GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutAsJsonAsync("/api/admin/signups", new SignUpsRequest(false), GameJson.Options)).StatusCode);
        Assert.True((await (await app.AdminAsync()).GetAsync("/api/admin/overview")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_overview_counts_new_hosts_their_plans_and_this_weeks_parties()
    {
        var before = await OverviewAsync();

        // Two new hosts, both on the free trial; the admin gives one of them free access.
        var (first, _) = await HostAsync();
        var (_, secondId) = await HostAsync();
        Assert.True((await (await app.AdminAsync()).PostAsync($"/api/admin/hosts/{secondId}/free-access", null)).IsSuccessStatusCode);

        // The first plays a mystery with two guests. A party still in its lobby isn't counted: it may never be played.
        var party = await Read<PartyInfo>(await first.PostAsJsonAsync("/api/parties", new CreatePartyRequest(Blackwood, PartyMode.SharedScreen, null), GameJson.Options));
        await first.PostAsJsonAsync("/api/parties", new CreatePartyRequest(Blackwood, PartyMode.SharedScreen, null), GameJson.Options);
        foreach (var name in new[] { "Ada", "Ben" })
            await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest(name)));
        await WithDbAsync(db => db.Parties.Where(p => p.Code == party.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PartyStatus.InProgress).SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow)));

        var after = await OverviewAsync();
        Assert.Equal(before.Hosts.Total + 2, after.Hosts.Total);
        Assert.Equal(before.Hosts.NewThisWeek + 2, after.Hosts.NewThisWeek);
        Assert.Equal(before.Hosts.Active + 1, after.Hosts.Active);
        int Plan(AdminOverview o, AccessPlan plan) => o.Plans.FirstOrDefault(p => p.Plan == plan)?.Hosts ?? 0;
        Assert.Equal(Plan(before, AccessPlan.Trial) + 1, Plan(after, AccessPlan.Trial));
        Assert.Equal(Plan(before, AccessPlan.Free) + 1, Plan(after, AccessPlan.Free));
        Assert.DoesNotContain(after.Plans, p => p.Plan == AccessPlan.Admin);

        Assert.Equal(before.Parties.Mysteries + 1, after.Parties.Mysteries);
        Assert.Equal(before.Parties.Guests + 2, after.Parties.Guests);
        Assert.Equal(before.Parties.LiveNow + 1, after.Parties.LiveNow);
    }

    [Fact]
    public async Task The_weekly_chart_counts_games_played_to_the_end_by_week_and_game()
    {
        var now = DateTimeOffset.UtcNow;
        var before = await OverviewAsync();
        await WithDbAsync(db =>
        {
            db.PlayRecords.AddRange(
                Record(GameKind.Mystery, Blackwood, now), Record(GameKind.EscapeRoom, "the-workshop", now, escaped: true),
                Record(GameKind.Mystery, Blackwood, now.AddDays(-7)),
                Record(GameKind.Mystery, Blackwood, now.AddDays(-90))); // too long ago for the chart
            return Task.CompletedTask;
        });

        var after = await OverviewAsync();
        Assert.Equal(8, after.Weeks.Count);
        Assert.Equal(DateOnly.FromDateTime(AdminHubEndpoints.Monday(now).UtcDateTime), after.Weeks[^1].Week);
        Assert.Equal(before.Weeks[^1].Mysteries + 1, after.Weeks[^1].Mysteries);
        Assert.Equal(before.Weeks[^1].EscapeRooms + 1, after.Weeks[^1].EscapeRooms);
        Assert.Equal(before.Weeks[^2].Mysteries + 1, after.Weeks[^2].Mysteries);
        Assert.Equal(before.Weeks.Sum(w => w.Mysteries) + 2, after.Weeks.Sum(w => w.Mysteries));
    }

    [Fact]
    public void Weeks_start_on_monday_at_midnight_utc()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), AdminHubEndpoints.Monday(new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), AdminHubEndpoints.Monday(new DateTimeOffset(2026, 10, 11, 23, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), AdminHubEndpoints.Monday(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)));
        // An evening in Phoenix is already the next day in UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), AdminHubEndpoints.Monday(new DateTimeOffset(2026, 10, 11, 20, 0, 0, TimeSpan.FromHours(-7))));
    }

    [Fact]
    public async Task The_games_list_adds_up_each_mystery_with_its_versions_and_each_room()
    {
        var now = DateTimeOffset.UtcNow;
        string version = "";
        var before = (await GamesAsync()).ToDictionary(g => (g.Kind, g.Id));
        await WithDbAsync(async db =>
        {
            version = await db.Scenarios.Where(s => s.VariantOf == Blackwood).Select(s => s.Id).FirstAsync();
            db.PlayRecords.AddRange(
                Record(GameKind.Mystery, Blackwood, now, accusers: 4, correct: 1),
                Record(GameKind.Mystery, version, now.AddDays(-60), accusers: 4, correct: 3),
                Record(GameKind.EscapeRoom, "the-bunker", now, escaped: true),
                Record(GameKind.EscapeRoom, "the-bunker", now, escaped: false));
            db.PlayFeedback.AddRange(
                Vote(GameKind.Mystery, Blackwood, 5, FeedbackDifficulty.JustRight, now),
                Vote(GameKind.Mystery, version, 2, FeedbackDifficulty.TooHard, now));
        });

        var games = await GamesAsync();
        // A version isn't a row of its own: its games count on its mystery's.
        Assert.DoesNotContain(games, g => g.Id == version);
        var blackwood = games.Single(g => g.Kind == GameKind.Mystery && g.Id == Blackwood);
        var was = before[(GameKind.Mystery, Blackwood)];
        Assert.Equal(ContentOrigin.BuiltIn, blackwood.Origin);
        Assert.Null(blackwood.Owner);
        Assert.Equal(was.Plays + 2, blackwood.Plays);
        Assert.Equal(was.RecentPlays + 1, blackwood.RecentPlays);
        Assert.Equal(was.Ratings + 2, blackwood.Ratings);
        Assert.Equal(was.TooHard + 1, blackwood.TooHard);
        Assert.NotNull(blackwood.SolveRate);
        Assert.NotNull(blackwood.Rating);

        var bunker = games.Single(g => g.Kind == GameKind.EscapeRoom && g.Id == "the-bunker");
        Assert.Equal(before[(GameKind.EscapeRoom, "the-bunker")].Plays + 2, bunker.Plays);
        Assert.Equal(ContentOrigin.BuiltIn, bunker.Origin);
        // Every built-in room is listed, played or not.
        Assert.Contains(games, g => g.Kind == GameKind.EscapeRoom && g.Id == "the-workshop");
    }

    [Fact]
    public async Task A_hosts_own_room_shows_its_owner_and_exact_numbers()
    {
        // A host's own room, played twice (one escape) and rated twice: a fresh row, so every number is known.
        var (_, hostId) = await HostAsync();
        var roomId = $"room-{Guid.NewGuid():N}"[..20];
        var now = DateTimeOffset.UtcNow;
        await WithDbAsync(db =>
        {
            db.EscapeRooms.Add(new EscapeRoomEntity
            {
                Id = roomId, OwnerUserId = hostId, Title = "Grandma's Attic", ContentRating = ButlerDidIt.Game.Scenarios.ContentRating.Family,
                Document = "{}", CreatedAt = now, UpdatedAt = now,
            });
            db.PlayRecords.AddRange(Record(GameKind.EscapeRoom, roomId, now, escaped: true), Record(GameKind.EscapeRoom, roomId, now.AddDays(-45), escaped: false));
            db.PlayFeedback.AddRange(Vote(GameKind.EscapeRoom, roomId, 4, FeedbackDifficulty.JustRight, now), Vote(GameKind.EscapeRoom, roomId, 3, FeedbackDifficulty.TooEasy, now));
            return Task.CompletedTask;
        });

        var attic = (await GamesAsync()).Single(g => g.Id == roomId);
        Assert.Equal(ContentOrigin.Host, attic.Origin);
        Assert.Equal("The Host", attic.Owner);
        Assert.Equal(ButlerDidIt.Game.Scenarios.ContentRating.Family, attic.Shelf);
        Assert.Equal(2, attic.Plays);
        Assert.Equal(1, attic.RecentPlays);
        Assert.Equal(0.5, attic.SolveRate);
        Assert.Equal(3.5, attic.Rating);
        Assert.Equal(2, attic.Ratings);
        Assert.Equal((1, 1, 0), (attic.TooEasy, attic.JustRight, attic.TooHard));
        Assert.Equal(now, attic.LastPlayed!.Value, TimeSpan.FromSeconds(1));
        Assert.False(attic.Hidden);
    }

    [Fact]
    public async Task A_hidden_room_says_so_and_an_unplayed_game_has_no_numbers()
    {
        await app.AdminAsync();
        await WithDbAsync(db =>
        {
            db.HiddenContent.Add(new HiddenContentEntity { Kind = GameKind.EscapeRoom, ContentId = "the-funhouse", HiddenAt = DateTimeOffset.UtcNow });
            return Task.CompletedTask;
        });
        var funhouse = (await GamesAsync()).Single(g => g.Id == "the-funhouse");
        Assert.True(funhouse.Hidden);

        var quiet = (await GamesAsync()).First(g => g.Plays == 0 && g.Ratings == 0);
        Assert.Null(quiet.SolveRate);
        Assert.Null(quiet.Rating);
        Assert.Null(quiet.LastPlayed);
    }

    [Fact]
    public async Task The_hosts_list_says_when_each_host_joined_and_last_hosted()
    {
        var (host, id) = await HostAsync();
        var admin = await app.AdminAsync();
        var listed = (await Read<List<HostView>>(await admin.GetAsync("/api/admin/hosts"))).Single(h => h.Id == id);
        Assert.Equal(DateTimeOffset.UtcNow, listed.Joined!.Value, TimeSpan.FromMinutes(1));
        Assert.Null(listed.LastParty);

        await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties", new CreatePartyRequest(Blackwood, PartyMode.SharedScreen, null), GameJson.Options));
        listed = (await Read<List<HostView>>(await admin.GetAsync("/api/admin/hosts"))).Single(h => h.Id == id);
        Assert.Equal(DateTimeOffset.UtcNow, listed.LastParty!.Value, TimeSpan.FromMinutes(1));
    }
}

/// <summary>
/// The sign-up switch: the admin's choice overrides the server's configuration until they hand it back. Each class
/// has its own server, because the switch is one setting for the whole site.
/// </summary>
public class SignUpSwitchTests(AdminHubFactory app) : IClassFixture<AdminHubFactory>
{
    private HttpClient Anonymous() => app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private Task<HttpResponseMessage> SignUpAsync() =>
        Anonymous().PostAsJsonAsync("/api/auth/register", new RegisterRequest($"new{Guid.NewGuid():N}@example.com", "password123", "New Host"));

    private async Task<SignUpsView> SwitchAsync(bool? open)
    {
        var res = await (await app.AdminAsync()).PutAsJsonAsync("/api/admin/signups", new SignUpsRequest(open), GameJson.Options);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return GameJson.Deserialize<SignUpsView>(await res.Content.ReadAsStringAsync());
    }

    private async Task<bool> PageOffersSignUpAsync() =>
        GameJson.Deserialize<AuthOptionsView>(await Anonymous().GetStringAsync("/api/auth/options")).AllowRegistration;

    [Fact]
    public async Task The_admin_closes_sign_ups_to_invites_and_hands_the_choice_back_to_the_configuration()
    {
        var admin = await app.AdminAsync();
        var start = GameJson.Deserialize<SignUpsView>(await admin.GetStringAsync("/api/admin/signups"));
        Assert.True(start is { Open: true, ServerSetting: true, Switch: null });
        Assert.True((await SignUpAsync()).IsSuccessStatusCode);

        var closed = await SwitchAsync(false);
        Assert.True(closed is { Open: false, ServerSetting: true, Switch: false });
        Assert.False(await PageOffersSignUpAsync());
        var refused = await SignUpAsync();
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("need an invite", await refused.Content.ReadAsStringAsync());

        // An invite still works while sign-ups are closed: that's what invites are for.
        var invite = GameJson.Deserialize<CreatedInvite>(await (await admin.PostAsJsonAsync("/api/admin/invites", new CreateInviteRequest(null, "A friend"), GameJson.Options)).Content.ReadAsStringAsync());
        Assert.Equal(1, (await SwitchAsync(false)).OpenInvites);
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(invite.Link).Query)["invite"];
        Assert.True((await Anonymous().PostAsJsonAsync("/api/auth/register", new RegisterRequest($"friend{Guid.NewGuid():N}@example.com", "password123", "Friend", token))).IsSuccessStatusCode);

        // Handing it back: the configuration (open) decides again.
        var back = await SwitchAsync(null);
        Assert.True(back is { Open: true, Switch: null, OpenInvites: 0 });
        Assert.True(await PageOffersSignUpAsync());
        Assert.True((await SignUpAsync()).IsSuccessStatusCode);
    }
}

public class ClosedSignUpSwitchTests(ClosedAdminHubFactory app) : IClassFixture<ClosedAdminHubFactory>
{
    [Fact]
    public async Task The_admin_opens_sign_ups_on_an_invite_only_server()
    {
        var admin = await app.AdminAsync();
        var anonymous = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Task<HttpResponseMessage> SignUp() => anonymous.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"new{Guid.NewGuid():N}@example.com", "password123", "New Host"));

        Assert.Equal(HttpStatusCode.Forbidden, (await SignUp()).StatusCode);
        var open = GameJson.Deserialize<SignUpsView>(await (await admin.PutAsJsonAsync("/api/admin/signups", new SignUpsRequest(true), GameJson.Options)).Content.ReadAsStringAsync());
        Assert.True(open is { Open: true, ServerSetting: false, Switch: true });
        Assert.True((await SignUp()).IsSuccessStatusCode);
    }
}
