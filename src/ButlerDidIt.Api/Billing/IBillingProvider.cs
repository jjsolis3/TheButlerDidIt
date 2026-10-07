namespace ButlerDidIt.Api.Billing;

/// <summary>
/// A payment provider (#101): Stripe, or the fake one for tests. Like <c>IEmailSender</c> and <c>IMediaStore</c>, the
/// rest of the app only knows this interface, so the provider can change (a Merchant of Record such as Paddle, say,
/// to handle sales tax) by writing one class. It speaks in the app's terms, never the provider's own types.
/// </summary>
public interface IBillingProvider
{
    /// <summary>For the admin's page: "Stripe" or "Fake".</summary>
    string Name { get; }

    /// <summary>True with live keys, where real cards are charged; false in test mode.</summary>
    bool LiveMode { get; }

    /// <summary>True when webhooks can be checked (the signing secret is set). Without it every webhook is refused.</summary>
    bool CanVerifyWebhooks { get; }

    /// <summary>Makes a customer for a host, the first time they pay. Returns its id.</summary>
    Task<string> CreateCustomerAsync(string userId, string email, string name, CancellationToken ct);

    /// <summary>Starts a checkout on the provider's own payment page and returns its address.</summary>
    Task<string> CheckoutAsync(CheckoutRequest request, CancellationToken ct);

    /// <summary>Opens the provider's billing page for a customer (card, cancel, invoices) and returns its address.</summary>
    Task<string> PortalAsync(string customerId, string returnUrl, CancellationToken ct);

    /// <summary>Every subscription the customer has or had, as the provider has them now.</summary>
    Task<IReadOnlyList<PaidSubscription>> SubscriptionsAsync(string customerId, CancellationToken ct);

    /// <summary>A checkout as the provider has it now, or null when there's no such checkout.</summary>
    Task<PaidCheckout?> CheckoutSessionAsync(string sessionId, CancellationToken ct);

    /// <summary>A price, or null when the provider has none with that id (a typo, or a price from the other mode).</summary>
    Task<PriceInfo?> PriceAsync(string priceId, CancellationToken ct);

    /// <summary>Cancels a subscription at once: nothing more is charged.</summary>
    Task CancelAsync(string subscriptionId, CancellationToken ct);

    /// <summary>
    /// Reads a webhook: checks that it really comes from the provider (its signature, made with a secret only the
    /// provider and this server know) and that it's recent, then reads what it's about.
    /// Throws <see cref="BillingSignatureException"/> when it can't be trusted.
    /// </summary>
    BillingEvent ReadEvent(string payload, string? signature, DateTimeOffset now);

    /// <summary>Where the admin sees a subscription (or, for a pass, its customer) in the provider's dashboard.</summary>
    string? DashboardUrl(string? externalId, string? customerId);
}

/// <param name="TrialEnd">For a subscription bought during the site's free trial: the first payment waits until then.</param>
/// <param name="Notice">Shown by the pay button on the payment page (#103): how the plan renews or ends, and the refund policy.</param>
public sealed record CheckoutRequest(
    string CustomerId,
    string UserId,
    string PriceId,
    PlanKind Kind,
    DateTimeOffset? TrialEnd,
    string SuccessUrl,
    string CancelUrl,
    bool AutomaticTax,
    string? Notice = null);

/// <summary>A subscription as the provider has it now.</summary>
/// <param name="Status">The provider's word for it, e.g. Stripe's <c>active</c>, <c>past_due</c> or <c>canceled</c>.</param>
/// <param name="PeriodStart">When the period being paid for began (or a free trial's start).</param>
/// <param name="PeriodEnd">When it renews, or ends if cancelled at the period's end.</param>
/// <param name="CancelAt">When a cancelled subscription ends; null when it renews.</param>
/// <param name="EndedAt">When it ended, once it has.</param>
public sealed record PaidSubscription(
    string Id,
    string CustomerId,
    string Status,
    IReadOnlyList<PaidItem> Items,
    DateTimeOffset StartedAt,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? CancelAt,
    DateTimeOffset? EndedAt);

/// <summary>One thing a subscription or a checkout pays for: a price, and the product it belongs to.</summary>
public sealed record PaidItem(string PriceId, string? ProductId);

/// <summary>A checkout as the provider has it now.</summary>
/// <param name="ForUserId">Which host started it, as the checkout itself says.</param>
/// <param name="Paid">True once the money is in: checkouts by bank transfer, say, complete before they're paid.</param>
public sealed record PaidCheckout(string Id, string? CustomerId, string? ForUserId, PlanKind Kind, bool Paid, IReadOnlyList<PaidItem> Items);

/// <param name="Amount">In the currency's smallest unit (cents), as the provider gives it.</param>
/// <param name="Interval">For a recurring price: "month", "year", and so on; null for a one-time price.</param>
public sealed record PriceInfo(string Id, string? ProductId, long? Amount, string Currency, string? Interval, long IntervalCount, bool Active, bool LiveMode);

/// <summary>What a webhook says happened.</summary>
/// <param name="CheckoutId">For a checkout's events, the checkout.</param>
public sealed record BillingEvent(string Id, string Type, string? CustomerId, string? CheckoutId);

/// <summary>Something the host or admin can act on, with a message that says what.</summary>
public class BillingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A webhook that couldn't be proved to come from the payment provider.</summary>
public sealed class BillingSignatureException(string message, Exception? inner = null) : BillingException(message, inner);
