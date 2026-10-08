using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ButlerDidIt.Api.Monitoring;

/// <summary>
/// Error tracking and performance data with OpenTelemetry (#103): traces (each request, with the database queries and
/// calls to AI providers and Stripe inside it), metrics (request rates and timings, memory, garbage collection) and the
/// app's own logs, errors included, all sent to one OTLP endpoint.
///
/// OpenTelemetry is a vendor-neutral standard, so any service that speaks OTLP works (Grafana Cloud, Honeycomb, Axiom,
/// SigNoz, Uptrace, a self-hosted collector…), and switching is a setting, not code. It's off unless
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; the exporter reads the standard variables itself
/// (<c>OTEL_EXPORTER_OTLP_HEADERS</c> for the service's key, <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, <c>OTEL_SERVICE_NAME</c>).
///
/// Personal details stay out, as the privacy policy says: no IP addresses or browser details on requests, and no
/// query strings on calls to other services (an API key can travel in one). The app's logs already never name anyone
/// (see SmtpEmailSender).
/// </summary>
public static class Telemetry
{
    /// <summary>The setting that switches it on: where to send everything.</summary>
    public const string EndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static bool Enabled(IConfiguration config) => !string.IsNullOrWhiteSpace(config[EndpointSetting]);

    /// <summary>The running build, e.g. "1.0.0+d1c6b37…", so errors can be matched to the code that made them.</summary>
    public static string Version { get; } =
        typeof(Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static void AddTelemetry(this WebApplicationBuilder builder)
    {
        if (!Enabled(builder.Configuration)) return;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "butler-did-it", serviceVersion: Version))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                {
                    // The uptime checker calls /healthz every minute or so: thousands of traces a day that say nothing.
                    o.Filter = http => !http.Request.Path.StartsWithSegments("/healthz");
                    o.RecordException = true;
                    o.EnrichWithHttpRequest = (activity, _) => Anonymise(activity);
                })
                .AddHttpClientInstrumentation(o =>
                {
                    o.RecordException = true;
                    // Only where the call went, never its query string.
                    o.EnrichWithHttpRequestMessage = (activity, request) =>
                    {
                        if (request.RequestUri is { } uri) activity.SetTag("url.full", uri.GetLeftPart(UriPartial.Path));
                    };
                })
                // Npgsql reports each database command (its SQL, never the values) under this name.
                .AddSource("Npgsql"))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql"))
            .WithLogging()
            .UseOtlpExporter();
    }

    /// <summary>Takes the visitor's IP address and browser details off a request's trace.</summary>
    private static void Anonymise(Activity activity)
    {
        activity.SetTag("client.address", null);
        activity.SetTag("user_agent.original", null);
    }
}
