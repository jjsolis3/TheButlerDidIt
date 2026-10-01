using System.Net;
using System.Net.Http.Json;
using System.Web;
using ButlerDidIt.Ai.Generation;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Plans;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ButlerDidIt.Api.Tests;

/// <summary>Putting grants together: pure, no database.</summary>
public class AccessRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AccessGrantEntity Grant(GrantKind kind, GameAccess games, int startsInDays = -1, int? endsInDays = null, bool revoked = false) => new()
    {
        UserId = "u", Kind = kind, Games = games, StartsAt = Now.AddDays(startsInDays),
        EndsAt = endsInDays is { } d ? Now.AddDays(d) : null, RevokedAt = revoked ? Now.AddDays(-1) : null,
    };

    [Fact]
    public void Grants_in_effect_add_up_and_the_most_lasting_one_names_the_plan()
    {
        var access = Access.From(false, [Grant(GrantKind.Trial, GameAccess.Both, endsInDays: 3), Grant(GrantKind.Comp, GameAccess.Mysteries)], Now);
        Assert.Equal((true, true, AccessPlan.Free, (DateTimeOffset?)null), (access.Mysteries, access.EscapeRooms, access.Plan, access.EndsAt));

        // Two passes, one game each, make both games.
        var passes = Access.From(false, [Grant(GrantKind.Pass, GameAccess.Mysteries, endsInDays: 1), Grant(GrantKind.Pass, GameAccess.EscapeRooms, endsInDays: 2)], Now);
        Assert.Equal((true, true, AccessPlan.Pass), (passes.Mysteries, passes.EscapeRooms, passes.Plan));
        Assert.Equal(Now.AddDays(2), passes.EndsAt); // the one that lasts longer
    }

    [Fact]
    public void Ended_revoked_and_future_grants_do_not_count()
    {
        Assert.Equal(AccessPlan.TrialEnded, Access.From(false, [Grant(GrantKind.Trial, GameAccess.Both, startsInDays: -15, endsInDays: -1)], Now).Plan);
        Assert.Equal(AccessPlan.None, Access.From(false, [Grant(GrantKind.Comp, GameAccess.Both, revoked: true)], Now).Plan);
        var notYet = Access.From(false, [Grant(GrantKind.Pass, GameAccess.Both, startsInDays: 1, endsInDays: 4)], Now);
        Assert.False(notYet.Mysteries || notYet.EscapeRooms);
        Assert.Equal(AccessPlan.None, Access.From(false, [], Now).Plan);
    }

    [Fact]
    public void The_admin_always_has_every_game()
    {
        var access = Access.From(true, [], Now);
        Assert.Equal((true, true, AccessPlan.Admin), (access.Mysteries, access.EscapeRooms, access.Plan));
        Assert.True(access.Allows(GameKind.EscapeRoom) && access.Allows(GameKind.Mystery));
    }
}

/// <summary>A server whose admin (the first account) is made once, first.</summary>
public sealed class PlansFactory : ApiFactory
{
    private readonly Lazy<Task<HttpClient>> _admin;

    public PlansFactory() => _admin = new(async () =>
    {
        var client = CreateClient(); // keeps its cookies
        (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("owner@example.com", "password123", "Owner"))).EnsureSuccessStatusCode();
        return client;
    });

    public Task<HttpClient> AdminAsync() => _admin.Value;
}

public class AccessTests(PlansFactory app) : IClassFixture<PlansFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<(HttpClient Client, string Id)> HostAsync(string? invite = null)
    {
        await app.AdminAsync();
        var client = app.CreateClient();
        var me = await Read<MeResponse>(await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"h{Guid.NewGuid():N}@example.com", "password123", "Hana", invite)));
        return (client, me.Id);
    }

    private static Task<MeResponse> MeAsync(HttpClient client) => client.GetAsync("/api/auth/me").ContinueWith(t => Read<MeResponse>(t.Result)).Unwrap();

    private static Task<HttpResponseMessage> StartMysteryAsync(HttpClient host) => host.PostAsJsonAsync("/api/parties",
        new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null, UseAi: false), GameJson.Options);

    private static Task<HttpResponseMessage> StartEscapeAsync(HttpClient host) => host.PostAsJsonAsync("/api/parties/escape",
        new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, null, PuzzleChoice.Fresh, null), GameJson.Options);

    private async Task EndTrialAsync(string userId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AccessGrants.Where(g => g.UserId == userId && g.Kind == GrantKind.Trial)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.EndsAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task A_new_host_gets_a_free_trial_of_both_games()
    {
        var (host, _) = await HostAsync();
        var access = (await MeAsync(host)).Access;
        Assert.Equal((true, true, AccessPlan.Trial), (access.Mysteries, access.EscapeRooms, access.Plan));
        Assert.InRange(access.EndsAt!.Value, DateTimeOffset.UtcNow.AddDays(14).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(14).AddMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await StartMysteryAsync(host)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartEscapeAsync(host)).StatusCode);
    }

    [Fact]
    public async Task After_the_trial_nothing_new_starts_but_a_party_already_made_keeps_going()
    {
        var (host, id) = await HostAsync();
        var party = await Read<PartyInfo>(await StartMysteryAsync(host));
        await EndTrialAsync(id);

        Assert.Equal(AccessPlan.TrialEnded, (await MeAsync(host)).Access.Plan);
        var refused = await StartMysteryAsync(host);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Your free trial has ended", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await StartEscapeAsync(host)).StatusCode);
        // AI writing is starting something too. (The check comes before the "no AI set up" one.)
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync("/api/generation",
            new GenerateRequest("speakeasy", 4, ContentRating.Family, MysteryLength.Short, null), GameJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync("/api/escape-rooms/generate",
            new EscapeRoomGenerateRequest("a lighthouse", ContentRating.Family, 30), GameJson.Options)).StatusCode);

        // The party made during the trial still works: guests join, the host can open it.
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/parties/{party.Code}")).StatusCode);
    }

    [Fact]
    public async Task The_admin_gives_free_access_and_can_take_it_back()
    {
        var admin = await app.AdminAsync();
        var (host, id) = await HostAsync();
        await EndTrialAsync(id);

        var given = await Read<AccessView>(await admin.PostAsync($"/api/admin/hosts/{id}/free-access", null));
        Assert.Equal((AccessPlan.Free, true, true), (given.Plan, given.Mysteries, given.EscapeRooms));
        Assert.Equal(HttpStatusCode.OK, (await StartEscapeAsync(host)).StatusCode);
        var listed = (await Read<List<HostView>>(await admin.GetAsync("/api/admin/hosts"))).Single(h => h.Id == id);
        Assert.Equal(AccessPlan.Free, listed.Access.Plan);

        var taken = await Read<AccessView>(await admin.DeleteAsync($"/api/admin/hosts/{id}/free-access"));
        Assert.Equal(AccessPlan.TrialEnded, taken.Plan);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartEscapeAsync(host)).StatusCode);

        // Only the admin can do this.
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsync($"/api/admin/hosts/{id}/free-access", null)).StatusCode);
    }

    [Fact]
    public async Task A_party_pass_covers_its_one_game_until_it_ends()
    {
        var (host, id) = await HostAsync();
        await EndTrialAsync(id);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.AccessGrants.Add(new AccessGrantEntity { Id = Guid.NewGuid(), UserId = id, Games = GameAccess.EscapeRooms, Kind = GrantKind.Pass, StartsAt = now, EndsAt = now.AddHours(72), CreatedAt = now });
            await db.SaveChangesAsync();
        }

        Assert.Equal(AccessPlan.Pass, (await MeAsync(host)).Access.Plan);
        Assert.Equal(HttpStatusCode.OK, (await StartEscapeAsync(host)).StatusCode);
        var mystery = await StartMysteryAsync(host);
        Assert.Equal(HttpStatusCode.Forbidden, mystery.StatusCode);
        Assert.Contains("doesn't include murder mysteries", await mystery.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_invite_can_give_free_access_instead_of_the_trial()
    {
        var admin = await app.AdminAsync();
        async Task<string> InviteAsync(bool free) => HttpUtility.ParseQueryString(new Uri((await Read<CreatedInvite>(await admin.PostAsJsonAsync(
            "/api/admin/invites", new CreateInviteRequest(null, null, FreeAccess: free), GameJson.Options))).Link).Query)["invite"]!;

        var (family, _) = await HostAsync(await InviteAsync(free: true));
        var access = (await MeAsync(family)).Access;
        Assert.Equal((AccessPlan.Free, (DateTimeOffset?)null), (access.Plan, access.EndsAt));

        var (stranger, _) = await HostAsync(await InviteAsync(free: false));
        Assert.Equal(AccessPlan.Trial, (await MeAsync(stranger)).Access.Plan);
    }

    [Fact]
    public async Task The_admin_is_never_locked_out()
    {
        var admin = await app.AdminAsync();
        Assert.Equal(AccessPlan.Admin, (await MeAsync(admin)).Access.Plan);
        Assert.Equal(HttpStatusCode.OK, (await StartMysteryAsync(admin)).StatusCode);
    }
}

/// <summary>The migration that brought plans in gives everyone who already had an account free access.</summary>
public class AccessMigrationTests
{
    [Fact]
    public async Task Hosts_who_had_accounts_before_plans_keep_both_games_for_good()
    {
        var database = $"butler_test_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(ApiFactory.ServerConnection) { Database = database }.ConnectionString;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261001044728_Invites"); // the last migration before plans
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AspNetUsers" ("Id", "DisplayName", "IsAdmin", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ('old-host', 'Old Host', false, false, false, false, true, 0)
                """);

            await migrator.MigrateAsync(); // up to today

            var grant = await db.AccessGrants.SingleAsync(g => g.UserId == "old-host");
            Assert.Equal((GrantKind.Comp, GameAccess.Both, (DateTimeOffset?)null, (DateTimeOffset?)null), (grant.Kind, grant.Games, grant.EndsAt, grant.RevokedAt));
            Assert.Equal(AccessPlan.Free, Access.From(false, [grant], DateTimeOffset.UtcNow).Plan);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
