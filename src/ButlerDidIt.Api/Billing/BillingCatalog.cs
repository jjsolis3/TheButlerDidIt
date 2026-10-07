using System.Collections.Concurrent;
using ButlerDidIt.Api.Data;

namespace ButlerDidIt.Api.Billing;

/// <summary>A plan that's set up, with its price as the provider has it and anything wrong with it.</summary>
/// <param name="Price">Null when the provider has no such price, or couldn't be asked.</param>
/// <param name="Problem">Why it can't be sold as it is, for the admin; null when it's fine.</param>
public sealed record PlanOffer(PlanDefinition Plan, string PriceId, PriceInfo? Price, string? Problem)
{
    /// <summary>Offered to hosts: only plans that would check out and give what they say.</summary>
    public bool ForSale => Price is not null && Problem is null;
}

/// <summary>
/// The plans that are set up, each with its price read from the payment provider: the amount and how often it's paid
/// come from Stripe, so changing a price there needs no change here. Kept for ten minutes, as prices rarely change
/// and each page view shouldn't cost a call to Stripe.
/// </summary>
public sealed class BillingCatalog(BillingSetup setup, TimeProvider clock, ILogger<BillingCatalog> log)
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    /// <summary>A failed lookup is kept only a minute, so a mistake fixed in the dashboard shows soon.</summary>
    private static readonly TimeSpan KeepFailureFor = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (PriceInfo? Price, string? Error, DateTimeOffset Until)> _prices = new();

    public async Task<IReadOnlyList<PlanOffer>> OffersAsync(CancellationToken ct)
    {
        if (setup.Provider is not { } provider) return [];
        var offers = new List<PlanOffer>();
        foreach (var (plan, priceId) in BillingPlans.Sold(setup.Options.Prices))
        {
            var (price, error) = await PriceAsync(provider, priceId, ct);
            offers.Add(new PlanOffer(plan, priceId, price, error ?? Problem(plan, price, provider.LiveMode)));
        }
        return offers;
    }

    /// <summary>Read every price again on the next look, e.g. when the admin has just fixed one.</summary>
    public void Forget() => _prices.Clear();

    /// <summary>
    /// The games that the things paid for give. A price that's one of the plans gives that plan's games. One that
    /// isn't, such as last year's price of a plan, gives the games of the plan whose product it belongs to (each product
    /// in the dashboard is one plan's games), so subscribers on an old price keep their games when the price changes.
    /// Null when nothing matches.
    /// </summary>
    public async Task<GameAccess?> GamesForAsync(IEnumerable<PaidItem> items, PlanKind kind, CancellationToken ct)
    {
        var offers = (await OffersAsync(ct)).Where(o => o.Plan.Kind == kind).ToList();
        GameAccess? games = null;
        foreach (var item in items)
        {
            var plan = offers.FirstOrDefault(o => o.PriceId == item.PriceId)?.Plan
                ?? offers.FirstOrDefault(o => item.ProductId is not null && o.Price?.ProductId == item.ProductId)?.Plan;
            if (plan is not null) games = (games ?? GameAccess.None) | plan.Games;
        }
        return games;
    }

    private async Task<(PriceInfo? Price, string? Error)> PriceAsync(IBillingProvider provider, string priceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_prices.TryGetValue(priceId, out var kept) && kept.Until > now) return (kept.Price, kept.Error);
        try
        {
            var price = await provider.PriceAsync(priceId, ct);
            _prices[priceId] = (price, null, now + KeepFor);
            return (price, null);
        }
        catch (BillingException ex)
        {
            log.LogWarning(ex, "Couldn't read the price {PriceId}", priceId);
            _prices[priceId] = (null, ex.Message, now + KeepFailureFor);
            return (null, ex.Message);
        }
    }

    /// <summary>What's wrong with a plan's price, in words the admin can act on; null when nothing is.</summary>
    public static string? Problem(PlanDefinition plan, PriceInfo? price, bool liveMode)
    {
        if (price is null) return "Stripe has no price with this id. Check it was copied whole, and from the same mode (test or live) as the secret key.";
        if (price.LiveMode != liveMode)
            return $"This is a {(price.LiveMode ? "live" : "test")}-mode price, but the secret key is a {(liveMode ? "live" : "test")} one.";
        if (!price.Active) return "This price is archived in Stripe, so it can't be sold.";
        if (price.Amount is null) return "This price has no fixed amount (tiered or pay-what-you-want), which the plans can't show.";
        if (plan.Kind == PlanKind.Pass)
            return price.Interval is null ? null : "This price repeats, but a party pass is paid once. Use a one-time price.";
        if (price.Interval is null) return "This is a one-time price, but a subscription repeats. Use a recurring price.";
        var expected = plan.Interval == PlanInterval.Year ? "year" : "month";
        return price.Interval == expected && price.IntervalCount == 1
            ? null
            : $"This price renews every {(price.IntervalCount == 1 ? "" : $"{price.IntervalCount} ")}{price.Interval}{(price.IntervalCount == 1 ? "" : "s")}, but this plan renews every {expected}.";
    }
}
