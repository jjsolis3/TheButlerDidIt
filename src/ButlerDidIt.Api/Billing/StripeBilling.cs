using System.Net;
using System.Text.Json.Nodes;
using Stripe;

namespace ButlerDidIt.Api.Billing;

/// <summary>
/// Stripe (#101), through Stripe's own .NET library: Checkout (Stripe's hosted payment page, so card numbers never
/// reach this server), the Customer Portal (Stripe's hosted billing page) and webhooks. The library pins the Stripe
/// API version it was built for, so the shapes below can't change under us when Stripe releases a new version.
/// </summary>
internal sealed class StripeBilling : IBillingProvider
{
    /// <summary>How old a webhook may be (Stripe's own default): an old one replayed by someone who copied it is refused.</summary>
    private const long ToleranceSeconds = 300;

    private readonly StripeClient _client;
    private readonly string? _webhookSecret;

    public StripeBilling(StripeSettings settings)
    {
        var key = settings.SecretKey ?? throw new ArgumentException("The Stripe secret key is missing.", nameof(settings));
        _client = string.IsNullOrWhiteSpace(settings.ApiBase) ? new StripeClient(key) : new StripeClient(key, apiBase: settings.ApiBase.TrimEnd('/'));
        _webhookSecret = string.IsNullOrWhiteSpace(settings.WebhookSecret) ? null : settings.WebhookSecret.Trim();
        LiveMode = key.Contains("_live_", StringComparison.Ordinal);
    }

    public string Name => "Stripe";
    public bool LiveMode { get; }
    public bool CanVerifyWebhooks => _webhookSecret is not null;

    public Task<string> CreateCustomerAsync(string userId, string email, string name, CancellationToken ct) =>
        CallAsync("set up your billing", async () =>
        {
            var customer = await new CustomerService(_client).CreateAsync(new CustomerCreateOptions
            {
                Email = email,
                Name = name,
                // So whoever reads the Stripe dashboard can find the host here, and the other way round.
                Metadata = new() { ["userId"] = userId },
            }, cancellationToken: ct);
            return customer.Id;
        });

    public Task<string> CheckoutAsync(CheckoutRequest request, CancellationToken ct) =>
        CallAsync("start the checkout", async () =>
        {
            var metadata = new Dictionary<string, string> { ["userId"] = request.UserId };
            var options = new Stripe.Checkout.SessionCreateOptions
            {
                Mode = request.Kind == PlanKind.Subscription ? "subscription" : "payment",
                Customer = request.CustomerId,
                // The host the checkout is for, read back when it's finished.
                ClientReferenceId = request.UserId,
                LineItems = [new() { Price = request.PriceId, Quantity = 1 }],
                SuccessUrl = request.SuccessUrl,
                CancelUrl = request.CancelUrl,
                AllowPromotionCodes = true,
                Metadata = metadata,
            };
            if (request.Kind == PlanKind.Subscription)
                options.SubscriptionData = new() { Metadata = metadata, TrialEnd = request.TrialEnd?.UtcDateTime };
            else
                options.PaymentIntentData = new() { Metadata = metadata };
            // Stripe shows this right by its pay button: the renewal terms and the refund policy, where they count.
            if (request.Notice is { Length: > 0 } notice) options.CustomText = new() { Submit = new() { Message = notice } };
            if (request.AutomaticTax)
            {
                options.AutomaticTax = new() { Enabled = true };
                // Stripe Tax works out the tax from the address Checkout asks for, which has to be saved on the customer.
                options.CustomerUpdate = new() { Address = "auto" };
            }
            var session = await new Stripe.Checkout.SessionService(_client).CreateAsync(options, cancellationToken: ct);
            return session.Url;
        });

    public Task<string> PortalAsync(string customerId, string returnUrl, CancellationToken ct) =>
        CallAsync("open your billing page", async () =>
        {
            var session = await new Stripe.BillingPortal.SessionService(_client).CreateAsync(
                new Stripe.BillingPortal.SessionCreateOptions { Customer = customerId, ReturnUrl = returnUrl }, cancellationToken: ct);
            return session.Url;
        });

    public Task<IReadOnlyList<PaidSubscription>> SubscriptionsAsync(string customerId, CancellationToken ct) =>
        CallAsync("read the subscriptions", async () =>
        {
            var found = new List<PaidSubscription>();
            // "all" includes cancelled ones: a cancellation must end the host's access, not just go unseen.
            var options = new SubscriptionListOptions { Customer = customerId, Status = "all", Limit = 100 };
            await foreach (var s in new SubscriptionService(_client).ListAutoPagingAsync(options, cancellationToken: ct))
            {
                var items = s.Items?.Data ?? [];
                // Since Stripe's 2025 API, each item has its own billing period. Every item of a subscription here
                // renews together, so the earliest is the subscription's.
                DateTimeOffset? periodStart = items.Count == 0 ? null : items.Min(i => Utc(i.CurrentPeriodStart));
                DateTimeOffset? periodEnd = items.Count == 0 ? null : items.Min(i => Utc(i.CurrentPeriodEnd));
                found.Add(new PaidSubscription(
                    s.Id,
                    s.CustomerId,
                    s.Status,
                    items.Select(i => new PaidItem(i.Price.Id, i.Price.ProductId)).ToList(),
                    Utc(s.StartDate),
                    periodStart,
                    periodEnd,
                    s.CancelAt is { } at ? Utc(at) : s.CancelAtPeriodEnd ? periodEnd : null,
                    s.EndedAt is { } ended ? Utc(ended) : null));
            }
            return (IReadOnlyList<PaidSubscription>)found;
        });

    public Task<PaidCheckout?> CheckoutSessionAsync(string sessionId, CancellationToken ct) =>
        CallAsync("read the checkout", async () =>
        {
            try
            {
                var session = await new Stripe.Checkout.SessionService(_client).GetAsync(sessionId,
                    new Stripe.Checkout.SessionGetOptions { Expand = ["line_items"] }, cancellationToken: ct);
                return new PaidCheckout(
                    session.Id,
                    session.CustomerId,
                    session.ClientReferenceId,
                    session.Mode == "subscription" ? PlanKind.Subscription : PlanKind.Pass,
                    // A promotion code can make it free, which is as good as paid.
                    session.Status == "complete" && session.PaymentStatus is "paid" or "no_payment_required",
                    (session.LineItems?.Data ?? []).Where(li => li.Price is not null).Select(li => new PaidItem(li.Price.Id, li.Price.ProductId)).ToList());
            }
            catch (StripeException ex) when (Missing(ex))
            {
                return null;
            }
        });

    public Task<PriceInfo?> PriceAsync(string priceId, CancellationToken ct) =>
        CallAsync("read the price", async () =>
        {
            try
            {
                var p = await new PriceService(_client).GetAsync(priceId, cancellationToken: ct);
                return new PriceInfo(p.Id, p.ProductId, p.UnitAmount, p.Currency, p.Recurring?.Interval, p.Recurring?.IntervalCount ?? 0, p.Active, p.Livemode);
            }
            catch (StripeException ex) when (Missing(ex))
            {
                return (PriceInfo?)null;
            }
        });

    public Task CancelAsync(string subscriptionId, CancellationToken ct) =>
        CallAsync("cancel the subscription", async () => await new SubscriptionService(_client).CancelAsync(subscriptionId, cancellationToken: ct));

    public BillingEvent ReadEvent(string payload, string? signature, DateTimeOffset now)
    {
        if (_webhookSecret is null) throw new BillingSignatureException("The webhook signing secret (Billing__Stripe__WebhookSecret) isn't set, so no webhook can be trusted.");
        if (string.IsNullOrWhiteSpace(signature)) throw new BillingSignatureException("The webhook has no Stripe-Signature header.");
        try
        {
            // Stripe signs "{timestamp}.{body}" with the endpoint's secret (HMAC-SHA256). The library checks that, in
            // constant time, and that the timestamp is within the tolerance.
            EventUtility.ValidateSignature(payload, signature, _webhookSecret, ToleranceSeconds, now.ToUnixTimeSeconds());
        }
        catch (StripeException ex)
        {
            throw new BillingSignatureException(ex.Message, ex);
        }

        // Only the ids are read from the event itself. Everything that decides a host's access is read fresh from
        // Stripe, so an event in another API version's shape, or one arriving out of order, can't mislead us.
        var json = JsonNode.Parse(payload) ?? throw new BillingSignatureException("The webhook is empty.");
        var obj = json["data"]?["object"];
        var isCheckout = obj?["object"]?.GetValue<string>() == "checkout.session";
        return new BillingEvent(
            json["id"]?.GetValue<string>() ?? throw new BillingSignatureException("The webhook has no event id."),
            json["type"]?.GetValue<string>() ?? "",
            IdOf(obj?["customer"]),
            isCheckout ? obj?["id"]?.GetValue<string>() : null);
    }

    public string? DashboardUrl(string? externalId, string? customerId)
    {
        var root = LiveMode ? "https://dashboard.stripe.com/" : "https://dashboard.stripe.com/test/";
        if (externalId?.StartsWith("sub_", StringComparison.Ordinal) == true) return $"{root}subscriptions/{externalId}";
        return customerId is null ? null : $"{root}customers/{customerId}";
    }

    /// <summary>A customer is an id in webhooks, or an object when expanded.</summary>
    private static string? IdOf(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var id) => id,
        JsonObject o => o["id"]?.GetValue<string>(),
        _ => null,
    };

    private static bool Missing(StripeException ex) => ex.HttpStatusCode == HttpStatusCode.NotFound || ex.StripeError?.Code == "resource_missing";

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    /// <summary>Stripe's errors become messages for the host or admin, saying what couldn't be done and Stripe's reason.</summary>
    private static async Task<T> CallAsync<T>(string doing, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (StripeException ex)
        {
            throw new BillingException($"Stripe couldn't {doing}: {ex.StripeError?.Message ?? ex.Message}", ex);
        }
    }

    private static Task CallAsync(string doing, Func<Task> call) => CallAsync<bool>(doing, async () =>
    {
        await call();
        return true;
    });
}
