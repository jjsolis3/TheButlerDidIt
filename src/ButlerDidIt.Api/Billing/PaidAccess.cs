using ButlerDidIt.Api.Data;

namespace ButlerDidIt.Api.Billing;

/// <summary>A subscription turned into a grant's terms: what the host's pages say, and from when until when it counts.</summary>
/// <param name="EndsAt">When access ends; for a subscription that renews, a little after the renewal is due.</param>
/// <param name="RenewsAt">When it renews, for the host's page; null when it won't.</param>
public sealed record PaidTerms(PaidStatus Status, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, DateTimeOffset? RenewsAt)
{
    public bool GivesAccess(DateTimeOffset now) => Status != PaidStatus.Ended && (EndsAt is null || EndsAt > now);
}

/// <summary>
/// How a subscription at the payment provider becomes access to games (#101). Pure, so every case is tested without
/// a provider or a database. The subscription is always read fresh from the provider, never taken from a webhook, so
/// these rules see its state now, whatever order the webhooks came in.
/// </summary>
public static class PaidAccess
{
    /// <summary>
    /// How long past its renewal date a subscription keeps counting before the renewal is confirmed. Stripe renews at
    /// the end of the period, but the payment and its webhook can take an hour or so; without this, a host could lose
    /// their games for that hour every month. A missed webhook is covered too: the next one reads the renewal, and so
    /// does the host opening their account page or starting a game once the renewal is due
    /// (<see cref="BillingService.RefreshIfDueAsync"/>).
    /// </summary>
    public static readonly TimeSpan RenewalLeeway = TimeSpan.FromDays(2);

    public static PaidTerms ForSubscription(PaidSubscription sub, int graceDays, DateTimeOffset now)
    {
        switch (sub.Status)
        {
            case "active" or "trialing":
                // Cancelled at the end of the period: paid until then, and nothing after.
                if (sub.CancelAt is { } cancelAt) return new(PaidStatus.Ending, sub.StartedAt, cancelAt, null);
                var renews = sub.PeriodEnd ?? now;
                return new(sub.Status == "trialing" ? PaidStatus.Trialing : PaidStatus.Active, sub.StartedAt, renews + RenewalLeeway, renews);

            case "past_due":
                // The renewal's payment failed and the provider is retrying the card. Stripe starts the new period even
                // so, which is why the grace days count from the period's start (the day the renewal was due), not its end.
                return new(PaidStatus.PastDue, sub.StartedAt, (sub.PeriodStart ?? now).AddDays(graceDays), null);

            default:
                // canceled, unpaid (the provider gave up on the card), incomplete (the first payment never went through),
                // incomplete_expired and paused: no games.
                return new(PaidStatus.Ended, sub.StartedAt, sub.EndedAt ?? now, null);
        }
    }

    /// <summary>
    /// A subscription the host still has at the provider, even if it gives no games right now (a failed payment past
    /// its grace days). While they have one, they change it with Manage billing rather than buying a second.
    /// </summary>
    public static bool IsLiveSubscription(AccessGrantEntity g, DateTimeOffset now) =>
        g.Kind == GrantKind.Subscription && g.RevokedAt is null && g.Status switch
        {
            PaidStatus.Active or PaidStatus.Trialing or PaidStatus.PastDue => true,
            PaidStatus.Ending => g.EndsAt > now,
            _ => false,
        };
}
