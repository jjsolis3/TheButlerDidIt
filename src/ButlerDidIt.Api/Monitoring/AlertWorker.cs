using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Scale;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Monitoring;

/// <summary>When the admin is told something jumped (#103). The defaults suit a small site; raise them as it grows.</summary>
public sealed class AlertOptions
{
    /// <summary>How often the checks run. 0 switches the alerts off.</summary>
    public double IntervalMinutes { get; set; } = 60;

    /// <summary>AI spending in the last 24 hours below this is never worth an alert, however it compares.</summary>
    public decimal AiSpendMinUsd { get; set; } = 5;

    /// <summary>How many times a usual day's AI spending (the average over the week before) counts as a jump.</summary>
    public double AiSpendFactor { get; set; } = 3;

    /// <summary>Fewer failed payments than this in the last 24 hours are never worth an alert.</summary>
    public int FailedPaymentsMin { get; set; } = 3;

    /// <summary>How many times a usual day's failed payments counts as a jump.</summary>
    public double FailedPaymentsFactor { get; set; } = 3;

    /// <summary>Where alerts are emailed. Empty: every admin account's address.</summary>
    public string Email { get; set; } = "";
}

/// <summary>The rule for "jumped", on its own so it can be tested without a database.</summary>
public static class AlertRules
{
    public const string AiSpend = "ai-spend";
    public const string FailedPayments = "failed-payments";

    /// <summary>
    /// The last 24 hours' amount is at least <paramref name="minimum"/>, and more than <paramref name="factor"/> times a
    /// usual day's, which is the average over the 7 days before. A new site, with no usual day yet, only needs the minimum.
    /// </summary>
    public static bool Jumped(decimal lastDay, decimal weekBefore, decimal minimum, double factor) =>
        lastDay >= minimum && lastDay > (decimal)factor * (weekBefore / 7);
}

/// <summary>
/// Checks every hour whether AI spending or failed payments jumped (#103), and tells the admin by email and on the
/// admin hub's overview. A jump is measured against the site's own usual day, so a growing site isn't alerted all the
/// time, and each kind of alert is raised at most once a day.
///
/// The error tracking itself is OpenTelemetry's (see <see cref="Telemetry"/>); these two are business signals a
/// monitoring service doesn't know about: a leaked AI key or a runaway host, and cards suddenly failing.
/// </summary>
public sealed class AlertWorker(IServiceScopeFactory scopes, IOptions<AlertOptions> options, ClusterLock cluster, TimeProvider clock, ILogger<AlertWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.IntervalMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Alert checks failed");
            }
        }
    }

    /// <summary>Runs the checks once and returns the alerts raised.</summary>
    /// <param name="now">Overridable so tests can choose the moment.</param>
    public async Task<IReadOnlyList<AlertEntity>> RunOnceAsync(CancellationToken ct, DateTimeOffset? now = null)
    {
        // With several servers, one check (and one email) is enough.
        await using var turn = await cluster.TryAcquireAsync("alerts", ct);
        if (turn is null) return [];

        var at = now ?? clock.GetUtcNow();
        var (dayAgo, weekBefore) = (at.AddDays(-1), at.AddDays(-8));
        var o = options.Value;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var raised = new List<AlertEntity>();

        var usage = db.AiUsage.AsNoTracking();
        var spentDay = await usage.Where(u => u.At > dayAgo && u.At <= at).SumAsync(u => u.CostUsd, ct);
        var spentWeek = await usage.Where(u => u.At > weekBefore && u.At <= dayAgo).SumAsync(u => u.CostUsd, ct);
        if (AlertRules.Jumped(spentDay, spentWeek, o.AiSpendMinUsd, o.AiSpendFactor))
            raised.AddRange(await RaiseAsync(db, AlertRules.AiSpend, at,
                $"AI spending jumped: ${spentDay:0.00} in the last 24 hours, against ${spentWeek / 7:0.00} on a usual day the week before. " +
                "Admin hub → AI shows which hosts and roles it went to.", ct));

        // Stripe sends invoice.payment_failed for each failed attempt at a renewal (#101), so these are failed card charges.
        var failures = db.BillingEvents.AsNoTracking().Where(e => e.Type == "invoice.payment_failed");
        var failedDay = await failures.CountAsync(e => e.ReceivedAt > dayAgo && e.ReceivedAt <= at, ct);
        var failedWeek = await failures.CountAsync(e => e.ReceivedAt > weekBefore && e.ReceivedAt <= dayAgo, ct);
        if (AlertRules.Jumped(failedDay, failedWeek, o.FailedPaymentsMin, o.FailedPaymentsFactor))
            raised.AddRange(await RaiseAsync(db, AlertRules.FailedPayments, at,
                $"{failedDay} payments failed in the last 24 hours, against {failedWeek / 7.0:0.#} on a usual day the week before. " +
                "Stripe's dashboard (Payments → Failed) says why; Admin hub → Plans & billing shows whose.", ct));

        if (raised.Count > 0) await EmailAsync(scope.ServiceProvider, db, raised, ct);
        return raised;
    }

    /// <summary>Records the alert, unless one of its kind was raised in the last day.</summary>
    private async Task<IEnumerable<AlertEntity>> RaiseAsync(AppDbContext db, string kind, DateTimeOffset at, string message, CancellationToken ct)
    {
        var dayAgo = at.AddDays(-1);
        if (await db.Alerts.AnyAsync(a => a.Kind == kind && a.RaisedAt > dayAgo, ct)) return [];
        var alert = new AlertEntity { Id = Guid.NewGuid(), Kind = kind, Message = message, RaisedAt = at };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);
        // A warning in the logs too, so the monitoring service can alert on it as well.
        log.LogWarning("Alert {Kind}: {Message}", kind, message);
        return [alert];
    }

    private async Task EmailAsync(IServiceProvider services, AppDbContext db, IReadOnlyList<AlertEntity> alerts, CancellationToken ct)
    {
        var email = services.GetRequiredService<IEmailSender>();
        if (!email.IsConfigured) return; // the overview still shows them
        var to = string.IsNullOrWhiteSpace(options.Value.Email)
            ? await db.Users.AsNoTracking().Where(u => u.IsAdmin && u.Email != null).Select(u => u.Email!).ToListAsync(ct)
            : [options.Value.Email.Trim()];
        var subject = alerts.Count == 1 ? $"The Butler Did It: {Title(alerts[0].Kind)}" : $"The Butler Did It: {alerts.Count} things to look at";
        var text = string.Join("\n\n", alerts.Select(a => a.Message)) + "\n\nThe admin hub's overview lists recent alerts.";
        foreach (var address in to)
        {
            try
            {
                await email.SendAsync(address, subject, text, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not email an alert");
            }
        }
    }

    private static string Title(string kind) => kind switch
    {
        AlertRules.AiSpend => "AI spending jumped",
        AlertRules.FailedPayments => "failed payments jumped",
        _ => "something to look at",
    };
}
