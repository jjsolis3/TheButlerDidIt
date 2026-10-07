using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Legal;
using ButlerDidIt.Api.Monitoring;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace ButlerDidIt.Api.Tests;

public class AlertRuleTests
{
    [Theory]
    [InlineData(6, 0, true)]   // a new site: only the minimum counts
    [InlineData(4.99, 0, false)] // below the minimum, however it compares
    [InlineData(10, 21, true)] // a usual day was $3: $10 is more than three times that
    [InlineData(8, 21, false)] // ...and $8 isn't
    public void A_jump_is_over_the_minimum_and_well_above_a_usual_day(decimal lastDay, decimal weekBefore, bool jumped) =>
        Assert.Equal(jumped, AlertRules.Jumped(lastDay, weekBefore, minimum: 5, factor: 3));
}

/// <summary>A server that "sends" email into a list, with the hourly checks left to the test.</summary>
public sealed class AlertFactory : ApiFactory
{
    public CapturingEmailSender Email { get; } = new();

    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        ("App:PublicUrl", "https://mystery.example.com"),
        ("Alerts:IntervalMinutes", "0"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<ButlerDidIt.Api.Auth.IEmailSender>(Email));
    }
}

public class AlertTests(AlertFactory app) : IClassFixture<AlertFactory>
{
    [Fact]
    public async Task A_jump_in_AI_spending_or_failed_payments_is_emailed_to_the_admin_once_and_shown_on_the_overview()
    {
        var (admin, _) = await app.RegisterHostAsync("owner@example.com");
        var now = DateTimeOffset.UtcNow;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A usual day costs $1; the last 24 hours cost $6, which a leaked key or a runaway host would look like.
            for (var day = 2; day <= 8; day++) db.AiUsage.Add(Usage(now.AddDays(-day).AddHours(1), 1m));
            db.AiUsage.Add(Usage(now.AddHours(-3), 6m));
            // Four cards failed today, none the week before.
            for (var i = 0; i < 4; i++)
                db.BillingEvents.Add(new BillingEventEntity { Id = $"evt_{Guid.NewGuid():N}", Type = "invoice.payment_failed", ReceivedAt = now.AddHours(-i - 1) });
            await db.SaveChangesAsync();
        }

        var worker = app.Services.GetRequiredService<AlertWorker>();
        var raised = await worker.RunOnceAsync(default, now);
        Assert.Equal(new[] { AlertRules.AiSpend, AlertRules.FailedPayments }, raised.Select(a => a.Kind));
        Assert.Contains("$6.00 in the last 24 hours, against $1.00 on a usual day", raised[0].Message);
        Assert.Contains("4 payments failed in the last 24 hours, against 0 on a usual day", raised[1].Message);

        bool IsAlert((string To, string Subject, string Text) m) => m.Subject.StartsWith("The Butler Did It:", StringComparison.Ordinal);
        var mail = Assert.Single(app.Email.Sent, IsAlert);
        Assert.Equal("owner@example.com", mail.To);
        Assert.Equal("The Butler Did It: 2 things to look at", mail.Subject);
        Assert.Contains("AI spending jumped", mail.Text);

        // Once a day at most: the next hour's check says nothing new.
        Assert.Empty(await worker.RunOnceAsync(default, now.AddHours(1)));
        Assert.Single(app.Email.Sent, IsAlert);

        var overview = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/overview")).RootElement;
        Assert.Equal(2, overview.GetProperty("alerts").GetArrayLength());

        // A day and a bit later, with nothing new, all is quiet.
        Assert.Empty(await worker.RunOnceAsync(default, now.AddHours(26)));
    }

    private static AiUsageEntity Usage(DateTimeOffset at, decimal cost) => new()
    {
        At = at, Role = AiRole.Storyteller, ProviderName = "Claude", Model = "claude", CostUsd = cost, PriceKnown = true, Success = true,
        HostUserId = "someone", Purpose = "story",
    };
}

/// <summary>
/// OpenTelemetry switched on, as by OTEL_EXPORTER_OTLP_ENDPOINT. Nothing listens there (the exporter quietly fails),
/// so the traces are also kept in a list, to see exactly what would be sent.
/// </summary>
public sealed class TelemetryFactory : ApiFactory
{
    public List<Activity> Traces { get; } = [];

    // As docker-compose.yml passes them when only the endpoint is filled in.
    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        (Telemetry.EndpointSetting, "http://127.0.0.1:1"),
        ("OTEL_EXPORTER_OTLP_HEADERS", ""),
        ("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc"),
        ("OTEL_SERVICE_NAME", "butler-did-it"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(Traces)));
    }
}

public class TelemetryTests(TelemetryFactory app) : IClassFixture<TelemetryFactory>
{
    [Fact]
    public async Task Requests_are_traced_without_who_made_them_and_uptime_checks_are_left_out()
    {
        var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/themes");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone)");
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();
        // A request that fails is recorded as an error, which is what error tracking is for.
        Assert.False((await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@example.com", "wrong-password"))).IsSuccessStatusCode);
        app.Services.GetRequiredService<TracerProvider>().ForceFlush();

        Activity[] traces;
        lock (app.Traces) traces = [.. app.Traces];
        var themes = Assert.Single(traces, a => a.GetTagItem("url.path") as string == "/api/themes");
        Assert.Equal("GET /api/themes", themes.DisplayName);
        Assert.Null(themes.GetTagItem("client.address"));
        Assert.Null(themes.GetTagItem("user_agent.original"));
        Assert.Contains(app.Services.GetRequiredService<TracerProvider>().GetResource().Attributes, a => a.Key == "service.name" && (string)a.Value == "butler-did-it");
        Assert.DoesNotContain(traces, a => a.GetTagItem("url.path") as string == "/healthz");
        // The database work inside a request is traced too (Npgsql), its SQL without the values.
        Assert.Contains(traces, a => a.Source.Name == "Npgsql");

        // The privacy policy lists the monitoring service as soon as it's on.
        var privacy = GameJson.Deserialize<LegalPageView>(await client.GetStringAsync("/api/legal/privacy")).Markdown;
        Assert.Contains("- **Our monitoring service** receives error reports and timings", privacy);
    }
}
