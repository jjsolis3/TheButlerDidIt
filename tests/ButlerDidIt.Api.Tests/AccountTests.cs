using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Scenarios;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ButlerDidIt.Api.Tests;

public class AccountTests(EmailFactory app) : IClassFixture<EmailFactory>
{
    private HttpClient Anonymous() => app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string LinkIn(string text) => Regex.Match(text, @"https://\S+").Value;

    private (string Text, Uri Link) LastEmailTo(string to)
    {
        var mail = app.Email.Sent.Last(m => m.To == to);
        return (mail.Text, new Uri(LinkIn(mail.Text)));
    }

    private async Task<HttpStatusCode> LoginAsync(string email, string password) =>
        (await Anonymous().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode;

    [Fact]
    public async Task A_forgotten_password_is_reset_with_a_one_time_emailed_link()
    {
        var email = $"f{Guid.NewGuid():N}@example.com";
        await app.RegisterHostAsync(email);

        var res = await Anonymous().PostAsJsonAsync("/api/auth/forgot", new ForgotRequest(email));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (_, link) = LastEmailTo(email);
        Assert.StartsWith(EmailFactory.PublicUrl + "/reset-password?", link.ToString());

        var query = HttpUtility.ParseQueryString(link.Query);
        var reset = await Anonymous().PostAsJsonAsync("/api/auth/reset", new ResetRequest(query["email"]!, query["token"]!, "brand-new-pass1"));
        Assert.True(reset.StatusCode == HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
        Assert.Contains("\"emailConfirmed\":true", await reset.Content.ReadAsStringAsync()); // the link proved they own the inbox

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(email, "password123"));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(email, "brand-new-pass1"));

        // The same link can't be used twice.
        var again = await Anonymous().PostAsJsonAsync("/api/auth/reset", new ResetRequest(query["email"]!, query["token"]!, "another-pass-1"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("invalid or has expired", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forgot_password_gives_nothing_away_and_ignores_a_forged_host()
    {
        var before = app.Email.Sent.Count;
        var unknown = await Anonymous().PostAsJsonAsync("/api/auth/forgot", new ForgotRequest("nobody@example.com"));
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode); // same answer as for a real account…
        Assert.Equal(before, app.Email.Sent.Count);          // …but no email

        // An attacker asks for a reset for someone else, pretending the site lives at their domain.
        var victim = $"v{Guid.NewGuid():N}@example.com";
        await app.RegisterHostAsync(victim);
        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/auth/forgot") { Content = JsonContent.Create(new ForgotRequest(victim)) };
        forged.Headers.Host = "evil.example";
        await Anonymous().SendAsync(forged);
        var (text, _) = LastEmailTo(victim);
        Assert.DoesNotContain("evil.example", text);
        Assert.Contains(EmailFactory.PublicUrl, text);
    }

    [Fact]
    public async Task Hosts_must_confirm_their_email_before_creating_parties_when_the_server_requires_it()
    {
        var email = $"c{Guid.NewGuid():N}@example.com";
        var (host, _) = await app.RegisterHostAsync(email);
        var request = new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, ContentRating.Mature, null, UseAi: false);

        var blocked = await host.PostAsJsonAsync("/api/parties", request, GameJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("confirm your email", await blocked.Content.ReadAsStringAsync());

        var (_, link) = LastEmailTo(email);
        Assert.StartsWith(EmailFactory.PublicUrl + "/confirm-email?", link.ToString());
        var query = HttpUtility.ParseQueryString(link.Query);
        Assert.Equal(HttpStatusCode.NoContent, (await Anonymous().PostAsJsonAsync("/api/auth/confirm", new ConfirmRequest(query["user"]!, query["token"]!))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.PostAsJsonAsync("/api/parties", request, GameJson.Options)).StatusCode);
        var options = await Anonymous().GetStringAsync("/api/auth/options");
        Assert.Contains("\"emailEnabled\":true", options);
    }
}

/// <summary>A server without email: the admin hands out reset links instead.</summary>
public class AdminHostTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Admin_can_give_a_host_a_reset_link_when_there_is_no_email()
    {
        var (admin, _) = await app.RegisterHostAsync("admin@example.com"); // first account = admin
        var (host, _) = await app.RegisterHostAsync("forgetful@example.com");
        var anonymous = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        Assert.Contains("\"emailEnabled\":false", await anonymous.GetStringAsync("/api/auth/options"));
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/forgot", new ForgotRequest("forgetful@example.com"))).StatusCode);

        var hosts = await admin.GetFromJsonAsync<List<HostView>>("/api/admin/hosts", GameJson.Options);
        var target = hosts!.Single(h => h.Email == "forgetful@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/admin/hosts")).StatusCode); // hosts can't see each other

        var link = await (await admin.PostAsync($"/api/admin/hosts/{target.Id}/reset-link", null)).Content.ReadFromJsonAsync<ResetLinkView>(GameJson.Options);
        var query = HttpUtility.ParseQueryString(new Uri(link!.Link).Query);
        var reset = await anonymous.PostAsJsonAsync("/api/auth/reset", new ResetRequest(query["email"]!, query["token"]!, "remembered-now1"));
        Assert.True(reset.StatusCode == HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("forgetful@example.com", "remembered-now1"))).StatusCode);
    }
}
