using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ButlerDidIt.Api.Tests;

/// <summary>An invite-only server. Its first account, the admin, is made once and shared by the tests.</summary>
public sealed class InviteOnlyFactory : ApiFactory
{
    private readonly Lazy<Task<HttpClient>> _admin;

    public InviteOnlyFactory() => _admin = new(async () => (await RegisterHostAsync("admin@example.com")).Client);

    protected override bool AllowRegistration => false;

    /// <summary>The admin's client. Call it before anyone else signs up, or they would become the admin.</summary>
    public Task<HttpClient> AdminAsync() => _admin.Value;
}

public class InviteTests(InviteOnlyFactory app) : IClassFixture<InviteOnlyFactory>
{
    private HttpClient Anonymous() => app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string TokenIn(string link) => HttpUtility.ParseQueryString(new Uri(link).Query)["invite"]!;

    private async Task<CreatedInvite> CreateAsync(CreateInviteRequest req)
    {
        var res = await (await app.AdminAsync()).PostAsJsonAsync("/api/admin/invites", req, GameJson.Options);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<CreatedInvite>(GameJson.Options))!;
    }

    private async Task<string> NewTokenAsync(string? email = null) => TokenIn((await CreateAsync(new CreateInviteRequest(email, null))).Link);

    private Task<HttpResponseMessage> SignUpAsync(string email, string? invite, string password = "password123") =>
        Anonymous().PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, password, "Invited Host", invite));

    private async Task<IReadOnlyList<InviteView>> ListAsync() =>
        (await (await app.AdminAsync()).GetFromJsonAsync<List<InviteView>>("/api/admin/invites", GameJson.Options))!;

    private static string Unique(string name) => $"{name}{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task An_invite_link_signs_up_one_host_once()
    {
        await app.AdminAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await SignUpAsync(Unique("uninvited"), invite: null)).StatusCode);

        var created = await CreateAsync(new CreateInviteRequest(null, "Ana, my sister", Days: 7));
        Assert.Matches(@"^http://localhost/login\?invite=[A-Za-z0-9_-]{43}$", created.Link);
        Assert.Equal(InviteStatus.Pending, created.Invite.Status);
        var token = TokenIn(created.Link);

        // The sign-up page checks the link first, and learns who sent it.
        var check = await Anonymous().PostAsJsonAsync("/api/auth/invite", new InviteCheckRequest(token));
        var info = (await check.Content.ReadFromJsonAsync<InviteInfo>(GameJson.Options))!;
        Assert.Equal("The Host", info.InvitedBy);
        Assert.Null(info.Email);

        var email = Unique("ana");
        var signedUp = await SignUpAsync(email, token);
        Assert.True(signedUp.IsSuccessStatusCode, await signedUp.Content.ReadAsStringAsync());
        Assert.Contains("\"isAdmin\":false", await signedUp.Content.ReadAsStringAsync());

        // Used up: neither a second sign-up nor the page's check accepts it again.
        var again = await SignUpAsync(Unique("someone"), token);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("already been used", await again.Content.ReadAsStringAsync());
        Assert.Contains("already been used", await (await Anonymous().PostAsJsonAsync("/api/auth/invite", new InviteCheckRequest(token))).Content.ReadAsStringAsync());

        // The admin's list says who used it, and never shows the link again.
        var listed = (await ListAsync()).Single(i => i.Id == created.Invite.Id);
        Assert.Equal(InviteStatus.Used, listed.Status);
        Assert.Equal("Invited Host", listed.UsedBy);
        Assert.Equal("Ana, my sister", listed.Note);
        var raw = await (await app.AdminAsync()).GetStringAsync("/api/admin/invites");
        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain(ButlerDidIt.Api.Auth.SeatTokens.Hash(token), raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_invite_for_one_address_only_works_for_that_address()
    {
        var token = await NewTokenAsync("Bea.Lock@Example.com");

        var info = await (await Anonymous().PostAsJsonAsync("/api/auth/invite", new InviteCheckRequest(token))).Content.ReadFromJsonAsync<InviteInfo>(GameJson.Options);
        Assert.Equal("Bea.Lock@Example.com", info!.Email);

        var wrong = await SignUpAsync(Unique("eve"), token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("This invite is for Bea.Lock@Example.com", await wrong.Content.ReadAsStringAsync());

        // Email addresses aren't case-sensitive.
        var right = await SignUpAsync("bea.lock@example.com", token);
        Assert.True(right.IsSuccessStatusCode, await right.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Expired_and_revoked_invites_are_refused()
    {
        var expiring = await CreateAsync(new CreateInviteRequest(null, null, Days: 1));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Invites.Where(i => i.Id == expiring.Invite.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var late = await SignUpAsync(Unique("late"), TokenIn(expiring.Link));
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Contains("expired", await late.Content.ReadAsStringAsync());
        Assert.Equal(InviteStatus.Expired, (await ListAsync()).Single(i => i.Id == expiring.Invite.Id).Status);

        var revoked = await CreateAsync(new CreateInviteRequest(null, null));
        var admin = await app.AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/invites/{revoked.Invite.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/invites/{revoked.Invite.Id}")).StatusCode);
        var refused = await SignUpAsync(Unique("revoked"), TokenIn(revoked.Link));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("isn't valid", await refused.Content.ReadAsStringAsync());

        // A made-up token gets the same answer.
        Assert.Contains("isn't valid", await (await SignUpAsync(Unique("guess"), "not-a-real-invite")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failed_sign_up_leaves_the_invite_unused()
    {
        var token = await NewTokenAsync();

        var weak = await SignUpAsync(Unique("weak"), token, password: "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var taken = await SignUpAsync("admin@example.com", token); // the admin's address is taken
        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);

        var fixedUp = await SignUpAsync(Unique("retry"), token);
        Assert.True(fixedUp.IsSuccessStatusCode, await fixedUp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_people_racing_for_one_invite_get_one_account()
    {
        var token = await NewTokenAsync();
        var emails = Enumerable.Range(0, 5).Select(i => Unique($"race{i}-")).ToList();

        var results = await Task.WhenAll(emails.Select(e => SignUpAsync(e, token)));

        Assert.Single(results, r => r.IsSuccessStatusCode);
        Assert.All(results.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(u => emails.Contains(u.Email!)));
    }

    [Fact]
    public async Task Only_the_admin_manages_invites()
    {
        var token = await NewTokenAsync();
        var signedUp = await SignUpAsync(Unique("host"), token);
        var host = Anonymous();
        host.DefaultRequestHeaders.Add("Cookie", string.Join("; ", signedUp.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0])));

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/admin/invites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync("/api/admin/invites", new CreateInviteRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync("/api/admin/invites")).StatusCode);
    }

    [Fact]
    public async Task Bad_invite_requests_are_refused()
    {
        await app.AdminAsync();
        async Task<HttpStatusCode> Create(CreateInviteRequest req) =>
            (await (await app.AdminAsync()).PostAsJsonAsync("/api/admin/invites", req, GameJson.Options)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateInviteRequest(null, null, Days: 0)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateInviteRequest(null, null, Days: InviteEndpoints.MaxDays + 1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateInviteRequest("not an email", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateInviteRequest("Ana <ana@example.com>", null)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateInviteRequest(null, new string('x', 81))));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateInviteRequest(null, null, Send: true)));                    // nobody to send it to
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateInviteRequest(Unique("mail"), null, Send: true)));          // no email on this server
        Assert.Equal(HttpStatusCode.Conflict, await Create(new CreateInviteRequest("ADMIN@example.com", null)));                    // already has an account
    }
}

public class InviteEmailTests(EmailFactory app) : IClassFixture<EmailFactory>
{
    [Fact]
    public async Task An_invite_can_be_emailed_with_a_link_to_the_public_site()
    {
        var (admin, _) = await app.RegisterHostAsync("owner@example.com"); // first account = admin

        var res = await admin.PostAsJsonAsync("/api/admin/invites", new CreateInviteRequest("guest.host@example.com", null, Days: 3, Send: true), GameJson.Options);
        var created = (await res.Content.ReadFromJsonAsync<CreatedInvite>(GameJson.Options))!;
        Assert.True(created.Emailed);

        var mail = app.Email.Sent.Last(m => m.To == "guest.host@example.com");
        Assert.Contains("The Host has invited you", mail.Text);
        Assert.Contains("for 3 days", mail.Text);
        var link = Regex.Match(mail.Text, @"https://\S+").Value;
        Assert.Equal(created.Link, link);
        Assert.StartsWith(EmailFactory.PublicUrl + "/login?invite=", link);

        var token = HttpUtility.ParseQueryString(new Uri(link).Query)["invite"];
        var signedUp = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest("guest.host@example.com", "password123", "Guest Host", token));
        Assert.True(signedUp.IsSuccessStatusCode, await signedUp.Content.ReadAsStringAsync());
    }
}
