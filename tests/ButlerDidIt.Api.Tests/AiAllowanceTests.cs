using ButlerDidIt.Ai;
using ButlerDidIt.Api.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Plans;
using ButlerDidIt.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ButlerDidIt.Api.Tests;

/// <summary>Each plan's own monthly AI allowance (#147), worked out without a database.</summary>
public class AiAllowanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlanAiBudgets Plans = new() { Trial = 2, Pass = 5, Subscription = 20 };

    private static AccessGrantEntity Grant(GrantKind kind, DateTimeOffset? ends = null) =>
        new() { UserId = "host", Kind = kind, Games = GameAccess.Both, StartsAt = Now.AddDays(-30), EndsAt = ends };

    [Fact]
    public void Each_plan_has_its_own_allowance_and_the_rest_have_the_sites()
    {
        Assert.Equal(2m, AiAllowance.For(false, [Grant(GrantKind.Trial)], Now, Plans, 25));
        Assert.Equal(5m, AiAllowance.For(false, [Grant(GrantKind.Pass)], Now, Plans, 25));
        Assert.Equal(20m, AiAllowance.For(false, [Grant(GrantKind.Subscription)], Now, Plans, 25));
        // Free access from the admin, the admin, and a host who never had a plan: the site's budget.
        Assert.Equal(25m, AiAllowance.For(false, [Grant(GrantKind.Comp)], Now, Plans, 25));
        Assert.Equal(25m, AiAllowance.For(true, [], Now, Plans, 25));
        Assert.Equal(25m, AiAllowance.For(false, [], Now, Plans, 25));
        // A trial that's over (its party may still be going) keeps the trial's allowance, never the site's bigger one.
        Assert.Equal(2m, AiAllowance.For(false, [Grant(GrantKind.Trial, ends: Now.AddDays(-1))], Now, Plans, 25));
        Assert.Equal(5m, AiAllowance.For(false, [Grant(GrantKind.Trial, ends: Now.AddDays(-9)), Grant(GrantKind.Pass, ends: Now.AddDays(-1))], Now, Plans, 25));
    }

    [Fact]
    public void A_plan_left_unset_uses_the_sites_and_the_most_generous_grant_counts()
    {
        Assert.Equal(25m, AiAllowance.For(false, [Grant(GrantKind.Pass)], Now, new PlanAiBudgets { Trial = 2 }, 25));
        // A pass bought during the trial, or on top of a subscription: never less for having bought more.
        Assert.Equal(5m, AiAllowance.For(false, [Grant(GrantKind.Trial), Grant(GrantKind.Pass)], Now, Plans, 25));
        Assert.Equal(20m, AiAllowance.For(false, [Grant(GrantKind.Pass), Grant(GrantKind.Subscription)], Now, Plans, 25));
        // 0 is "no limit", which beats any amount.
        Assert.Equal(0m, AiAllowance.For(false, [Grant(GrantKind.Trial), Grant(GrantKind.Subscription)], Now, new PlanAiBudgets { Trial = 2, Subscription = 0 }, 25));
    }
}

/// <summary>A site where a free trial gets $1 of AI a month and everyone else the site's $25.</summary>
public sealed class PlanBudgetFactory : FakeAiFactory
{
    protected override string Budget => "25";

    // An empty one is how docker-compose passes a plan left blank: the site's budget.
    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
        [.. base.ExtraSettings, ("Plans:AiBudgetUsd:Trial", "1"), ("Plans:AiBudgetUsd:Subscription", "")];
}

public class PlanBudgetTests(PlanBudgetFactory app) : IClassFixture<PlanBudgetFactory>
{
    [Fact]
    public async Task A_trial_host_gets_the_trials_allowance_on_their_pages_and_in_the_check()
    {
        var (admin, _) = await app.RegisterHostAsync("owner@example.com"); // the first account is the admin
        var (host, _) = await app.RegisterHostAsync($"trial{Guid.NewGuid():N}@example.com");
        var me = GameJson.Deserialize<MeResponse>(await host.GetStringAsync("/api/auth/me"));

        Assert.Equal(1m, GameJson.Deserialize<AiStatus>(await host.GetStringAsync("/api/ai/status")).BudgetUsd);
        var account = JsonDocument.Parse(await host.GetStringAsync("/api/account")).RootElement;
        Assert.Equal(1m, account.GetProperty("usage").GetProperty("aiBudgetUsd").GetDecimal());

        // $1.50 spent: the trial's allowance is used up, though the site's $25 isn't.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AiUsage.Add(new AiUsageEntity
        {
            At = DateTimeOffset.UtcNow, Role = AiRole.Storyteller, ProviderName = "Fake", Model = "fake-model", CostUsd = 1.5m, PriceKnown = true,
            Success = true, HostUserId = me.Id, Purpose = "story",
        });
        await db.SaveChangesAsync();
        var budget = scope.ServiceProvider.GetRequiredService<IAiBudget>();
        var refused = await Assert.ThrowsAsync<AiBudgetExceededException>(() => budget.EnsureWithinBudgetAsync(me.Id, default));
        Assert.Contains("$1.00", refused.Message);

        // Free access from the admin lifts them to the site's budget.
        db.AccessGrants.Add(Access.FreeAccess(me.Id, DateTimeOffset.UtcNow, "Comp"));
        await db.SaveChangesAsync();
        await budget.EnsureWithinBudgetAsync(me.Id, default);
        Assert.Equal(25m, GameJson.Deserialize<AiStatus>(await host.GetStringAsync("/api/ai/status")).BudgetUsd);

        // The admin's AI page shows the plans' own allowances, and which use the site's.
        var plans = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/ai/usage")).RootElement.GetProperty("planBudgetsUsd");
        Assert.Equal(1m, plans.GetProperty("trial").GetDecimal());
        Assert.Equal(JsonValueKind.Null, plans.GetProperty("subscription").ValueKind);
    }
}
