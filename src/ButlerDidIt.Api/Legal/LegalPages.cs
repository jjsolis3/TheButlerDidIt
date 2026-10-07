using System.Text.RegularExpressions;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Billing;
using ButlerDidIt.Api.Content;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Api.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Legal;

/// <summary>
/// Who runs the site, for its terms, privacy and refund pages (#103). Set these before charging anyone: the admin
/// hub's overview says when they're missing.
/// </summary>
public sealed class LegalOptions
{
    /// <summary>The person or business that runs the site, as the terms name it: "Jane Doe" or "Butler Games LLC".</summary>
    public string OperatorName { get; set; } = "";

    /// <summary>Where people write about their account, their information or a refund.</summary>
    public string ContactEmail { get; set; } = "";

    /// <summary>The US state whose law governs the terms, usually the one the operator is based in.</summary>
    public string State { get; set; } = "";

    /// <summary>
    /// Where the pages' Markdown files are. Empty: <c>legal</c> in the content folder, which is part of the app's image.
    /// Point it at a mounted folder to edit the pages on the server without rebuilding.
    /// </summary>
    public string Folder { get; set; } = "";

    /// <summary>How many days after a payment an unused plan is refunded in full.</summary>
    public int RefundDays { get; set; } = 14;

    /// <summary>How long database backups are kept: docker-compose.yml keeps monthly ones for 6 months.</summary>
    public int BackupMonths { get; set; } = 6;

    /// <summary>The company the server runs at, e.g. "Hetzner", for the privacy policy's list. Empty: "Our hosting provider".</summary>
    public string HostingProvider { get; set; } = "";

    /// <summary>The company that sends the site's email, e.g. "Postmark". Empty: "Our email service".</summary>
    public string EmailProvider { get; set; } = "";

    /// <summary>The company that stores uploads when Media:Storage is S3, e.g. "Cloudflare R2". Empty: "Our file storage service".</summary>
    public string StorageProvider { get; set; } = "";

    /// <summary>The company that receives error reports and timings (#77), e.g. "Grafana Cloud". Empty: "Our monitoring service".</summary>
    public string MonitoringProvider { get; set; } = "";
}

/// <param name="Page">"terms", "privacy" or "refunds": the page's address on the site.</param>
/// <param name="Markdown">The page, with the owner's notes taken out and the {{…}} words filled in.</param>
/// <param name="Draft">Still the starter draft, so the admin is reminded to have it reviewed.</param>
public sealed record LegalPageView(string Page, string Title, string Markdown, bool Draft);

/// <summary>
/// The terms, privacy and refund pages (#103). Each is a Markdown file in <c>content/legal</c>, so the site's owner
/// edits the words without touching code, and the server fills in the details that must match how the site really
/// works: the owner's name and contact, the trial and pass lengths, how long things are kept, and the outside
/// services this site actually uses. A page that says one thing while the settings do another is how policies go
/// wrong.
///
/// The owner's notes in a file are HTML comments (&lt;!-- … --&gt;), removed here before anything reaches a browser.
/// A file still starting with the starter-draft line hasn't been reviewed yet.
/// </summary>
public sealed partial class LegalPages(
    IOptions<LegalOptions> options,
    IOptions<ContentOptions> content,
    IWebHostEnvironment env,
    IOptions<PlansOptions> plans,
    IOptions<BillingOptions> payments,
    IOptions<RetentionOptions> retention,
    IOptions<MediaOptions> media,
    IEmailSender email,
    IHumanCheck human,
    BillingSetup billing,
    IConfiguration config)
{
    public const string SiteName = "The Butler Did It";

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        ["terms"] = "Terms of Service",
        ["privacy"] = "Privacy Policy",
        ["refunds"] = "Refund Policy",
    };

    /// <summary>The first line of each starter draft. Deleting it tells the site the page has been reviewed.</summary>
    public const string DraftMarker = "<!-- starter draft";

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();

    private string PathOf(string page) => Path.Combine(
        string.IsNullOrWhiteSpace(options.Value.Folder) ? Path.Combine(env.ContentRootPath, content.Value.Root, "legal") : Path.Combine(env.ContentRootPath, options.Value.Folder),
        page + ".md");

    /// <summary>One page, ready to show; null for a page that doesn't exist.</summary>
    /// <param name="siteUrl">The site's address, as the pages name it.</param>
    public async Task<LegalPageView?> PageAsync(string page, AppDbContext db, string siteUrl, CancellationToken ct)
    {
        if (!Titles.TryGetValue(page, out var title) || !File.Exists(PathOf(page))) return null;
        var text = await File.ReadAllTextAsync(PathOf(page), ct);
        var values = await ValuesAsync(db, siteUrl, ct);
        // An unknown {{word}} is left as it is, so a typo shows on the page instead of quietly vanishing.
        var markdown = Placeholder().Replace(Comment().Replace(text, ""), m => values.GetValueOrDefault(m.Groups[1].Value) ?? m.Value);
        return new LegalPageView(page, title, markdown.Trim(), text.TrimStart().StartsWith(DraftMarker, StringComparison.Ordinal));
    }

    /// <summary>What the admin still has to do before the pages are ready to show customers, or null when nothing is left.</summary>
    public string? Problem()
    {
        var o = options.Value;
        var missing = new[] { ("Legal__OperatorName", o.OperatorName), ("Legal__ContactEmail", o.ContactEmail), ("Legal__State", o.State) }
            .Where(s => string.IsNullOrWhiteSpace(s.Item2)).Select(s => s.Item1).ToList();
        var absent = Titles.Keys.Where(p => !File.Exists(PathOf(p))).Select(p => Titles[p]).ToList();
        var drafts = Titles.Keys.Where(p => File.Exists(PathOf(p)) && File.ReadLines(PathOf(p)).FirstOrDefault()?.TrimStart().StartsWith(DraftMarker, StringComparison.Ordinal) == true)
            .Select(p => Titles[p]).ToList();
        var problems = new List<string>();
        if (absent.Count > 0) problems.Add($"The {Join(absent)} {(absent.Count == 1 ? "is" : "are")} missing from the legal folder (content/legal, or Legal__Folder).");
        if (missing.Count > 0) problems.Add($"Set {Join(missing)}, which the pages name.");
        if (drafts.Count > 0) problems.Add($"{Join(drafts)} {(drafts.Count == 1 ? "is" : "are")} still the starter draft: have {(drafts.Count == 1 ? "it" : "them")} reviewed, then delete the first line of {(drafts.Count == 1 ? "its file" : "each file")} in the legal folder (content/legal, or Legal__Folder).");
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private async Task<Dictionary<string, string>> ValuesAsync(AppDbContext db, string siteUrl, CancellationToken ct)
    {
        var o = options.Value;
        return new Dictionary<string, string>
        {
            ["SiteName"] = SiteName,
            ["SiteUrl"] = Uri.TryCreate(siteUrl, UriKind.Absolute, out var url) ? url.Authority : siteUrl,
            // Not set yet: say so plainly rather than hide it. The admin hub's overview asks for them too.
            ["Operator"] = Or(o.OperatorName, "[the site's owner: name not set yet]"),
            ["ContactEmail"] = string.IsNullOrWhiteSpace(o.ContactEmail) ? "[contact email not set yet]" : $"[{o.ContactEmail}](mailto:{o.ContactEmail})",
            ["State"] = Or(o.State, "[state not set yet]"),
            ["RefundDays"] = o.RefundDays.ToString(),
            ["BackupMonths"] = o.BackupMonths.ToString(),
            ["TrialDays"] = plans.Value.TrialDays.ToString(),
            ["PassHours"] = payments.Value.PassHours.ToString(),
            ["GraceDays"] = payments.Value.GraceDays.ToString(),
            ["IdlePartyDays"] = retention.Value.IdlePartyDays.ToString(),
            ["FinishedPartyDays"] = retention.Value.FinishedPartyDays.ToString(),
            ["ServiceProviders"] = await ProvidersAsync(db, ct),
        };
    }

    /// <summary>
    /// The outside services this site uses, as a Markdown list, read from its own settings: the AI providers given a
    /// role under Admin → AI, Stripe when payments are on, and the hosting, email, storage and monitoring services.
    /// </summary>
    private async Task<string> ProvidersAsync(AppDbContext db, CancellationToken ct)
    {
        var o = options.Value;
        var lines = new List<string> { $"**{Or(o.HostingProvider, "Our hosting provider")}** runs the server and the database that hold everything this policy describes." };
        if (string.Equals(media.Value.Storage, "S3", StringComparison.OrdinalIgnoreCase))
            lines.Add($"**{Or(o.StorageProvider, "Our file storage service")}** stores uploaded pictures, sounds and videos, and costume selfies.");
        if (email.IsConfigured)
            lines.Add($"**{Or(o.EmailProvider, "Our email service")}** delivers account emails. It receives your email address and the message.");
        if (human.SiteKey is not null)
            lines.Add("**Cloudflare Turnstile** checks that the sign-up form is filled in by a person. While you sign up, it sees your browser's details and IP address.");
        if (billing.Provider is { Name: "Stripe" })
            lines.Add("**Stripe** takes payments. It receives what you enter on its payment page, and tells us whether you paid, your plan and its dates. See [Stripe's privacy policy](https://stripe.com/privacy).");

        var kinds = await db.AiRoles.AsNoTracking().Select(r => r.Provider!.Kind).Distinct().ToListAsync(ct);
        var companies = kinds.Select(Company).OfType<string>().Distinct().Order().ToList();
        if (companies.Count > 0)
            lines.Add($"**{Join(companies)}** {(companies.Count == 1 ? "powers" : "power")} the AI features. \"AI features\" below says what {(companies.Count == 1 ? "it receives" : "they receive")}.");
        if (kinds.Any(k => k is AiProviderKind.Ollama or AiProviderKind.Piper or AiProviderKind.StableDiffusion))
            lines.Add("**Self-hosted AI models,** on a server the site's owner set up, play some AI parts, so what those receive doesn't go to an AI company.");
        if (kinds.Count == 0) lines.Add("**No AI provider:** the AI features are off on this site.");

        // Error reports and timings (#77): sent only when an OpenTelemetry endpoint is set.
        if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            lines.Add($"**{Or(o.MonitoringProvider, "Our monitoring service")}** receives error reports and timings, so we can find and fix problems. They leave out names, email addresses and what people type.");
        return string.Join("\n", lines.Select(l => "- " + l));
    }

    /// <summary>The company behind an AI provider, or null for one that runs on a server the owner set up (or the test fake).</summary>
    private static string? Company(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => "Anthropic",
        AiProviderKind.OpenAI => "OpenAI",
        AiProviderKind.Gemini => "Google",
        AiProviderKind.ElevenLabs => "ElevenLabs",
        _ => null,
    };

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
}

public static class LegalEndpoints
{
    public static void MapLegalEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: anyone may read the terms before signing up or paying.
        app.MapGet("/api/legal/{page}", async (string page, LegalPages pages, AppDbContext db, IOptions<AppOptions> site, HttpRequest request, CancellationToken ct) =>
        {
            var siteUrl = site.Value.PublicUrl ?? $"{request.Scheme}://{request.Host}";
            return await pages.PageAsync(page.ToLowerInvariant(), db, siteUrl, ct) is { } view ? Results.Ok(view) : Results.NotFound();
        });
    }
}
