using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Escape;
using ButlerDidIt.Escape.Engine;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// A server that sends email and checks every session on every request (Auth:SessionCheckSeconds=0),
/// so a test can see other devices signed out straight away. Its admin is made once, first.
/// </summary>
public sealed class AccountFactory : ApiFactory
{
    public const string PublicUrl = "https://butler.example.com";
    private readonly Lazy<Task> _admin;

    public AccountFactory() => _admin = new(async () => await RegisterHostAsync("owner@example.com"));

    public CapturingEmailSender Email { get; } = new();

    /// <summary>Call before anyone else signs up, or they would become the admin.</summary>
    public Task EnsureAdminAsync() => _admin.Value;

    protected override IEnumerable<(string Key, string Value)> ExtraSettings => [("App:PublicUrl", PublicUrl), ("Auth:SessionCheckSeconds", "0")];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<ButlerDidIt.Api.Auth.IEmailSender>(Email));
    }
}

public class AccountHubTests(AccountFactory app) : IClassFixture<AccountFactory>
{
    private const string Password = "password123";

    /// <summary>A new host, on a client that keeps its cookies like a browser (a refreshed sign-in replaces the old cookie).</summary>
    private async Task<(HttpClient Client, string Email, string Id)> HostAsync()
    {
        await app.EnsureAdminAsync();
        var email = $"h{Guid.NewGuid():N}@example.com";
        var client = app.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Hana Host"));
        res.EnsureSuccessStatusCode();
        var me = (await res.Content.ReadFromJsonAsync<MeResponse>(GameJson.Options))!;
        return (client, email, me.Id);
    }

    /// <summary>The same account signed in on another device.</summary>
    private async Task<HttpClient> OtherDeviceAsync(string email, string password = Password)
    {
        var client = app.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password))).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpStatusCode> LoginAsync(string email, string password) =>
        (await app.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode;

    private static async Task<HttpStatusCode> MeAsync(HttpClient client) => (await client.GetAsync("/api/auth/me")).StatusCode;

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    private static Task<PartyInfo> MysteryPartyAsync(HttpClient host) => host.PostAsJsonAsync("/api/parties",
        new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null, UseAi: false), GameJson.Options).ContinueWith(t => Read<PartyInfo>(t.Result)).Unwrap();

    [Fact]
    public async Task The_account_page_counts_what_the_host_has_made_this_month()
    {
        var (host, email, _) = await HostAsync();
        await MysteryPartyAsync(host);
        await Read<PartyInfo>(await host.PostAsJsonAsync("/api/parties/escape",
            new CreateEscapePartyRequest("the-workshop", PartyMode.SharedScreen, null, PuzzleChoice.Fresh, null), GameJson.Options));

        var account = await Read<AccountView>(await host.GetAsync("/api/account"));
        Assert.Equal(("Hana Host", email, false, true), (account.DisplayName, account.Email, account.IsAdmin, account.EmailEnabled));
        Assert.Equal((1, 1, 2), (account.Usage.MysteriesThisMonth, account.Usage.EscapeRoomsThisMonth, account.Usage.PartiesAllTime));
        Assert.Equal(2, account.Library.Parties);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/account")).StatusCode);
    }

    [Fact]
    public async Task A_name_change_needs_no_password_but_must_fit()
    {
        var (host, _, _) = await HostAsync();
        var me = await Read<MeResponse>(await host.PutAsJsonAsync("/api/account/profile", new ProfileRequest("  Detective Dee  ")));
        Assert.Equal("Detective Dee", me.DisplayName);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutAsJsonAsync("/api/account/profile", new ProfileRequest(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutAsJsonAsync("/api/account/profile", new ProfileRequest(new string('x', 61)))).StatusCode);
    }

    [Fact]
    public async Task A_new_email_address_changes_only_once_it_confirms()
    {
        var (host, oldEmail, _) = await HostAsync();
        var newEmail = $"new{Guid.NewGuid():N}@example.com";

        var wrong = await host.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest(newEmail, "not-my-password"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("isn't your current password", await wrong.Content.ReadAsStringAsync());

        var asked = await Read<EmailChangeResult>(await host.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest(newEmail, Password)));
        Assert.True(asked.Pending);
        Assert.Equal(oldEmail, asked.Me.Email); // nothing changes until the link is clicked
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(oldEmail, Password));
        Assert.Contains(app.Email.Sent, m => m.To == oldEmail && m.Text.Contains(newEmail)); // the old inbox is told

        var mail = app.Email.Sent.Last(m => m.To == newEmail);
        var link = new Uri(Regex.Match(mail.Text, @"https://\S+").Value);
        Assert.StartsWith(AccountFactory.PublicUrl + "/account/confirm-email?", link.ToString());
        var query = HttpUtility.ParseQueryString(link.Query);
        var confirm = new ConfirmEmailChangeRequest(query["user"]!, query["email"]!, query["token"]!);

        // Opened on another device that isn't signed in: the token is the proof.
        var confirmed = await Read<MeResponse>(await app.CreateClient().PostAsJsonAsync("/api/account/email/confirm", confirm));
        Assert.Equal(newEmail, confirmed.Email);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(newEmail, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(oldEmail, Password));

        // The link works once.
        Assert.Equal(HttpStatusCode.BadRequest, (await app.CreateClient().PostAsJsonAsync("/api/account/email/confirm", confirm)).StatusCode);
    }

    [Fact]
    public async Task An_email_another_host_uses_is_refused()
    {
        var (host, _, _) = await HostAsync();
        var res = await host.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest("OWNER@example.com", Password));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_new_password_signs_out_other_devices_but_not_this_one()
    {
        var (host, email, _) = await HostAsync();
        var phone = await OtherDeviceAsync(email);

        var wrong = await host.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest("not-my-password", "brand-new-pass1"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var weak = await host.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(Password, "short"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(phone)); // failed attempts change nothing

        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(Password, "brand-new-pass1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(host));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(phone));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(email, Password));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(email, "brand-new-pass1"));
        Assert.Contains(app.Email.Sent, m => m.To == email && m.Subject == "Your password was changed");
    }

    [Fact]
    public async Task Sign_out_everywhere_keeps_only_this_browser()
    {
        var (host, email, _) = await HostAsync();
        var laptop = await OtherDeviceAsync(email);
        var tablet = await OtherDeviceAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsync("/api/account/sign-out-everywhere", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await MeAsync(host));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(laptop));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(tablet));
    }

    [Fact]
    public async Task Download_my_data_has_the_account_and_what_it_made_but_no_secrets()
    {
        var (host, email, _) = await HostAsync();
        var party = await MysteryPartyAsync(host);

        var res = await host.GetAsync("/api/account/export");
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("butler-did-it-account.json", res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
        var json = await res.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(email, root.GetProperty("account").GetProperty("email").GetString());
        Assert.Equal(DateTimeOffset.UtcNow, root.GetProperty("account").GetProperty("joined").GetDateTimeOffset(), TimeSpan.FromMinutes(5));
        Assert.Equal(party.Code, root.GetProperty("parties")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_an_account_removes_what_it_made_and_nobody_else_s()
    {
        var (host, email, hostId) = await HostAsync();
        var party = await MysteryPartyAsync(host);
        var photo = await GuestSelfieAsync(party.Code);
        var (other, _, _) = await HostAsync();
        var otherParty = await MysteryPartyAsync(other);

        // Things the AI wrote for them, and an escape on a leaderboard.
        var mysteryId = $"mine-{Guid.NewGuid():N}";
        var roomId = $"room-{Guid.NewGuid():N}";
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Scenarios.Add(new ScenarioEntity { Id = mysteryId, ThemeSlug = "blackwood", Title = "Mine", Document = "{}", OwnerUserId = hostId, Source = ScenarioSource.Custom });
            db.EscapeRooms.Add(new EscapeRoomEntity { Id = roomId, OwnerUserId = hostId, Title = "My room", Document = "{}" });
            db.EscapeResults.Add(new EscapeResult { RoomId = "the-workshop", PartyId = Guid.NewGuid(), HostUserId = hostId, Team = "Ada, Ben", Escaped = true, Score = 900, FinishedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var wrong = await host.PostAsJsonAsync("/api/account/delete", new DeleteAccountRequest("not-my-password"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.PostAsJsonAsync("/api/account/delete", new DeleteAccountRequest(Password))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(host));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(email, Password));
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync($"/api/parties/{party.Code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync(photo)).StatusCode); // the selfie file is gone
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Scenarios.AnyAsync(s => s.Id == mysteryId));
            Assert.False(await db.EscapeRooms.AnyAsync(r => r.Id == roomId));
            // The time stays on the leaderboard, without the names.
            Assert.False(await db.EscapeResults.AnyAsync(r => r.HostUserId == hostId || r.Team == "Ada, Ben"));
            Assert.True(await db.EscapeResults.AnyAsync(r => r.Score == 900 && r.HostUserId == ""));
        }

        // Another host's party is untouched.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/parties/{otherParty.Code}")).StatusCode);
    }

    [Fact]
    public async Task The_admin_account_cannot_be_deleted()
    {
        await app.EnsureAdminAsync();
        var admin = await OtherDeviceAsync("owner@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/account/delete", new DeleteAccountRequest(Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(admin));
    }

    private async Task<string> GuestSelfieAsync(string code)
    {
        var seat = await Read<SeatResponse>(await app.CreateClient().PostAsJsonAsync($"/api/parties/{code}/join", new JoinRequest("Ada")));
        var guest = app.CreateClient();
        guest.DefaultRequestHeaders.Add("X-Seat-Token", seat.Token);
        using var bitmap = new SKBitmap(40, 40);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Teal);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 90);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(png.ToArray()), "photo", "me.png" } };
        var url = (await Read<Dictionary<string, string>>(await guest.PostAsync("/api/seat/photo", form)))["photoUrl"];
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync(url)).StatusCode);
        return url;
    }
}

/// <summary>On a server without email there's no link to send, so the current password is the only check.</summary>
public class AccountWithoutEmailTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Without_email_a_new_address_takes_effect_at_once()
    {
        var oldEmail = $"o{Guid.NewGuid():N}@example.com";
        var newEmail = $"n{Guid.NewGuid():N}@example.com";
        var host = app.CreateClient();
        (await host.PostAsJsonAsync("/api/auth/register", new RegisterRequest(oldEmail, "password123", "Ola"))).EnsureSuccessStatusCode();

        var res = await host.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest(newEmail, "password123"));
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        var result = GameJson.Deserialize<EmailChangeResult>(body);
        Assert.False(result.Pending);
        Assert.Equal(newEmail, result.Me.Email);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/auth/me")).StatusCode); // still signed in here
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(newEmail, "password123"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(oldEmail, "password123"))).StatusCode);
    }
}
