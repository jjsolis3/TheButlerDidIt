using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ButlerDidIt.Ai;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Legal;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// A site whose owner has done everything the legal pages ask (#103): their details are set, the pages have been
/// reviewed (a copy of content/legal without the starter-draft line), and it sends email and takes payments by Stripe.
/// </summary>
public sealed class LegalFactory : ApiFactory
{
    public const string PublicUrl = "https://mystery.example.com";

    public LegalFactory()
    {
        // As the owner would after the review: the same pages, with their first line deleted.
        Directory.CreateDirectory(Reviewed);
        foreach (var file in Directory.GetFiles(RepoLegalFolder(), "*.md"))
            File.WriteAllLines(Path.Combine(Reviewed, Path.GetFileName(file)), File.ReadAllLines(file).Skip(1));
    }

    /// <summary>Inside the media folder, so it goes when the factory does.</summary>
    public string Reviewed => Path.Combine(MediaRoot, "legal");

    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        ("App:PublicUrl", PublicUrl),
        ("Legal:Folder", Reviewed),
        ("Legal:OperatorName", "Butler Games LLC"),
        ("Legal:ContactEmail", "help@butler.example"),
        ("Legal:State", "Arizona"),
        ("Legal:RefundDays", "10"),
        ("Legal:HostingProvider", "Butler Cloud"),
        ("Legal:EmailProvider", "Postmark"),
        ("Billing:Provider", "Stripe"),
        ("Billing:Stripe:SecretKey", "sk_test_not_a_real_key"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Email "sent" here is only kept, so signing up doesn't try to reach a mail server.
        builder.ConfigureTestServices(s => s.AddSingleton<ButlerDidIt.Api.Auth.IEmailSender>(new CapturingEmailSender()));
    }

    public static string RepoLegalFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "content", "legal"))) return Path.Combine(dir.FullName, "content", "legal");
        throw new DirectoryNotFoundException("content/legal wasn't found above the test's folder.");
    }
}

public class LegalTests(LegalFactory app) : IClassFixture<LegalFactory>
{
    private static readonly string[] Pages = ["terms", "privacy", "refunds"];

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private async Task<LegalPageView> PageAsync(string page) => await Read<LegalPageView>(await app.CreateClient().GetAsync($"/api/legal/{page}"));

    [Fact]
    public async Task The_pages_fill_in_the_sites_details_and_never_show_the_owners_notes()
    {
        foreach (var page in Pages)
        {
            var view = await PageAsync(page);
            Assert.False(view.Draft);
            Assert.Equal(LegalPages.Titles[page], view.Title);
            Assert.StartsWith($"# {view.Title}", view.Markdown);
            // Every {{word}} was known, and the notes for the owner (HTML comments) never leave the server.
            Assert.DoesNotContain("{{", view.Markdown);
            Assert.DoesNotContain("<!--", view.Markdown);
            Assert.DoesNotContain("REVIEW:", view.Markdown);
            Assert.DoesNotContain("not set yet", view.Markdown);
            Assert.Contains("Butler Games LLC, [help@butler.example](mailto:help@butler.example)", view.Markdown);
        }

        // The numbers are the site's own settings, so the pages can't disagree with how it behaves.
        var terms = (await PageAsync("terms")).Markdown;
        Assert.Contains("runs The Butler Did It at mystery.example.com", terms);
        Assert.Contains("New hosts get 14 days free", terms);
        Assert.Contains("a single payment for 72 hours of hosting", terms);
        Assert.Contains("you keep your games for 7 days", terms);
        Assert.Contains("the laws of the State of Arizona", terms);

        var refunds = (await PageAsync("refunds")).Markdown;
        Assert.Contains("## A full refund within 10 days, if unused", refunds);

        var privacy = (await PageAsync("privacy")).Markdown;
        Assert.Contains("**Deleted automatically** 30 days after the party, or 14 days after it was last used", privacy);
        Assert.Contains("kept for up to 6 months", privacy);
    }

    [Fact]
    public async Task The_privacy_policy_names_the_services_this_site_actually_uses()
    {
        // Two AI providers with roles, as the admin would set them up under Admin → AI: a company's, and one run at home.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var claude = new AiProviderEntity { Id = Guid.NewGuid(), Name = "Claude", Kind = AiProviderKind.Anthropic, CreatedAt = now, UpdatedAt = now };
            var ollama = new AiProviderEntity { Id = Guid.NewGuid(), Name = "Home", Kind = AiProviderKind.Ollama, CreatedAt = now, UpdatedAt = now };
            db.AiProviders.AddRange(claude, ollama);
            db.AiRoles.AddRange(
                new AiRoleEntity { Role = AiRole.Storyteller, ProviderId = claude.Id, Model = "claude" },
                new AiRoleEntity { Role = AiRole.Actor, ProviderId = ollama.Id, Model = "llama" });
            await db.SaveChangesAsync();
        }

        var privacy = (await PageAsync("privacy")).Markdown;
        Assert.Contains("- **Butler Cloud** runs the server and the database", privacy);
        Assert.Contains("- **Postmark** delivers account emails", privacy);
        Assert.Contains("- **Stripe** takes payments", privacy);
        Assert.Contains("[Stripe's privacy policy](https://stripe.com/privacy)", privacy);
        Assert.Contains("- **Anthropic** powers the AI features", privacy);
        Assert.Contains("- **Self-hosted AI models,**", privacy);
        // Nothing this site doesn't use: uploads stay on its own disk, and no monitoring service is set.
        Assert.DoesNotContain("storage service", privacy);
        Assert.DoesNotContain("monitoring service", privacy);
        Assert.DoesNotContain("No AI provider", privacy);
    }

    [Fact]
    public async Task Only_the_three_pages_can_be_read()
    {
        var client = app.CreateClient();
        foreach (var path in new[] { "/api/legal/secrets", "/api/legal/..%2F..%2Fappsettings", "/api/legal/terms.md" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal("terms", (await Read<LegalPageView>(await client.GetAsync("/api/legal/TERMS"))).Page);
    }

    [Fact]
    public async Task Signing_up_records_when_the_terms_were_accepted_and_the_admin_sees_the_pages_are_ready()
    {
        var (admin, _) = await app.RegisterHostAsync($"owner{Guid.NewGuid():N}@example.com");
        var (host, _) = await app.RegisterHostAsync($"terms{Guid.NewGuid():N}@example.com");
        var me = await Read<MeResponse>(await host.GetAsync("/api/auth/me"));
        using (var scope = app.Services.CreateScope())
        {
            var accepted = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(u => u.Id == me.Id)).TermsAcceptedAt;
            Assert.NotNull(accepted);
            Assert.True(DateTimeOffset.UtcNow - accepted < TimeSpan.FromMinutes(1));
        }
        // It's part of the host's own data.
        var export = JsonDocument.Parse(await host.GetStringAsync("/api/account/export")).RootElement;
        Assert.Equal(JsonValueKind.String, export.GetProperty("account").GetProperty("acceptedTerms").ValueKind);

        // Whichever account came first is the admin, and the overview's checklist has nothing left to ask.
        var first = await Read<MeResponse>(await admin.GetAsync("/api/auth/me"));
        var overview = JsonDocument.Parse(await (first.IsAdmin ? admin : host).GetStringAsync("/api/admin/overview")).RootElement;
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("server").GetProperty("legalProblem").ValueKind);
    }
}

/// <summary>A fresh site: the starter drafts, and none of the owner's details set yet.</summary>
public class LegalDraftTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Starter_drafts_say_plainly_what_is_missing_and_the_admin_is_told()
    {
        var (admin, _) = await app.RegisterHostAsync("owner@example.com");
        var terms = GameJson.Deserialize<LegalPageView>(await app.CreateClient().GetStringAsync("/api/legal/terms"));
        Assert.True(terms.Draft);
        Assert.Contains("[the site's owner: name not set yet]", terms.Markdown);
        Assert.Contains("[contact email not set yet]", terms.Markdown);
        Assert.Contains("the laws of the State of [state not set yet]", terms.Markdown);
        Assert.DoesNotContain("<!--", terms.Markdown);

        // No email, no payments, no AI: the privacy policy lists only the hosting.
        var privacy = GameJson.Deserialize<LegalPageView>(await app.CreateClient().GetStringAsync("/api/legal/privacy")).Markdown;
        Assert.Contains("- **Our hosting provider** runs the server", privacy);
        Assert.Contains("- **No AI provider:** the AI features are off on this site.", privacy);
        Assert.DoesNotContain("**Stripe**", privacy);
        Assert.DoesNotContain("email service", privacy);

        var problem = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/overview")).RootElement.GetProperty("server").GetProperty("legalProblem").GetString();
        Assert.Contains("Set Legal__OperatorName, Legal__ContactEmail and Legal__State", problem);
        Assert.Contains("Terms of Service, Privacy Policy and Refund Policy are still the starter draft", problem);
    }
}
