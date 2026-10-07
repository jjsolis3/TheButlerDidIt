using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stripe;

namespace ButlerDidIt.Api.Billing;

/// <summary>
/// A pretend payment provider for tests and for trying the pages out (Billing:Provider=Fake; refused in Production).
/// It keeps customers, checkouts and subscriptions in memory, and this app serves its "checkout" and "billing" pages
/// (<see cref="BillingEndpoints"/>). It sends webhooks shaped and signed exactly like Stripe's, so they go through the
/// same checks as real ones.
///
/// Its prices are named for what they are: <c>fake_both_month_1200</c> is $12.00 a month, <c>fake_both_once_500</c>
/// a one-time $5.00. Prices with the same middle name belong to the same product.
/// </summary>
public sealed partial class FakeBilling(TimeProvider clock) : IBillingProvider
{
    /// <summary>The webhook signing secret. Not a secret: the fake is never used for real payments.</summary>
    public const string WebhookSecret = "whsec_fake";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FakeCheckout> _checkouts = [];
    private readonly Dictionary<string, FakeSubscription> _subscriptions = [];
    private readonly Dictionary<string, string> _portalReturns = [];
    private int _next;

    public string Name => "Fake";
    public bool LiveMode => false;
    public bool CanVerifyWebhooks => true;

    /// <summary>Every customer made, so a test can check one host never gets two.</summary>
    public List<string> Customers { get; } = [];

    public Task<string> CreateCustomerAsync(string userId, string email, string name, CancellationToken ct)
    {
        lock (_gate)
        {
            var id = $"cus_fake_{++_next}";
            Customers.Add(id);
            return Task.FromResult(id);
        }
    }

    public Task<string> CheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        if (Price(request.PriceId) is null) throw new BillingException($"Stripe couldn't start the checkout: No such price: '{request.PriceId}'");
        lock (_gate)
        {
            var id = $"cs_fake_{++_next}";
            _checkouts[id] = new FakeCheckout(id, request);
            return Task.FromResult($"{Origin(request.SuccessUrl)}/api/billing/fake/checkout/{id}");
        }
    }

    public Task<string> PortalAsync(string customerId, string returnUrl, CancellationToken ct)
    {
        lock (_gate)
        {
            _portalReturns[customerId] = returnUrl;
            return Task.FromResult($"{Origin(returnUrl)}/api/billing/fake/portal/{customerId}");
        }
    }

    public Task<IReadOnlyList<PaidSubscription>> SubscriptionsAsync(string customerId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PaidSubscription>>(_subscriptions.Values.Where(s => s.CustomerId == customerId).Select(s => s.Read()).ToList());
    }

    public Task<PaidCheckout?> CheckoutSessionAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_checkouts.TryGetValue(sessionId, out var c)) return Task.FromResult<PaidCheckout?>(null);
            var r = c.Request;
            return Task.FromResult<PaidCheckout?>(new PaidCheckout(c.Id, r.CustomerId, r.UserId, r.Kind, c.Paid, [new PaidItem(r.PriceId, Price(r.PriceId)?.ProductId)]));
        }
    }

    public Task<PriceInfo?> PriceAsync(string priceId, CancellationToken ct) => Task.FromResult(Price(priceId));

    public Task CancelAsync(string subscriptionId, CancellationToken ct)
    {
        Change(subscriptionId, s => s.Cancel(clock.GetUtcNow()));
        return Task.CompletedTask;
    }

    public BillingEvent ReadEvent(string payload, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signature)) throw new BillingSignatureException("The webhook has no Stripe-Signature header.");
        try
        {
            EventUtility.ValidateSignature(payload, signature, WebhookSecret, 300, now.ToUnixTimeSeconds());
        }
        catch (StripeException ex)
        {
            throw new BillingSignatureException(ex.Message, ex);
        }
        var json = JsonNode.Parse(payload)!;
        var obj = json["data"]?["object"];
        return new BillingEvent(json["id"]!.GetValue<string>(), json["type"]!.GetValue<string>(), obj?["customer"]?.GetValue<string>(),
            obj?["object"]?.GetValue<string>() == "checkout.session" ? obj["id"]?.GetValue<string>() : null);
    }

    public string? DashboardUrl(string? externalId, string? customerId) => null;

    // ---------------------------------------------------------------- what the customer and "Stripe" do

    /// <summary>The checkout page's details: what's being bought, and where to go back to.</summary>
    public CheckoutRequest? Checkout(string id)
    {
        lock (_gate) return _checkouts.TryGetValue(id, out var c) ? c.Request : null;
    }

    /// <summary>
    /// The customer pays (the checkout page's button): a subscription starts, as Stripe would start it, and the
    /// webhook Stripe would send comes back, signed, for the app to handle. Null when there's no such checkout.
    /// </summary>
    public FakeWebhook? Pay(string checkoutId)
    {
        lock (_gate)
        {
            if (!_checkouts.TryGetValue(checkoutId, out var c) || c.Paid) return null;
            c.Paid = true;
            var r = c.Request;
            if (r.Kind == PlanKind.Subscription)
            {
                var price = Price(r.PriceId)!;
                var now = clock.GetUtcNow();
                var trial = r.TrialEnd is { } end && end > now;
                var sub = new FakeSubscription($"sub_fake_{++_next}", r.CustomerId, price)
                {
                    Status = trial ? "trialing" : "active",
                    StartedAt = now,
                    PeriodStart = now,
                    PeriodEnd = trial ? r.TrialEnd!.Value : FakeSubscription.Next(now, price),
                };
                _subscriptions[sub.Id] = sub;
            }
            return Event("checkout.session.completed", new JsonObject
            {
                ["id"] = c.Id, ["object"] = "checkout.session", ["customer"] = r.CustomerId,
                ["mode"] = r.Kind == PlanKind.Subscription ? "subscription" : "payment",
            });
        }
    }

    /// <summary>Changes a subscription as Stripe might (renewed, a card declined, cancelled) and returns Stripe's webhook for it.</summary>
    public FakeWebhook Change(string subscriptionId, Action<FakeSubscription> change, string type = "customer.subscription.updated")
    {
        lock (_gate)
        {
            var sub = _subscriptions[subscriptionId];
            change(sub);
            return Event(type, new JsonObject { ["id"] = sub.Id, ["object"] = "subscription", ["customer"] = sub.CustomerId, ["status"] = sub.Status });
        }
    }

    public IReadOnlyList<FakeSubscription> SubscriptionsOf(string customerId)
    {
        lock (_gate) return _subscriptions.Values.Where(s => s.CustomerId == customerId).ToList();
    }

    /// <summary>Where the portal's "back" goes.</summary>
    public string? PortalReturn(string customerId)
    {
        lock (_gate) return _portalReturns.GetValueOrDefault(customerId);
    }

    /// <summary>A webhook as Stripe would send it: an event around <paramref name="obj"/>, with a fresh id and Stripe's signature header.</summary>
    public FakeWebhook Event(string type, JsonObject obj)
    {
        var payload = new JsonObject
        {
            ["id"] = $"evt_fake_{Guid.NewGuid():N}",
            ["object"] = "event",
            ["type"] = type,
            ["created"] = clock.GetUtcNow().ToUnixTimeSeconds(),
            ["data"] = new JsonObject { ["object"] = obj },
        }.ToJsonString();
        return new FakeWebhook(payload, Sign(payload, clock.GetUtcNow()));
    }

    /// <summary>Stripe's Stripe-Signature header for <paramref name="payload"/>, as signed at <paramref name="at"/>.</summary>
    public static string Sign(string payload, DateTimeOffset at, string secret = WebhookSecret) =>
        EventUtility.GenerateSignatureHeader(payload, secret, at.ToUnixTimeSeconds());

    /// <summary>A fake price, read from its name; null for any other name (as Stripe answers a typo).</summary>
    public static PriceInfo? Price(string priceId)
    {
        var m = PriceName().Match(priceId);
        if (!m.Success) return null;
        var interval = m.Groups["interval"].Value;
        return new PriceInfo(priceId, $"prod_fake_{m.Groups["product"].Value}", long.Parse(m.Groups["amount"].Value), "usd",
            interval == "once" ? null : interval, interval == "once" ? 0 : 1, Active: true, LiveMode: false);
    }

    [GeneratedRegex("^fake_(?<product>[a-z0-9]+)_(?<interval>month|year|once)_(?<amount>[0-9]+)$")]
    private static partial Regex PriceName();

    private static string Origin(string url) => new Uri(url).GetLeftPart(UriPartial.Authority);

    private sealed class FakeCheckout(string id, CheckoutRequest request)
    {
        public string Id { get; } = id;
        public CheckoutRequest Request { get; } = request;
        public bool Paid { get; set; }
    }
}

/// <summary>A webhook from the fake provider: its body, and the Stripe-Signature header to send with it.</summary>
public sealed record FakeWebhook(string Payload, string Signature);

/// <summary>
/// A subscription at the fake provider, which tests change to play out what Stripe would do. Its times are whole
/// seconds, as Stripe's are (Unix timestamps).
/// </summary>
public sealed class FakeSubscription(string id, string customerId, PriceInfo price)
{
    public string Id { get; } = id;
    public string CustomerId { get; } = customerId;
    public PriceInfo Price { get; set; } = price;
    public string Status { get; set; } = "active";
    public DateTimeOffset StartedAt { get; set => field = Whole(value); }
    public DateTimeOffset PeriodStart { get; set => field = Whole(value); }
    public DateTimeOffset PeriodEnd { get; set => field = Whole(value); }
    public DateTimeOffset? CancelAt { get; set => field = value is { } v ? Whole(v) : null; }
    public DateTimeOffset? EndedAt { get; set => field = value is { } v ? Whole(v) : null; }

    private static DateTimeOffset Whole(DateTimeOffset t) => DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());

    /// <summary>The next period, as a renewal starts it.</summary>
    public void Renew()
    {
        PeriodStart = PeriodEnd;
        PeriodEnd = Next(PeriodStart, Price);
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = "canceled";
        EndedAt = now;
    }

    public static DateTimeOffset Next(DateTimeOffset from, PriceInfo price) => price.Interval == "year" ? from.AddYears(1) : from.AddMonths(1);

    internal PaidSubscription Read() =>
        new(Id, CustomerId, Status, [new PaidItem(Price.Id, Price.ProductId)], StartedAt, PeriodStart, PeriodEnd, CancelAt, EndedAt);
}
