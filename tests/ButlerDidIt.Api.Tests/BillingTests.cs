using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ButlerDidIt.Api.Billing;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Api.Plans;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>A server that sells plans through the fake payment provider (#101). Its first account is the admin.</summary>
public sealed class BillingFactory : ApiFactory
{
    private readonly Lazy<Task<HttpClient>> _admin;

    public BillingFactory() => _admin = new(async () =>
    {
        var client = CreateClient();
        (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("owner@example.com", "password123", "Owner"))).EnsureSuccessStatusCode();
        return client;
    });

    public Task<HttpClient> AdminAsync() => _admin.Value;

    public FakeBilling Fake => Services.GetRequiredService<BillingSetup>().Fake!;

    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        ("Billing:Provider", "Fake"),
        ("Billing:Prices:MysteriesMonthly", "fake_mysteries_month_800"),
        ("Billing:Prices:BothMonthly", "fake_both_month_1200"),
        ("Billing:Prices:BothYearly", "fake_both_year_12000"),
        ("Billing:Prices:EscapeRoomsPass", "fake_escapepass_once_500"),
        ("Billing:Prices:BothPass", "fake_bothpass_once_900"),
        // Set up wrong: a one-time price for a monthly plan. The admin is told; hosts never see it.
        ("Billing:Prices:EscapeRoomsMonthly", "fake_wrong_once_800"),
    ];
}

public class BillingTests(BillingFactory app) : IClassFixture<BillingFactory>
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)?["detail"]?.GetValue<string>() ?? body;
    }

    private async Task<(HttpClient Client, string Id)> HostAsync(bool trialOver = true)
    {
        await app.AdminAsync();
        var client = app.CreateClient();
        var me = await Read<MeResponse>(await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"h{Guid.NewGuid():N}@example.com", "password123", "Hana")));
        if (trialOver) await EndTrialAsync(me.Id);
        return (client, me.Id);
    }

    private async Task EndTrialAsync(string userId)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().AccessGrants.Where(g => g.UserId == userId && g.Kind == GrantKind.Trial)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.EndsAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    private static async Task<AccessView> AccessAsync(HttpClient host) => (await Read<MeResponse>(await host.GetAsync("/api/auth/me"))).Access;

    private static Task<HttpResponseMessage> BuyAsync(HttpClient host, BillingPlan plan) =>
        host.PostAsJsonAsync("/api/billing/checkout", new BuyRequest(plan), GameJson.Options);

    private static string CheckoutId(string url) => url[(url.LastIndexOf('/') + 1)..];

    /// <summary>The host pays on the (fake) payment page, which sends the webhook and sends the browser back.</summary>
    private async Task<HttpResponseMessage> PayAsync(string checkoutUrl)
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await browser.PostAsync(new Uri(checkoutUrl).AbsolutePath + "/pay", null);
    }

    private async Task<string> SubscribeAsync(HttpClient host, BillingPlan plan = BillingPlan.BothMonthly)
    {
        var url = (await Read<RedirectView>(await BuyAsync(host, plan))).Url;
        Assert.Equal(HttpStatusCode.Redirect, (await PayAsync(url)).StatusCode);
        return CheckoutId(url);
    }

    private async Task<string> CustomerOfAsync(string userId)
    {
        using var scope = app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == userId)).BillingCustomerId!;
    }

    private async Task<List<AccessGrantEntity>> PaidGrantsAsync(string userId)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AccessGrants.AsNoTracking()
            .Where(g => g.UserId == userId && g.ExternalId != null).ToListAsync();
    }

    private Task<HttpResponseMessage> SendWebhookAsync(string payload, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhook") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (signature is not null) request.Headers.Add("Stripe-Signature", signature);
        return app.CreateClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> SendWebhookAsync(FakeWebhook webhook) => SendWebhookAsync(webhook.Payload, webhook.Signature);

    private static Task<HttpResponseMessage> StartEscapeAsync(HttpClient host) => host.PostAsJsonAsync("/api/parties/escape",
        new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, null, PuzzleChoice.Fresh, null), GameJson.Options);

    private static Task<HttpResponseMessage> StartMysteryAsync(HttpClient host) => host.PostAsJsonAsync("/api/parties",
        new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null, UseAi: false), GameJson.Options);

    // ---------------------------------------------------------------- buying

    [Fact]
    public async Task A_host_whose_trial_ended_buys_a_subscription_and_the_webhook_gives_them_the_games()
    {
        var (host, id) = await HostAsync();
        var view = await Read<BillingView>(await host.GetAsync("/api/billing"));
        Assert.True(view.Enabled);
        Assert.Equal([BillingPlan.MysteriesMonthly, BillingPlan.BothMonthly, BillingPlan.BothYearly, BillingPlan.EscapeRoomsPass, BillingPlan.BothPass],
            view.Plans.Select(p => p.Id)); // the misconfigured plan isn't offered
        Assert.Equal((1200L, "usd", PlanInterval.Month), view.Plans.Where(p => p.Id == BillingPlan.BothMonthly).Select(p => (p.Amount!.Value, p.Currency, p.Interval!.Value)).Single());

        // Locked out, and told what to do about it.
        Assert.Contains("Choose a plan on your account page", await ProblemAsync(await StartEscapeAsync(host), HttpStatusCode.Forbidden));
        Assert.True((await AccessAsync(host)).Payments);

        var url = (await Read<RedirectView>(await BuyAsync(host, BillingPlan.BothMonthly))).Url;
        Assert.Contains("/api/billing/fake/checkout/cs_fake_", url);
        var checkout = CheckoutId(url);

        // Coming back to the "success" address before paying proves nothing: anyone can visit it.
        var unpaid = await Read<SyncView>(await host.PostAsJsonAsync("/api/billing/sync", new SyncRequest(checkout)));
        Assert.Equal((AccessPlan.TrialEnded, false), (unpaid.Me.Access.Plan, unpaid.Paid));

        var back = await PayAsync(url);
        Assert.Equal($"/account?billing=done&session={checkout}", back.Headers.Location!.PathAndQuery);
        Assert.True((await Read<SyncView>(await host.PostAsJsonAsync("/api/billing/sync", new SyncRequest(checkout)))).Paid);

        var access = await AccessAsync(host);
        Assert.Equal((true, true, AccessPlan.Subscription, PaidStatus.Active), (access.Mysteries, access.EscapeRooms, access.Plan, access.Status));
        Assert.InRange(access.RenewsAt!.Value, DateTimeOffset.UtcNow.AddMonths(1).AddMinutes(-5), DateTimeOffset.UtcNow.AddMonths(1).AddMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await StartEscapeAsync(host)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartMysteryAsync(host)).StatusCode);

        // The billing page now offers to manage it, and one customer was made for the host.
        var after = await Read<BillingView>(await host.GetAsync("/api/billing"));
        Assert.True(after.CanManage && after.Subscribed);
        Assert.Single(app.Fake.Customers, await CustomerOfAsync(id));
        var portal = await Read<RedirectView>(await host.PostAsync("/api/billing/portal", null));
        Assert.Contains("/api/billing/fake/portal/cus_fake_", portal.Url);
    }

    [Fact]
    public async Task Subscribing_during_the_free_trial_charges_nothing_until_the_trial_ends()
    {
        var (host, id) = await HostAsync(trialOver: false);
        var trialEnds = (await AccessAsync(host)).EndsAt!.Value;
        var checkout = await SubscribeAsync(host, BillingPlan.BothYearly);
        Assert.Equal(trialEnds, app.Fake.Checkout(checkout)!.TrialEnd);

        var access = await AccessAsync(host);
        // Stripe keeps times in whole seconds.
        Assert.Equal((AccessPlan.Subscription, PaidStatus.Trialing, DateTimeOffset.FromUnixTimeSeconds(trialEnds.ToUnixTimeSeconds())),
            (access.Plan, access.Status, access.RenewsAt));
        Assert.Single(await PaidGrantsAsync(id));
    }

    [Fact]
    public async Task One_subscription_at_a_time_and_the_admin_never_pays()
    {
        var admin = await app.AdminAsync();
        Assert.Contains("You run this site", await ProblemAsync(await BuyAsync(admin, BillingPlan.BothMonthly), HttpStatusCode.BadRequest));

        var (host, _) = await HostAsync();
        Assert.Contains("isn't for sale", await ProblemAsync(await BuyAsync(host, BillingPlan.EscapeRoomsMonthly), HttpStatusCode.BadRequest));
        Assert.Contains("isn't for sale", await ProblemAsync(await BuyAsync(host, BillingPlan.EscapeRoomsYearly), HttpStatusCode.BadRequest)); // no price at all

        await SubscribeAsync(host, BillingPlan.MysteriesMonthly);
        var access = await AccessAsync(host);
        Assert.Equal((true, false), (access.Mysteries, access.EscapeRooms));
        // A second subscription would charge twice: plan changes are made on the billing page.
        Assert.Contains("You already have a subscription", await ProblemAsync(await BuyAsync(host, BillingPlan.BothMonthly), HttpStatusCode.BadRequest));
        // A pass for the game the plan lacks is fine; one for a game they have would add nothing.
        Assert.Equal(HttpStatusCode.OK, (await BuyAsync(host, BillingPlan.EscapeRoomsPass)).StatusCode);
    }

    [Fact]
    public async Task A_party_pass_gives_its_games_for_its_hours_once_however_often_it_is_reported()
    {
        var (host, id) = await HostAsync();
        var url = (await Read<RedirectView>(await BuyAsync(host, BillingPlan.EscapeRoomsPass))).Url;
        await PayAsync(url);

        var access = await AccessAsync(host);
        Assert.Equal((AccessPlan.Pass, true, false), (access.Plan, access.EscapeRooms, access.Mysteries));
        Assert.InRange(access.EndsAt!.Value, DateTimeOffset.UtcNow.AddHours(72).AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(72).AddMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await StartEscapeAsync(host)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartMysteryAsync(host)).StatusCode);

        // Stripe reports the same checkout again (a new event about it), and the host comes back to the success page.
        var checkout = CheckoutId(url);
        var again = app.Fake.Event("checkout.session.completed", new JsonObject { ["id"] = checkout, ["object"] = "checkout.session", ["customer"] = await CustomerOfAsync(id) });
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(again)).StatusCode);
        await host.PostAsJsonAsync("/api/billing/sync", new SyncRequest(checkout));
        Assert.Single(await PaidGrantsAsync(id));

        // Another pass for the same game would add nothing.
        Assert.Contains("would add nothing", await ProblemAsync(await BuyAsync(host, BillingPlan.EscapeRoomsPass), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Someone_elses_checkout_gives_nothing()
    {
        var (buyer, _) = await HostAsync();
        var url = (await Read<RedirectView>(await BuyAsync(buyer, BillingPlan.BothPass))).Url;
        await PayAsync(url);

        // Another host, who has paid before (so has a customer), comes back with the buyer's checkout id.
        var (other, otherId) = await HostAsync();
        await PayAsync((await Read<RedirectView>(await BuyAsync(other, BillingPlan.EscapeRoomsPass))).Url);
        Assert.Null((await Read<SyncView>(await other.PostAsJsonAsync("/api/billing/sync", new SyncRequest(CheckoutId(url))))).Paid); // not theirs
        Assert.Equal((false, true), ((await AccessAsync(other)).Mysteries, (await AccessAsync(other)).EscapeRooms));
        Assert.Single(await PaidGrantsAsync(otherId));
    }

    // ---------------------------------------------------------------- webhooks

    [Fact]
    public async Task A_forged_stale_or_unsigned_webhook_is_refused_and_a_repeat_changes_nothing()
    {
        var (host, id) = await HostAsync();
        await SubscribeAsync(host);
        var sub = app.Fake.SubscriptionsOf(await CustomerOfAsync(id)).Single();

        // Someone who knows the address but not the secret tries to cancel a stranger's plan.
        var forged = app.Fake.Change(sub.Id, _ => { });
        var payload = forged.Payload.Replace("\"active\"", "\"canceled\"");
        Assert.Equal(HttpStatusCode.BadRequest, (await SendWebhookAsync(payload, forged.Signature)).StatusCode); // the body was changed after signing
        Assert.Equal(HttpStatusCode.BadRequest, (await SendWebhookAsync(payload, FakeBilling.Sign(payload, DateTimeOffset.UtcNow, "whsec_guess"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendWebhookAsync(payload, null)).StatusCode);
        // A real one copied and replayed ten minutes later is too old.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendWebhookAsync(forged.Payload, FakeBilling.Sign(forged.Payload, DateTimeOffset.UtcNow.AddMinutes(-10)))).StatusCode);

        // The genuine event, delivered twice: handled once.
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(forged)).StatusCode);
        var before = await PaidGrantsAsync(id);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(forged)).StatusCode);
        using var scope = app.Services.CreateScope();
        var eventId = JsonNode.Parse(forged.Payload)!["id"]!.GetValue<string>();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().BillingEvents.CountAsync(e => e.Id == eventId));
        Assert.Equivalent(before, await PaidGrantsAsync(id));
        Assert.Equal(AccessPlan.Subscription, (await AccessAsync(host)).Plan);
    }

    [Fact]
    public async Task Webhooks_that_arrive_out_of_order_still_end_in_the_providers_state()
    {
        var (host, id) = await HostAsync();
        await SubscribeAsync(host);
        var sub = app.Fake.SubscriptionsOf(await CustomerOfAsync(id)).Single();

        // "Updated: active" is sent, then the subscription is cancelled at once and "deleted" is sent. Stripe delivers
        // the deletion first and the old update after it.
        var stale = app.Fake.Change(sub.Id, _ => { });
        var deleted = app.Fake.Change(sub.Id, s => s.Cancel(DateTimeOffset.UtcNow), "customer.subscription.deleted");
        await SendWebhookAsync(deleted);
        await SendWebhookAsync(stale);

        var access = await AccessAsync(host);
        Assert.Equal((false, false), (access.Mysteries, access.EscapeRooms));
        Assert.Equal(PaidStatus.Ended, (await PaidGrantsAsync(id)).Single().Status);
        // Over, so a new subscription may be bought.
        Assert.Equal(HttpStatusCode.OK, (await BuyAsync(host, BillingPlan.BothMonthly)).StatusCode);
    }

    [Fact]
    public async Task Cancelling_on_the_billing_page_keeps_the_games_until_the_paid_period_ends()
    {
        var (host, id) = await HostAsync();
        await SubscribeAsync(host);
        var customer = await CustomerOfAsync(id);
        var sub = app.Fake.SubscriptionsOf(customer).Single();

        // The fake billing page's Cancel button, as Stripe's: at the end of the period.
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync($"/api/billing/fake/portal/{customer}/cancel/{sub.Id}", null)).StatusCode);

        var access = await AccessAsync(host);
        Assert.Equal((AccessPlan.Subscription, PaidStatus.Ending, sub.PeriodEnd, (DateTimeOffset?)null), (access.Plan, access.Status, access.EndsAt, access.RenewsAt));
        Assert.Equal(HttpStatusCode.OK, (await StartEscapeAsync(host)).StatusCode);
        var grant = (await PaidGrantsAsync(id)).Single();
        Assert.False(Access.InEffect(grant, sub.PeriodEnd.AddSeconds(1)));
        // Still theirs until then, so it's changed (renewed) on the billing page rather than bought again.
        Assert.Contains("You already have a subscription", await ProblemAsync(await BuyAsync(host, BillingPlan.BothYearly), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task A_failed_renewal_keeps_the_games_for_the_grace_days_then_stops_until_the_card_works()
    {
        var (host, id) = await HostAsync();
        await SubscribeAsync(host);
        var sub = app.Fake.SubscriptionsOf(await CustomerOfAsync(id)).Single();

        // Renewal day: the new period starts and the card is declined.
        await SendWebhookAsync(app.Fake.Change(sub.Id, s => { s.PeriodEnd = DateTimeOffset.UtcNow; s.Renew(); s.Status = "past_due"; }, "invoice.payment_failed"));
        var access = await AccessAsync(host);
        Assert.Equal((AccessPlan.Subscription, PaidStatus.PastDue, true), (access.Plan, access.Status, access.EscapeRooms));
        Assert.Equal(sub.PeriodStart.AddDays(7), access.EndsAt);
        Assert.Contains("Update your card", await ProblemAsync(await BuyAsync(host, BillingPlan.BothMonthly), HttpStatusCode.BadRequest));

        // Stripe gives up on the card.
        await SendWebhookAsync(app.Fake.Change(sub.Id, s => s.Status = "unpaid"));
        Assert.False((await AccessAsync(host)).EscapeRooms);

        // A new card pays the invoice.
        await SendWebhookAsync(app.Fake.Change(sub.Id, s => s.Status = "active", "invoice.paid"));
        access = await AccessAsync(host);
        Assert.Equal((PaidStatus.Active, true, sub.PeriodEnd), (access.Status, access.EscapeRooms, access.RenewsAt));
    }

    [Fact]
    public async Task A_renewal_whose_webhook_never_came_is_checked_when_the_host_needs_it()
    {
        var (host, id) = await HostAsync();
        await SubscribeAsync(host);
        var sub = app.Fake.SubscriptionsOf(await CustomerOfAsync(id)).Single();

        // The renewal went through at Stripe, but its webhook was lost, and our grant has run out meanwhile.
        app.Fake.Change(sub.Id, s => s.Renew());
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AccessGrants.Where(g => g.ExternalId == sub.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.RenewsAt, DateTimeOffset.UtcNow.AddDays(-3)).SetProperty(g => g.EndsAt, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.False((await AccessAsync(host)).EscapeRooms);

        // Starting a game asks Stripe before saying no.
        Assert.Equal(HttpStatusCode.OK, (await StartEscapeAsync(host)).StatusCode);
        Assert.Equal(sub.PeriodEnd, (await AccessAsync(host)).RenewsAt);
    }

    // ---------------------------------------------------------------- leaving, and the admin

    [Fact]
    public async Task Deleting_an_account_cancels_its_subscription_first()
    {
        var (host, id) = await HostAsync();
        await SubscribeAsync(host);
        var customer = await CustomerOfAsync(id);

        var res = await host.PostAsJsonAsync("/api/account/delete", new DeleteAccountRequest("password123"));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("canceled", app.Fake.SubscriptionsOf(customer).Single().Status);
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AccessGrants.AnyAsync(g => g.UserId == id));
    }

    [Fact]
    public async Task The_admin_sees_the_plans_what_is_wrong_with_them_and_every_payment()
    {
        var (host, id) = await HostAsync();
        await SubscribeAsync(host);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/admin/billing")).StatusCode);

        var admin = await app.AdminAsync();
        var view = await Read<AdminBillingView>(await admin.GetAsync("/api/admin/billing?refresh=true"));
        Assert.Equal(("Fake", false, true, 72, 7), (view.Provider, view.LiveMode, view.WebhooksVerified, view.PassHours, view.GraceDays));
        var wrong = view.Plans.Single(p => p.Id == BillingPlan.EscapeRoomsMonthly);
        Assert.False(wrong.ForSale);
        Assert.Contains("one-time price", wrong.Problem);
        Assert.True(view.Plans.Single(p => p.Id == BillingPlan.BothMonthly).ForSale);
        Assert.NotNull(view.LastEvent);
        var row = view.Paid.First(p => p.UserId == id);
        Assert.Equal((GrantKind.Subscription, PaidStatus.Active, true, "Hana"), (row.Kind, row.Status, row.InEffect, row.DisplayName));

        // "Sync" reads a host's subscriptions from the provider, for when a webhook went missing.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/billing/hosts/{id}/sync", null)).StatusCode);
        var (stranger, strangerId) = await HostAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/billing/hosts/{strangerId}/sync", null)).StatusCode);
    }
}

/// <summary>A server without payments: nothing changes for hosts, and the webhook isn't there (#101).</summary>
public class PaymentsOffTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Without_a_payment_provider_nothing_is_for_sale()
    {
        var (host, _) = await app.RegisterHostAsync($"off{Guid.NewGuid():N}@example.com");
        var view = GameJson.Deserialize<BillingView>(await host.GetStringAsync("/api/billing"));
        Assert.False(view.Enabled);
        Assert.Empty(view.Plans);
        Assert.False(GameJson.Deserialize<MeResponse>(await host.GetStringAsync("/api/auth/me")).Access.Payments);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsJsonAsync("/api/billing/checkout", new BuyRequest(BillingPlan.BothMonthly), GameJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().PostAsync("/api/billing/webhook", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public void The_fake_provider_never_runs_in_production_where_anyone_could_pay_with_it()
    {
        BillingSetup Setup(string environment, BillingOptions options) => new(Microsoft.Extensions.Options.Options.Create(options),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment }, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BillingSetup>.Instance);

        var production = Setup("Production", new BillingOptions { Provider = "Fake" });
        Assert.Null(production.Provider);
        Assert.Contains("only for tests", production.Problem);
        Assert.NotNull(Setup("Development", new BillingOptions { Provider = "Fake" }).Fake);

        // Stripe is chosen by its key alone, and asking for it without one is reported, not ignored.
        Assert.Equal("Stripe", Setup("Production", new BillingOptions { Stripe = { SecretKey = "sk_test_123" } }).Provider?.Name);
        Assert.Contains("secret key", Setup("Production", new BillingOptions { Provider = "Stripe" }).Problem);
        Assert.Contains("no payment provider called 'Paypal'", Setup("Production", new BillingOptions { Provider = "Paypal" }).Problem);
        Assert.False(Setup("Production", new BillingOptions()).Enabled);
    }
}
