using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Plans;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ButlerDidIt.Api.Billing;

public enum WebhookOutcome
{
    /// <summary>Acted on (or recorded, for an event that changes nothing here).</summary>
    Handled,

    /// <summary>Seen before: answered without doing anything again.</summary>
    Repeat,

    /// <summary>Payments are off on this server.</summary>
    NotSetUp,
}

/// <summary>
/// Payments (#101). Access only ever changes from what the payment provider says now, read fresh: never from the
/// browser coming back from the checkout page (anyone can visit that address) and never from what a webhook's body
/// says (webhooks can come late, twice, or out of order). A webhook, the host's return from checkout and the admin's
/// "Sync" button all do the same thing: read the customer from the provider and bring their grants in line.
/// </summary>
public sealed class BillingService(
    AppDbContext db,
    BillingSetup setup,
    BillingCatalog catalog,
    BillingLocks locks,
    TimeProvider clock,
    ILogger<BillingService> log)
{
    private IBillingProvider Provider => setup.Provider ?? throw new BillingException("Payments aren't set up on this server.");

    // ---------------------------------------------------------------- the host's billing page

    public async Task<BillingView> ViewAsync(AppUser user, CancellationToken ct)
    {
        if (!setup.Enabled || user.IsAdmin) return new BillingView(setup.Enabled, false, false, setup.Options.PassHours, []);
        var now = clock.GetUtcNow();
        var subscribed = (await db.AccessGrants.AsNoTracking().Where(g => g.UserId == user.Id && g.Kind == GrantKind.Subscription).ToListAsync(ct))
            .Any(g => PaidAccess.IsLiveSubscription(g, now));
        var plans = (await catalog.OffersAsync(ct)).Where(o => o.ForSale)
            .Select(o => new PlanOfferView(o.Plan.Id, o.Plan.Games.HasFlag(GameAccess.Mysteries), o.Plan.Games.HasFlag(GameAccess.EscapeRooms),
                o.Plan.Kind, o.Plan.Interval, o.Price!.Amount, o.Price.Currency))
            .ToList();
        return new BillingView(true, user.BillingCustomerId is not null, subscribed, setup.Options.PassHours, plans);
    }

    // ---------------------------------------------------------------- buying

    /// <summary>Starts a checkout for <paramref name="plan"/> and returns the provider's payment page to send the host to.</summary>
    /// <param name="siteUrl">The site's address, which the payment page sends the host back to.</param>
    public async Task<string> CheckoutAsync(AppUser user, BillingPlan plan, string siteUrl, CancellationToken ct)
    {
        var provider = Provider;
        if (user.IsAdmin) throw new BillingException("You run this site, so every game is already yours.");
        var offer = (await catalog.OffersAsync(ct)).FirstOrDefault(o => o.Plan.Id == plan && o.ForSale)
            ?? throw new BillingException("That plan isn't for sale here.");

        var customer = await CustomerAsync(user, ct);
        // Decide from the provider's latest, not from a webhook that may still be on its way.
        await SyncCustomerAsync(customer, ct);
        var now = clock.GetUtcNow();
        var grants = await db.AccessGrants.AsNoTracking().Where(g => g.UserId == user.Id).ToListAsync(ct);

        DateTimeOffset? trialEnd = null;
        if (offer.Plan.Kind == PlanKind.Subscription)
        {
            // One subscription per host: changing plan or cancelling is done on the provider's billing page, which
            // charges or credits the difference. A second subscription would charge twice for the same games.
            if (grants.FirstOrDefault(g => PaidAccess.IsLiveSubscription(g, now)) is { } live)
                throw new BillingException(live.Status == PaidStatus.PastDue
                    ? "Your last payment didn't go through. Update your card with Manage billing to keep your subscription."
                    : "You already have a subscription. Change or cancel it with Manage billing.");
            // Bought during the free trial: the first payment waits for the trial's end, so no free day is lost.
            // (Stripe wants a trial to last two days at least; a trial ending sooner simply isn't carried over.)
            var trial = grants.Where(g => g.Kind == GrantKind.Trial && Access.InEffect(g, now)).Max(g => g.EndsAt);
            if (trial > now.AddDays(2).AddMinutes(5)) trialEnd = trial;
        }
        else
        {
            var access = Access.From(false, grants, now);
            var adds = (offer.Plan.Games.HasFlag(GameAccess.Mysteries) && !access.Mysteries) || (offer.Plan.Games.HasFlag(GameAccess.EscapeRooms) && !access.EscapeRooms);
            if (!adds) throw new BillingException("You can already start those games, so a pass would add nothing. A pass starts the moment it's paid for.");
        }

        return await provider.CheckoutAsync(new CheckoutRequest(customer, user.Id, offer.PriceId, offer.Plan.Kind, trialEnd,
            // {CHECKOUT_SESSION_ID} is filled in by Stripe, so the page can ask about this very checkout.
            $"{siteUrl}/account?billing=done&session={{CHECKOUT_SESSION_ID}}",
            $"{siteUrl}/account?billing=cancelled",
            setup.Options.AutomaticTax), ct);
    }

    /// <summary>The provider's billing page for the host: card, plan, cancelling, invoices.</summary>
    public Task<string> PortalAsync(AppUser user, string siteUrl, CancellationToken ct) =>
        user.BillingCustomerId is { } customer
            ? Provider.PortalAsync(customer, $"{siteUrl}/account?billing=back", ct)
            : throw new BillingException("You haven't bought anything here yet, so there's no billing to manage.");

    /// <summary>
    /// The host is back from the payment page (or reopened their account page): bring their grants up to date. A
    /// checkout id from the address only counts if the provider says it's this host's and paid, so a copied or made-up
    /// one does nothing.
    /// </summary>
    /// <returns>Whether that checkout is paid; null when there's none, or it isn't theirs.</returns>
    public async Task<bool?> SyncForHostAsync(AppUser user, string? checkoutId, CancellationToken ct)
    {
        if (!setup.Enabled || user.BillingCustomerId is not { } customer) return null;
        var checkout = string.IsNullOrWhiteSpace(checkoutId) ? null : await CheckoutFinishedAsync(checkoutId, user.Id, ct);
        await SyncCustomerAsync(customer, ct);
        return checkout?.Paid;
    }

    /// <summary>
    /// Asks the provider about the host's subscription when what we have may be out of date: a renewal that's due but
    /// not confirmed, or a payment that failed. Called before the host is told a game isn't in their plan, and when
    /// they open their account page, so a webhook that never arrived can't keep a paying host out. Quiet on failure:
    /// what we have stands.
    /// </summary>
    public async Task RefreshIfDueAsync(AppUser user, CancellationToken ct)
    {
        if (!setup.Enabled || user.BillingCustomerId is null) return;
        var now = clock.GetUtcNow();
        var due = await db.AccessGrants.AsNoTracking().AnyAsync(g => g.UserId == user.Id && g.Kind == GrantKind.Subscription
            && (((g.Status == PaidStatus.Active || g.Status == PaidStatus.Trialing) && g.RenewsAt < now) || g.Status == PaidStatus.PastDue), ct);
        if (!due) return;
        try
        {
            await SyncForHostAsync(user, null, ct);
        }
        catch (BillingException ex)
        {
            log.LogWarning(ex, "Payments: couldn't check {UserId}'s subscription with the provider", user.Id);
        }
    }

    /// <summary>A host's customer at the provider, made the first time they pay.</summary>
    private async Task<string> CustomerAsync(AppUser user, CancellationToken ct)
    {
        if (user.BillingCustomerId is { } known) return known;
        // Two clicks of "Choose" at once must not make two customers.
        await using var _ = await locks.AcquireAsync($"user:{user.Id}", ct);
        var current = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.BillingCustomerId).FirstAsync(ct);
        if (current is null)
        {
            current = await Provider.CreateCustomerAsync(user.Id, user.Email ?? "", user.DisplayName, ct);
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.BillingCustomerId, current), ct);
        }
        user.BillingCustomerId = current;
        // Saved above already: tell EF so, or the next SaveChanges would write it again.
        var entry = db.Entry(user);
        if (entry.State != EntityState.Detached) entry.Property(u => u.BillingCustomerId).OriginalValue = current;
        return current;
    }

    // ---------------------------------------------------------------- the provider's word

    /// <summary>
    /// Brings a customer's subscription grants in line with the provider: one grant per subscription, whose games,
    /// dates and status follow it (<see cref="PaidAccess"/>). Safe to run any number of times, in any order.
    /// </summary>
    public async Task SyncCustomerAsync(string customerId, CancellationToken ct)
    {
        await using var _ = await locks.AcquireAsync(customerId, ct);
        var userId = await db.Users.AsNoTracking().Where(u => u.BillingCustomerId == customerId).Select(u => u.Id).FirstOrDefaultAsync(ct);
        if (userId is null)
        {
            // A customer made in the dashboard, or whose host deleted their account: nobody here to give games to.
            log.LogWarning("Payments: no host has the customer {CustomerId}; nothing to update.", customerId);
            return;
        }

        var subscriptions = await Provider.SubscriptionsAsync(customerId, ct);
        var now = clock.GetUtcNow();
        var ids = subscriptions.Select(s => s.Id).ToList();
        var grants = await db.AccessGrants.Where(g => g.ExternalId != null && ids.Contains(g.ExternalId)).ToDictionaryAsync(g => g.ExternalId!, ct);
        foreach (var sub in subscriptions)
        {
            var terms = PaidAccess.ForSubscription(sub, setup.Options.GraceDays, now);
            grants.TryGetValue(sub.Id, out var grant);
            // Never paid for, or over before we heard of it: there's nothing to show.
            if (grant is null && !terms.GivesAccess(now)) continue;

            var games = await catalog.GamesForAsync(sub.Items, PlanKind.Subscription, ct) ?? grant?.Games;
            if (games is null)
            {
                log.LogWarning("Payments: subscription {SubscriptionId} is for {Prices}, which isn't one of the plans, so it gives no games. " +
                    "Set its price in Billing__Prices, or use a product that one of the plans' prices belongs to.",
                    sub.Id, string.Join(", ", sub.Items.Select(i => i.PriceId)));
                continue;
            }
            if (grant is null)
            {
                grant = new AccessGrantEntity
                {
                    Id = Guid.NewGuid(), UserId = userId, Kind = GrantKind.Subscription, ExternalId = sub.Id,
                    Note = "Subscription", CreatedAt = now,
                };
                db.AccessGrants.Add(grant);
            }
            grant.Games = games.Value;
            grant.StartsAt = terms.StartsAt;
            grant.Status = terms.Status;
            grant.RenewsAt = terms.RenewsAt;
            // Once a subscription is over, its access ends when it did, and an end already past never moves later.
            grant.EndsAt = terms.Status == PaidStatus.Ended && grant.EndsAt is { } had && had < terms.EndsAt ? had : terms.EndsAt;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A checkout finished: if it bought a party pass and is paid, the host gets the pass, once.
    /// (A subscription's checkout needs nothing here: the subscription it made is read by <see cref="SyncCustomerAsync"/>.)
    /// </summary>
    /// <param name="forUserId">When the host asks: only their own checkout counts.</param>
    /// <returns>The checkout, when it's the host's; null otherwise.</returns>
    private async Task<PaidCheckout?> CheckoutFinishedAsync(string checkoutId, string? forUserId, CancellationToken ct)
    {
        var checkout = await Provider.CheckoutSessionAsync(checkoutId, ct);
        if (checkout?.CustomerId is not { } customer) return null;
        var userId = await db.Users.AsNoTracking().Where(u => u.BillingCustomerId == customer).Select(u => u.Id).FirstOrDefaultAsync(ct);
        if (userId is null || (forUserId is not null && forUserId != userId)) return null;
        if (checkout is { Kind: PlanKind.Pass, Paid: true }) await RecordPassAsync(checkout, userId, customer, ct);
        return checkout;
    }

    private async Task RecordPassAsync(PaidCheckout checkout, string userId, string customer, CancellationToken ct)
    {
        await using var _ = await locks.AcquireAsync(customer, ct);
        if (await db.AccessGrants.AnyAsync(g => g.ExternalId == checkout.Id, ct)) return;

        var games = await catalog.GamesForAsync(checkout.Items, PlanKind.Pass, ct);
        if (games is null)
        {
            log.LogWarning("Payments: checkout {CheckoutId} paid for {Prices}, which isn't one of the passes, so it gives no games.",
                checkout.Id, string.Join(", ", checkout.Items.Select(i => i.PriceId)));
            return;
        }
        // From now, when we first hear it's paid: a payment that took a while to clear doesn't eat into the pass.
        var now = clock.GetUtcNow();
        db.AccessGrants.Add(new AccessGrantEntity
        {
            Id = Guid.NewGuid(), UserId = userId, Kind = GrantKind.Pass, Games = games.Value, StartsAt = now,
            EndsAt = now.AddHours(setup.Options.PassHours), Status = PaidStatus.Active, ExternalId = checkout.Id,
            Note = "Party pass", CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A webhook. Its signature is checked first: anyone can send a request to this address, and only one signed with
    /// the endpoint's secret can be from Stripe. Then whatever it's about is read fresh from the provider, and the
    /// event's id is kept so a repeat does nothing. If the provider can't be reached, this throws, the webhook gets an
    /// error, and Stripe sends it again later (for up to three days).
    /// </summary>
    public async Task<WebhookOutcome> HandleWebhookAsync(string payload, string? signature, CancellationToken ct)
    {
        if (setup.Provider is not { } provider) return WebhookOutcome.NotSetUp;
        var evt = provider.ReadEvent(payload, signature, clock.GetUtcNow());
        if (await db.BillingEvents.AnyAsync(e => e.Id == evt.Id, ct)) return WebhookOutcome.Repeat;

        // Only what can change a host's access; other events are just recorded (and are best not sent at all).
        if (evt.Type.StartsWith("checkout.session.", StringComparison.Ordinal)
            || evt.Type.StartsWith("customer.subscription.", StringComparison.Ordinal)
            || evt.Type.StartsWith("invoice.", StringComparison.Ordinal))
        {
            if (evt.CheckoutId is { } checkout) await CheckoutFinishedAsync(checkout, null, ct);
            if (evt.CustomerId is { } customer) await SyncCustomerAsync(customer, ct);
        }

        db.BillingEvents.Add(new BillingEventEntity
        {
            Id = evt.Id, Type = evt.Type.Length <= 100 ? evt.Type : evt.Type[..100], CustomerId = evt.CustomerId, ReceivedAt = clock.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return WebhookOutcome.Repeat; // another server handled the same event at the same moment
        }
        return WebhookOutcome.Handled;
    }

    /// <summary>
    /// A host is deleting their account: cancel every subscription first, at once, so they're never charged again.
    /// Their customer stays at the provider, with its invoices, which the site's owner must keep for tax.
    /// </summary>
    public async Task CancelEverythingAsync(AppUser user, CancellationToken ct)
    {
        if (user.BillingCustomerId is not { } customer) return;
        if (setup.Provider is not { } provider)
        {
            log.LogError("Payments are off, so {CustomerId}'s subscriptions couldn't be cancelled as their account was deleted. Cancel them in the Stripe dashboard.", customer);
            return;
        }
        foreach (var sub in await provider.SubscriptionsAsync(customer, ct))
            if (sub.Status is not ("canceled" or "incomplete_expired"))
                await provider.CancelAsync(sub.Id, ct);
    }
}

/// <summary>What the host's account page shows about buying (#101).</summary>
/// <param name="Enabled">The site sells plans.</param>
/// <param name="CanManage">The host has paid before, so the provider's billing page has something for them.</param>
/// <param name="Subscribed">The host has a subscription now (changing it is done on the billing page).</param>
/// <param name="Plans">Only plans whose price is set up correctly.</param>
public sealed record BillingView(bool Enabled, bool CanManage, bool Subscribed, int PassHours, IReadOnlyList<PlanOfferView> Plans);

/// <param name="Amount">In the currency's smallest unit (cents).</param>
public sealed record PlanOfferView(BillingPlan Id, bool Mysteries, bool EscapeRooms, PlanKind Kind, PlanInterval? Interval, long? Amount, string? Currency);
