using System.Net;
using System.Security.Claims;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Plans;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Billing;

public sealed record BuyRequest(BillingPlan Plan);
public sealed record SyncRequest(string? Session);

/// <summary>Where to send the browser next: the provider's payment or billing page.</summary>
public sealed record RedirectView(string Url);

/// <summary>The host after their plan was brought up to date.</summary>
/// <param name="Paid">Whether the checkout they came back from is paid; null when there was none, or it isn't theirs.</param>
public sealed record SyncView(MeResponse Me, bool? Paid);

/// <summary>The admin hub's Plans &amp; billing tab (#101).</summary>
/// <param name="Provider">"Stripe" or "Fake"; null when payments are off.</param>
/// <param name="WebhooksVerified">False when the webhook signing secret is missing, so no payment can reach an account.</param>
/// <param name="Problem">Why payments are off although they were asked for.</param>
/// <param name="EventsThisWeek">Webhooks handled in the last seven days.</param>
public sealed record AdminBillingView(
    bool Enabled,
    string? Provider,
    bool LiveMode,
    bool WebhooksVerified,
    string? Problem,
    int PassHours,
    int GraceDays,
    bool AutomaticTax,
    IReadOnlyList<AdminPlanRow> Plans,
    AdminBillingEventView? LastEvent,
    int EventsThisWeek,
    IReadOnlyList<AdminPaidRow> Paid);

/// <param name="Interval">"month", "year", or null for a one-time price, as Stripe has it.</param>
public sealed record AdminPlanRow(BillingPlan Id, PlanKind Kind, string PriceId, long? Amount, string? Currency, string? Interval, bool ForSale, string? Problem);

public sealed record AdminBillingEventView(string Type, DateTimeOffset At);

/// <summary>One subscription or pass, newest first.</summary>
/// <param name="DashboardUrl">The subscription (or, for a pass, the customer) in the provider's dashboard.</param>
public sealed record AdminPaidRow(
    string UserId,
    string? DisplayName,
    string? Email,
    GrantKind Kind,
    bool Mysteries,
    bool EscapeRooms,
    PaidStatus? Status,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? RenewsAt,
    bool InEffect,
    string? DashboardUrl);

/// <summary>
/// Payments (#101): what a host can buy, the way to the provider's payment and billing pages, the provider's
/// webhook, the admin's tab, and (with the fake provider only) the fake provider's own pages.
/// </summary>
public static class BillingEndpoints
{
    public const string RateLimit = "billing";

    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var host = app.MapGroup("/api/billing").RequireAuthorization(AuthPolicies.Host);

        host.MapGet("/", async (ClaimsPrincipal principal, UserManager<AppUser> users, BillingService billing, CancellationToken ct) =>
            await users.GetUserAsync(principal) is { } user ? Results.Ok(await billing.ViewAsync(user, ct)) : Results.Unauthorized());

        host.MapPost("/checkout", async (BuyRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, BillingService billing,
            IOptions<AppOptions> options, HttpRequest request, CancellationToken ct) =>
        {
            if (await users.GetUserAsync(principal) is not { } user) return Results.Unauthorized();
            try
            {
                return Results.Ok(new RedirectView(await billing.CheckoutAsync(user, req.Plan, SiteUrl(options, request), ct)));
            }
            catch (BillingException ex)
            {
                return Refused(ex);
            }
        }).AddEndpointFilter(AuthEndpoints.RequireConfirmedHost).RequireRateLimiting(RateLimit);

        host.MapPost("/portal", async (ClaimsPrincipal principal, UserManager<AppUser> users, BillingService billing,
            IOptions<AppOptions> options, HttpRequest request, CancellationToken ct) =>
        {
            if (await users.GetUserAsync(principal) is not { } user) return Results.Unauthorized();
            try
            {
                return Results.Ok(new RedirectView(await billing.PortalAsync(user, SiteUrl(options, request), ct)));
            }
            catch (BillingException ex)
            {
                return Refused(ex);
            }
        }).RequireRateLimiting(RateLimit);

        // Back from the payment page. The address it came back to proves nothing, so this only asks the provider.
        host.MapPost("/sync", async (SyncRequest req, ClaimsPrincipal principal, UserManager<AppUser> users, BillingService billing,
            HttpContext http, CancellationToken ct) =>
        {
            if (await users.GetUserAsync(principal) is not { } user) return Results.Unauthorized();
            bool? paid;
            try
            {
                paid = await billing.SyncForHostAsync(user, req.Session, ct);
            }
            catch (BillingException ex)
            {
                return Refused(ex);
            }
            return Results.Ok(new SyncView(await AuthEndpoints.ToMeAsync(user, http), paid));
        }).RequireRateLimiting(RateLimit);

        // ---- The provider's webhook. No sign-in (Stripe can't sign in): the signature is the proof, so the body is read
        // exactly as sent, before anything parses it. Any answer but 2xx makes Stripe send it again later.
        app.MapPost("/api/billing/webhook", async (HttpRequest request, BillingService billing, ILogger<BillingService> log, CancellationToken ct) =>
        {
            string payload;
            using (var reader = new StreamReader(request.Body)) payload = await reader.ReadToEndAsync(ct);
            try
            {
                var outcome = await billing.HandleWebhookAsync(payload, request.Headers["Stripe-Signature"].FirstOrDefault(), ct);
                return outcome == WebhookOutcome.NotSetUp ? Results.NotFound() : Results.Ok();
            }
            catch (BillingSignatureException ex)
            {
                log.LogWarning("Payments: refused a webhook: {Reason}", ex.Message);
                return Results.Problem("This webhook couldn't be verified.", statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // ---- The admin's tab.
        var admin = app.MapGroup("/api/admin/billing").RequireAuthorization(AuthPolicies.Host).AddEndpointFilter(AdminHostEndpoints.RequireAdmin);

        admin.MapGet("/", async (bool? refresh, AppDbContext db, BillingSetup setup, BillingCatalog catalog, TimeProvider clock, CancellationToken ct) =>
        {
            if (refresh == true) catalog.Forget();
            var now = clock.GetUtcNow();
            var plans = (await catalog.OffersAsync(ct)).Select(o => new AdminPlanRow(o.Plan.Id, o.Plan.Kind, o.PriceId, o.Price?.Amount, o.Price?.Currency,
                o.Price?.Interval, o.ForSale, o.Problem)).ToList();
            var weekAgo = now.AddDays(-7);
            var last = await db.BillingEvents.AsNoTracking().OrderByDescending(e => e.ReceivedAt).Select(e => new AdminBillingEventView(e.Type, e.ReceivedAt)).FirstOrDefaultAsync(ct);
            var grants = await db.AccessGrants.AsNoTracking().Where(g => g.ExternalId != null).OrderByDescending(g => g.CreatedAt).Take(200).ToListAsync(ct);
            var userIds = grants.Select(g => g.UserId).Distinct().ToList();
            var people = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName, u.Email, u.BillingCustomerId }).ToDictionaryAsync(u => u.Id, ct);
            var paid = grants.Select(g =>
            {
                var who = people.GetValueOrDefault(g.UserId);
                return new AdminPaidRow(g.UserId, who?.DisplayName, who?.Email, g.Kind, g.Games.HasFlag(GameAccess.Mysteries), g.Games.HasFlag(GameAccess.EscapeRooms),
                    g.Status, g.StartsAt, g.EndsAt, g.RenewsAt, Access.InEffect(g, now), setup.Provider?.DashboardUrl(g.ExternalId, who?.BillingCustomerId));
            }).ToList();
            var o = setup.Options;
            return Results.Ok(new AdminBillingView(setup.Enabled, setup.Provider?.Name, setup.Provider?.LiveMode ?? false, setup.Provider?.CanVerifyWebhooks ?? false,
                setup.Problem, o.PassHours, o.GraceDays, o.AutomaticTax, plans, last, await db.BillingEvents.CountAsync(e => e.ReceivedAt > weekAgo, ct), paid));
        });

        // When a webhook went missing: read one host's subscriptions from the provider now.
        admin.MapPost("/hosts/{id}/sync", async (string id, AppDbContext db, BillingService billing, CancellationToken ct) =>
        {
            var customer = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.BillingCustomerId).FirstOrDefaultAsync(ct);
            if (customer is null) return Results.Problem("This host hasn't bought anything.", statusCode: StatusCodes.Status404NotFound);
            try
            {
                await billing.SyncCustomerAsync(customer, ct);
            }
            catch (BillingException ex)
            {
                return Refused(ex);
            }
            return Results.NoContent();
        });

        if (app.ServiceProvider.GetRequiredService<BillingSetup>().Fake is not null) MapFakeProviderPages(app);
    }

    /// <summary>
    /// The site's own address, which the payment pages send the host back to. App:PublicUrl when it's set; otherwise
    /// the address this request came to, which is safe here: only the host who asked is sent there.
    /// </summary>
    private static string SiteUrl(IOptions<AppOptions> options, HttpRequest request) =>
        (options.Value.PublicUrl ?? $"{request.Scheme}://{request.Host}").TrimEnd('/');

    /// <summary>The provider failing is a 502 (its fault, not the host's); anything else is the host's request.</summary>
    private static IResult Refused(BillingException ex) =>
        Results.Problem(ex.Message, statusCode: ex.InnerException is null ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);

    // ---------------------------------------------------------------- the fake provider's pages

    /// <summary>
    /// What Stripe's payment page and billing page do, for the fake provider: buttons that pay, or cancel, and send the
    /// webhook Stripe would. Mapped only when the fake provider is on, which it never is in Production.
    /// </summary>
    private static void MapFakeProviderPages(IEndpointRouteBuilder app)
    {
        var fake = app.MapGroup("/api/billing/fake");

        fake.MapGet("/checkout/{id}", (string id, BillingSetup setup) =>
        {
            if (setup.Fake!.Checkout(id) is not { } r) return Results.NotFound();
            var price = FakeBilling.Price(r.PriceId);
            var how = price?.Interval is null ? "once" : $"a {price.Interval}";
            var what = $"{(price?.Amount ?? 0) / 100m:0.00} {price?.Currency.ToUpperInvariant()} {how}";
            var trial = r.TrialEnd is null ? "" : $" Free until {r.TrialEnd.Value:d MMM yyyy}.";
            return Page("Fake checkout", $"""
                <p>Paying for <code>{Enc(r.PriceId)}</code>: {Enc(what)}.{trial}</p>
                <p class="notice">{Enc(r.Notice ?? "")}</p>
                <form method="post" action="/api/billing/fake/checkout/{Enc(id)}/pay"><button type="submit">Pay</button></form>
                <p><a href="{Enc(r.CancelUrl)}">Cancel and go back</a></p>
                """);
        });

        fake.MapPost("/checkout/{id}/pay", async (string id, BillingSetup setup, BillingService billing, CancellationToken ct) =>
        {
            if (setup.Fake!.Checkout(id) is not { } r) return Results.NotFound();
            if (setup.Fake.Pay(id) is { } webhook) await billing.HandleWebhookAsync(webhook.Payload, webhook.Signature, ct);
            return Results.Redirect(r.SuccessUrl.Replace("{CHECKOUT_SESSION_ID}", id));
        });

        fake.MapGet("/portal/{customer}", (string customer, BillingSetup setup) =>
        {
            var fakeProvider = setup.Fake!;
            var rows = string.Concat(fakeProvider.SubscriptionsOf(customer).Select(s =>
            {
                var when = s.CancelAt is null ? $", renews {s.PeriodEnd:d MMM yyyy}" : $", ends {s.CancelAt.Value:d MMM yyyy}";
                var canCancel = s.Status is "active" or "trialing" && s.CancelAt is null;
                var cancel = canCancel
                    ? $"""<form method="post" action="/api/billing/fake/portal/{Enc(customer)}/cancel/{Enc(s.Id)}"><button type="submit">Cancel subscription</button></form>"""
                    : "";
                return $"<li><code>{Enc(s.Price.Id)}</code>: {Enc(s.Status)}{when}{cancel}</li>";
            }));
            return Page("Fake billing", $"""
                <ul>{(rows.Length == 0 ? "<li>No subscriptions.</li>" : rows)}</ul>
                <p><a href="{Enc(fakeProvider.PortalReturn(customer) ?? "/account")}">Back to the site</a></p>
                """);
        });

        // Cancelling on Stripe's billing page cancels at the end of the period: paid for until then.
        fake.MapPost("/portal/{customer}/cancel/{subscription}", async (string customer, string subscription, BillingSetup setup, BillingService billing, CancellationToken ct) =>
        {
            if (!setup.Fake!.SubscriptionsOf(customer).Any(s => s.Id == subscription)) return Results.NotFound();
            var webhook = setup.Fake.Change(subscription, s => s.CancelAt = s.PeriodEnd);
            await billing.HandleWebhookAsync(webhook.Payload, webhook.Signature, ct);
            return Results.Redirect($"/api/billing/fake/portal/{Uri.EscapeDataString(customer)}");
        });
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    // $$""" makes {{…}} the holes, so the CSS can keep its single braces.
    private static IResult Page(string title, string body) => Results.Content($$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{title}}</title><style>body { font: 16px system-ui; max-width: 32rem; margin: 3rem auto; padding: 0 1rem } button { font: inherit; padding: .5rem 1rem }</style></head>
        <body><h1>{{title}}</h1><p><em>Test payments: no money moves.</em></p>{{body}}</body></html>
        """, "text/html");
}
