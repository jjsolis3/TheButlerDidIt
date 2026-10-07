using System.Collections.Concurrent;
using ButlerDidIt.Api.Scale;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Billing;

/// <summary>
/// Which payment provider the site uses, decided once at start-up from the settings (#101). With none, payments are
/// off: nothing is for sale, and hosts get their games from trials and the admin, as before.
/// </summary>
public sealed class BillingSetup
{
    public BillingSetup(IOptions<BillingOptions> options, IHostEnvironment environment, TimeProvider clock, ILogger<BillingSetup> log)
    {
        Options = options.Value;
        var stripeKey = Options.Stripe.SecretKey;
        var kind = string.IsNullOrWhiteSpace(Options.Provider) ? (string.IsNullOrWhiteSpace(stripeKey) ? null : "Stripe") : Options.Provider.Trim();
        if (kind is null) return;

        if (kind.Equals("Stripe", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(stripeKey)) Problem = "Payments are set to Stripe, but the secret key (Billing__Stripe__SecretKey) isn't set.";
            else Provider = new StripeBilling(Options.Stripe);
        }
        else if (kind.Equals("Fake", StringComparison.OrdinalIgnoreCase))
        {
            // Anyone could "pay" with the fake one, so it never runs where real hosts are.
            if (environment.IsProduction()) Problem = "The fake payment provider is only for tests, so it's off in Production. Use Stripe.";
            else Provider = new FakeBilling(clock);
        }
        else
        {
            Problem = $"There's no payment provider called '{kind}'. Use Stripe.";
        }

        if (Problem is not null) log.LogError("Payments are off: {Problem}", Problem);
        else if (Provider is { CanVerifyWebhooks: false })
            log.LogWarning("Payments: the webhook signing secret (Billing__Stripe__WebhookSecret) isn't set, so payments won't reach hosts' accounts until it is.");
    }

    public BillingOptions Options { get; }

    /// <summary>The provider, or null when payments are off.</summary>
    public IBillingProvider? Provider { get; }

    /// <summary>The fake provider, when that's the one in use (tests drive it).</summary>
    public FakeBilling? Fake => Provider as FakeBilling;

    /// <summary>Why payments are off although they were asked for; null when they're on, or simply not set up.</summary>
    public string? Problem { get; }

    public bool Enabled => Provider is not null;
}

/// <summary>
/// One piece of billing work per customer at a time: on this server (a gate per key) and across servers (an advisory
/// lock in Postgres, with Scale:MultiInstance). Stripe often sends two webhooks about the same customer within
/// milliseconds (the checkout, and the subscription it made), and both would otherwise add the same grant.
/// </summary>
public sealed class BillingLocks(ClusterLock cluster)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public async Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return new Releaser(gate, await cluster.AcquireAsync($"billing:{key}", ct));
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Releaser(SemaphoreSlim gate, IAsyncDisposable shared) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await shared.DisposeAsync();
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
