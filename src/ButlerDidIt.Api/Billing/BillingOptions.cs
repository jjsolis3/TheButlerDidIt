using ButlerDidIt.Api.Data;

namespace ButlerDidIt.Api.Billing;

/// <summary>
/// Payments (#101), from the <c>Billing</c> section of the settings. Payments are off until a payment provider is set
/// up, and the site works exactly as before: free trials and free access from the admin.
/// </summary>
public sealed class BillingOptions
{
    /// <summary>
    /// <c>Stripe</c>, or <c>Fake</c> for tests and for trying the pages out (refused in Production: anyone could "pay").
    /// Empty means Stripe when <see cref="StripeSettings.SecretKey"/> is set, and no payments otherwise.
    /// </summary>
    public string? Provider { get; set; }

    public StripeSettings Stripe { get; set; } = new();

    /// <summary>The provider's price for each plan. A plan without one isn't offered.</summary>
    public BillingPrices Prices { get; set; } = new();

    /// <summary>How long a party pass lasts, from the moment it's paid for.</summary>
    public int PassHours { get; set; } = 72;

    /// <summary>
    /// How long a host keeps their games after a renewal payment fails, while the provider retries the card. Counted
    /// from the day the renewal was due.
    /// </summary>
    public int GraceDays { get; set; } = 7;

    /// <summary>Let Stripe Tax add sales tax or VAT at checkout. Turn on only once Stripe Tax is set up in the Stripe dashboard.</summary>
    public bool AutomaticTax { get; set; }
}

public sealed class StripeSettings
{
    /// <summary>The secret key (<c>sk_test_…</c> or <c>sk_live_…</c>), or a restricted key (<c>rk_…</c>).</summary>
    public string? SecretKey { get; set; }

    /// <summary>The webhook endpoint's signing secret (<c>whsec_…</c>), which proves a webhook came from Stripe.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Another address for Stripe's API. Only tests set it, to talk to stripe-mock.</summary>
    public string? ApiBase { get; set; }
}

/// <summary>
/// The price id (<c>price_…</c>, from the Stripe dashboard) of each plan the site sells. They're named one by one
/// rather than listed, so each is one plain setting (<c>Billing__Prices__BothMonthly</c>) and a typo in a plan's
/// name is a start-up error rather than a plan that quietly isn't sold.
/// </summary>
public sealed class BillingPrices
{
    public string? MysteriesMonthly { get; set; }
    public string? MysteriesYearly { get; set; }
    public string? EscapeRoomsMonthly { get; set; }
    public string? EscapeRoomsYearly { get; set; }
    public string? BothMonthly { get; set; }
    public string? BothYearly { get; set; }

    /// <summary>One-time prices: a party pass of one game, or both.</summary>
    public string? MysteriesPass { get; set; }
    public string? EscapeRoomsPass { get; set; }
    public string? BothPass { get; set; }
}

/// <summary>Every plan the site can sell. Which ones it does sell depends on <see cref="BillingPrices"/>.</summary>
public enum BillingPlan
{
    MysteriesMonthly,
    MysteriesYearly,
    EscapeRoomsMonthly,
    EscapeRoomsYearly,
    BothMonthly,
    BothYearly,
    MysteriesPass,
    EscapeRoomsPass,
    BothPass,
}

public enum PlanKind
{
    /// <summary>Paid every month or year until cancelled.</summary>
    Subscription,

    /// <summary>Paid once, for <see cref="BillingOptions.PassHours"/>.</summary>
    Pass,
}

public enum PlanInterval
{
    Month,
    Year,
}

/// <summary>What a plan gives: which games, and how it's paid for.</summary>
public sealed record PlanDefinition(BillingPlan Id, GameAccess Games, PlanKind Kind, PlanInterval? Interval);

public static class BillingPlans
{
    public static readonly IReadOnlyList<PlanDefinition> All =
    [
        new(BillingPlan.MysteriesMonthly, GameAccess.Mysteries, PlanKind.Subscription, PlanInterval.Month),
        new(BillingPlan.MysteriesYearly, GameAccess.Mysteries, PlanKind.Subscription, PlanInterval.Year),
        new(BillingPlan.EscapeRoomsMonthly, GameAccess.EscapeRooms, PlanKind.Subscription, PlanInterval.Month),
        new(BillingPlan.EscapeRoomsYearly, GameAccess.EscapeRooms, PlanKind.Subscription, PlanInterval.Year),
        new(BillingPlan.BothMonthly, GameAccess.Both, PlanKind.Subscription, PlanInterval.Month),
        new(BillingPlan.BothYearly, GameAccess.Both, PlanKind.Subscription, PlanInterval.Year),
        new(BillingPlan.MysteriesPass, GameAccess.Mysteries, PlanKind.Pass, null),
        new(BillingPlan.EscapeRoomsPass, GameAccess.EscapeRooms, PlanKind.Pass, null),
        new(BillingPlan.BothPass, GameAccess.Both, PlanKind.Pass, null),
    ];

    public static PlanDefinition Get(BillingPlan plan) => All.Single(p => p.Id == plan);

    /// <summary>The price set up for <paramref name="plan"/>, or null when it isn't sold.</summary>
    public static string? PriceId(BillingPrices prices, BillingPlan plan)
    {
        var id = plan switch
        {
            BillingPlan.MysteriesMonthly => prices.MysteriesMonthly,
            BillingPlan.MysteriesYearly => prices.MysteriesYearly,
            BillingPlan.EscapeRoomsMonthly => prices.EscapeRoomsMonthly,
            BillingPlan.EscapeRoomsYearly => prices.EscapeRoomsYearly,
            BillingPlan.BothMonthly => prices.BothMonthly,
            BillingPlan.BothYearly => prices.BothYearly,
            BillingPlan.MysteriesPass => prices.MysteriesPass,
            BillingPlan.EscapeRoomsPass => prices.EscapeRoomsPass,
            BillingPlan.BothPass => prices.BothPass,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }

    /// <summary>The plans that are sold, with their prices.</summary>
    public static IEnumerable<(PlanDefinition Plan, string PriceId)> Sold(BillingPrices prices) =>
        All.Select(p => (Plan: p, PriceId: PriceId(prices, p.Id))).Where(x => x.PriceId is not null).Select(x => (x.Plan, x.PriceId!));
}
