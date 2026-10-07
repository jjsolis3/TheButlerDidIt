using ButlerDidIt.Api.Billing;
using ButlerDidIt.Api.Data;

namespace ButlerDidIt.Api.Tests;

/// <summary>How a subscription at the payment provider becomes access (#101): pure, no provider, no database.</summary>
public class PaidAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = Now.AddMonths(-3);

    private static PaidSubscription Sub(string status, DateTimeOffset? periodStart = null, DateTimeOffset? periodEnd = null,
        DateTimeOffset? cancelAt = null, DateTimeOffset? endedAt = null) =>
        new("sub_1", "cus_1", status, [new PaidItem("price_1", "prod_1")], Started,
            periodStart ?? Now.AddDays(-10), periodEnd ?? Now.AddDays(20), cancelAt, endedAt);

    [Fact]
    public void An_active_subscription_counts_a_little_past_its_renewal_and_says_when_it_renews()
    {
        var terms = PaidAccess.ForSubscription(Sub("active"), graceDays: 7, Now);
        Assert.Equal(PaidStatus.Active, terms.Status);
        Assert.Equal(Now.AddDays(20), terms.RenewsAt);
        Assert.Equal(Now.AddDays(20) + PaidAccess.RenewalLeeway, terms.EndsAt); // the renewal's webhook can take a while
        Assert.Equal(Started, terms.StartsAt);
        Assert.True(terms.GivesAccess(Now));
    }

    [Fact]
    public void A_subscription_in_its_free_days_renews_when_they_end()
    {
        var terms = PaidAccess.ForSubscription(Sub("trialing", periodEnd: Now.AddDays(5)), 7, Now);
        Assert.Equal((PaidStatus.Trialing, Now.AddDays(5)), (terms.Status, terms.RenewsAt));
    }

    [Fact]
    public void Cancelling_at_the_end_of_the_period_keeps_the_games_until_then_and_not_a_moment_longer()
    {
        var terms = PaidAccess.ForSubscription(Sub("active", cancelAt: Now.AddDays(20)), 7, Now);
        Assert.Equal((PaidStatus.Ending, Now.AddDays(20), (DateTimeOffset?)null), (terms.Status, terms.EndsAt, terms.RenewsAt));
        Assert.True(terms.GivesAccess(Now.AddDays(19)));
        Assert.False(terms.GivesAccess(Now.AddDays(20).AddSeconds(1)));
    }

    [Fact]
    public void A_failed_renewal_keeps_the_games_for_the_grace_days_from_when_it_was_due()
    {
        // Stripe has already started the new period, unpaid: the grace counts from its start, not its end a month away.
        var due = Now.AddDays(-2);
        var terms = PaidAccess.ForSubscription(Sub("past_due", periodStart: due, periodEnd: due.AddMonths(1)), graceDays: 7, Now);
        Assert.Equal((PaidStatus.PastDue, due.AddDays(7), (DateTimeOffset?)null), (terms.Status, terms.EndsAt, terms.RenewsAt));
        Assert.True(terms.GivesAccess(Now));
        Assert.False(terms.GivesAccess(due.AddDays(7).AddSeconds(1)));
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("unpaid")]
    [InlineData("incomplete")]
    [InlineData("incomplete_expired")]
    [InlineData("paused")]
    public void Everything_else_gives_no_games(string status)
    {
        var terms = PaidAccess.ForSubscription(Sub(status, endedAt: status == "canceled" ? Now.AddDays(-1) : null), 7, Now);
        Assert.Equal(PaidStatus.Ended, terms.Status);
        Assert.False(terms.GivesAccess(Now));
        Assert.Equal(status == "canceled" ? Now.AddDays(-1) : Now, terms.EndsAt); // a cancelled one ended when it did
    }

    [Fact]
    public void A_live_subscription_is_one_the_host_would_change_rather_than_buy_again()
    {
        AccessGrantEntity Grant(PaidStatus status, int endsInDays = 10) => new()
        {
            UserId = "u", Kind = GrantKind.Subscription, Status = status, EndsAt = Now.AddDays(endsInDays),
        };
        Assert.True(PaidAccess.IsLiveSubscription(Grant(PaidStatus.Active), Now));
        Assert.True(PaidAccess.IsLiveSubscription(Grant(PaidStatus.PastDue, endsInDays: -1), Now)); // the card is still being retried
        Assert.True(PaidAccess.IsLiveSubscription(Grant(PaidStatus.Ending), Now));
        Assert.False(PaidAccess.IsLiveSubscription(Grant(PaidStatus.Ending, endsInDays: -1), Now));
        Assert.False(PaidAccess.IsLiveSubscription(Grant(PaidStatus.Ended), Now));
        Assert.False(PaidAccess.IsLiveSubscription(new AccessGrantEntity { UserId = "u", Kind = GrantKind.Pass, Status = PaidStatus.Active }, Now));
    }
}

/// <summary>What the admin is told about a plan's price (#101).</summary>
public class PlanPriceTests
{
    private static PriceInfo Price(string? interval, long count = 1, bool live = false, bool active = true, long? amount = 1200) =>
        new("price_1", "prod_1", amount, "usd", interval, count, active, live);

    [Fact]
    public void A_price_that_fits_its_plan_is_fine()
    {
        Assert.Null(BillingCatalog.Problem(BillingPlans.Get(BillingPlan.BothMonthly), Price("month"), liveMode: false));
        Assert.Null(BillingCatalog.Problem(BillingPlans.Get(BillingPlan.BothYearly), Price("year"), liveMode: false));
        Assert.Null(BillingCatalog.Problem(BillingPlans.Get(BillingPlan.BothPass), Price(null), liveMode: false));
    }

    [Fact]
    public void Every_kind_of_mistake_is_named()
    {
        var monthly = BillingPlans.Get(BillingPlan.MysteriesMonthly);
        Assert.Contains("no price with this id", BillingCatalog.Problem(monthly, null, false));
        Assert.Contains("test-mode price, but the secret key is a live one", BillingCatalog.Problem(monthly, Price("month"), liveMode: true));
        Assert.Contains("archived", BillingCatalog.Problem(monthly, Price("month", active: false), false));
        Assert.Contains("no fixed amount", BillingCatalog.Problem(monthly, Price("month", amount: null), false));
        Assert.Contains("one-time price, but a subscription repeats", BillingCatalog.Problem(monthly, Price(null), false));
        Assert.Contains("renews every year, but this plan renews every month", BillingCatalog.Problem(monthly, Price("year"), false));
        Assert.Contains("renews every 3 months", BillingCatalog.Problem(monthly, Price("month", count: 3), false));
        Assert.Contains("a party pass is paid once", BillingCatalog.Problem(BillingPlans.Get(BillingPlan.EscapeRoomsPass), Price("month"), false));
    }

    [Fact]
    public void Only_plans_with_a_price_are_sold()
    {
        var prices = new BillingPrices { BothMonthly = " price_both ", EscapeRoomsPass = "price_pass", MysteriesYearly = "" };
        Assert.Equal([(BillingPlan.BothMonthly, "price_both"), (BillingPlan.EscapeRoomsPass, "price_pass")],
            BillingPlans.Sold(prices).Select(x => (x.Plan.Id, x.PriceId)));
    }
}
