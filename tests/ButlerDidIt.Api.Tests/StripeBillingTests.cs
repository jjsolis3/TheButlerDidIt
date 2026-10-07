using ButlerDidIt.Api.Billing;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// The Stripe adapter (#101). Its requests are checked against stripe-mock, Stripe's own simulator, which rejects any
/// request that Stripe's API definition (for the version Stripe.net pins) wouldn't accept, and answers with Stripe's
/// sample objects. Set TEST_STRIPE_MOCK_URL (e.g. http://localhost:12111) to run them:
///   go install github.com/stripe/stripe-mock@latest &amp;&amp; stripe-mock -http-port 12111
/// The webhook checks need no network.
/// </summary>
public class StripeBillingTests
{
    private const string Secret = "whsec_test_secret";

    private static IBillingProvider Stripe(string? apiBase = null) => new BillingSetup(
        Options.Create(new BillingOptions { Stripe = { SecretKey = "sk_test_123", WebhookSecret = Secret, ApiBase = apiBase } }),
        new HostingEnvironment { EnvironmentName = "Production" }, TimeProvider.System, NullLogger<BillingSetup>.Instance).Provider!;

    private static IBillingProvider Mock() => Stripe(Environment.GetEnvironmentVariable("TEST_STRIPE_MOCK_URL"));

    // ---------------------------------------------------------------- webhooks (no network)

    private const string Event = """
        {"id":"evt_1","object":"event","type":"customer.subscription.updated","api_version":"2019-01-01",
         "data":{"object":{"id":"sub_1","object":"subscription","customer":"cus_1","status":"active"}}}
        """;

    [Fact]
    public void A_webhook_signed_with_the_endpoints_secret_is_read()
    {
        var now = DateTimeOffset.UtcNow;
        // An event in an old API version's shape is fine: only its ids are read.
        var evt = Stripe().ReadEvent(Event, EventUtility.GenerateSignatureHeader(Event, Secret, now.ToUnixTimeSeconds()), now);
        Assert.Equal(new BillingEvent("evt_1", "customer.subscription.updated", "cus_1", null), evt);

        var checkout = """{"id":"evt_2","type":"checkout.session.completed","data":{"object":{"id":"cs_1","object":"checkout.session","customer":"cus_1"}}}""";
        Assert.Equal("cs_1", Stripe().ReadEvent(checkout, EventUtility.GenerateSignatureHeader(checkout, Secret, now.ToUnixTimeSeconds()), now).CheckoutId);
    }

    [Fact]
    public void A_webhook_with_a_wrong_missing_or_old_signature_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var stripe = Stripe();
        Assert.Throws<BillingSignatureException>(() => stripe.ReadEvent(Event, EventUtility.GenerateSignatureHeader(Event, "whsec_other", now.ToUnixTimeSeconds()), now));
        Assert.Throws<BillingSignatureException>(() => stripe.ReadEvent(Event.Replace("active", "canceled"), EventUtility.GenerateSignatureHeader(Event, Secret, now.ToUnixTimeSeconds()), now));
        Assert.Throws<BillingSignatureException>(() => stripe.ReadEvent(Event, null, now));
        Assert.Throws<BillingSignatureException>(() => stripe.ReadEvent(Event, EventUtility.GenerateSignatureHeader(Event, Secret, now.AddMinutes(-6).ToUnixTimeSeconds()), now));

        // Without the signing secret nothing can be trusted, so everything is refused, and the admin's page says why.
        var unsigned = new BillingSetup(Options.Create(new BillingOptions { Stripe = { SecretKey = "sk_live_123" } }),
            new HostingEnvironment { EnvironmentName = "Production" }, TimeProvider.System, NullLogger<BillingSetup>.Instance).Provider!;
        Assert.False(unsigned.CanVerifyWebhooks);
        Assert.True(unsigned.LiveMode);
        Assert.Contains("WebhookSecret", Assert.Throws<BillingSignatureException>(() =>
            unsigned.ReadEvent(Event, EventUtility.GenerateSignatureHeader(Event, Secret, now.ToUnixTimeSeconds()), now)).Message);
    }

    [Fact]
    public void Dashboard_links_point_at_test_or_live_mode()
    {
        Assert.Equal("https://dashboard.stripe.com/test/subscriptions/sub_1", Stripe().DashboardUrl("sub_1", "cus_1"));
        Assert.Equal("https://dashboard.stripe.com/test/customers/cus_1", Stripe().DashboardUrl("cs_1", "cus_1")); // a pass: its customer
    }

    // ---------------------------------------------------------------- Stripe's API (stripe-mock)

    [RequiresEnvFact("TEST_STRIPE_MOCK_URL")]
    public async Task Customers_checkouts_and_the_billing_page_are_requests_stripe_accepts()
    {
        var stripe = Mock();
        var customer = await stripe.CreateCustomerAsync("user-1", "host@example.com", "Hana", default);
        Assert.StartsWith("cus_", customer);

        // A subscription bought during the site's trial, with Stripe Tax on, and a party pass.
        var subscription = await stripe.CheckoutAsync(new CheckoutRequest(customer, "user-1", "price_123", PlanKind.Subscription,
            DateTimeOffset.UtcNow.AddDays(10), "https://site.example/account?billing=done&session={CHECKOUT_SESSION_ID}",
            "https://site.example/account?billing=cancelled", AutomaticTax: true), default);
        Assert.StartsWith("https://", subscription);
        var pass = await stripe.CheckoutAsync(new CheckoutRequest(customer, "user-1", "price_123", PlanKind.Pass, null,
            "https://site.example/account?billing=done&session={CHECKOUT_SESSION_ID}", "https://site.example/account?billing=cancelled", AutomaticTax: false), default);
        Assert.StartsWith("https://", pass);

        Assert.StartsWith("https://", await stripe.PortalAsync(customer, "https://site.example/account?billing=back", default));
    }

    [RequiresEnvFact("TEST_STRIPE_MOCK_URL")]
    public async Task Subscriptions_checkouts_and_prices_are_read_in_the_apps_terms()
    {
        var stripe = Mock();
        var subscriptions = await stripe.SubscriptionsAsync("cus_123", default);
        var sub = Assert.Single(subscriptions);
        Assert.StartsWith("sub_", sub.Id);
        Assert.False(string.IsNullOrEmpty(sub.Status));
        var item = Assert.Single(sub.Items);
        Assert.StartsWith("price_", item.PriceId);
        Assert.StartsWith("prod_", item.ProductId);
        Assert.NotNull(sub.PeriodEnd); // read from the subscription's items, where Stripe's API keeps it now

        var checkout = await stripe.CheckoutSessionAsync("cs_123", default);
        Assert.NotNull(checkout);
        Assert.StartsWith("cs_", checkout.Id);

        var price = await stripe.PriceAsync("price_123", default);
        Assert.NotNull(price);
        Assert.Equal(("usd", false), (price.Currency, price.LiveMode));
        Assert.StartsWith("prod_", price.ProductId);

        await stripe.CancelAsync(sub.Id, default);
    }

    [RequiresEnvFact("TEST_STRIPE_MOCK_URL")]
    public async Task Stripes_refusals_become_messages_that_say_what_could_not_be_done()
    {
        // stripe-mock refuses a key that isn't a test key, as Stripe refuses a wrong key.
        var wrongKey = new BillingSetup(Options.Create(new BillingOptions { Stripe = { SecretKey = "not-a-key", ApiBase = Environment.GetEnvironmentVariable("TEST_STRIPE_MOCK_URL") } }),
            new HostingEnvironment { EnvironmentName = "Production" }, TimeProvider.System, NullLogger<BillingSetup>.Instance).Provider!;
        var ex = await Assert.ThrowsAsync<BillingException>(() => wrongKey.CreateCustomerAsync("user-1", "host@example.com", "Hana", default));
        Assert.StartsWith("Stripe couldn't set up your billing:", ex.Message);
        Assert.NotNull(ex.InnerException); // so the endpoint answers 502: Stripe's side, not the host's
    }
}
