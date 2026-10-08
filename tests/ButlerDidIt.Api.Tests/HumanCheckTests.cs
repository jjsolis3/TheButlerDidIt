using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ButlerDidIt.Api.Auth;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Legal;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Tests;

/// <summary>The Turnstile client (#103), against a stand-in for Cloudflare's verify address.</summary>
public class TurnstileCheckTests
{
    /// <summary>Answers every request with <paramref name="answer"/>, and keeps the requests to look at.</summary>
    private sealed class Cloudflare(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler, IHttpClientFactory
    {
        public List<(Uri? Url, Dictionary<string, string> Form)> Calls { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri, body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]))));
            return answer(request);
        }
    }

    private static TurnstileCheck Check(Cloudflare cloudflare) =>
        new(cloudflare, Options.Create(new TurnstileOptions { SiteKey = "site-key", SecretKey = "secret-key" }), NullLogger<TurnstileCheck>.Instance);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_person_is_let_through_after_Cloudflare_says_so_with_the_secret_key()
    {
        var cloudflare = new Cloudflare(_ => Json("""{"success":true,"error-codes":[],"hostname":"mystery.example.com"}"""));
        Assert.True(await Check(cloudflare).VerifyAsync("token-from-the-widget", "203.0.113.7", default));

        var (url, form) = Assert.Single(cloudflare.Calls);
        Assert.Equal(TurnstileCheck.VerifyUrl, url?.ToString());
        Assert.Equal("secret-key", form["secret"]);
        Assert.Equal("token-from-the-widget", form["response"]);
        Assert.Equal("203.0.113.7", form["remoteip"]);
    }

    [Fact]
    public async Task Anything_short_of_a_yes_from_Cloudflare_is_a_no()
    {
        Assert.False(await Check(new Cloudflare(_ => Json("""{"success":false,"error-codes":["invalid-input-response"]}"""))).VerifyAsync("used-token", null, default));
        Assert.False(await Check(new Cloudflare(_ => Json("{}", HttpStatusCode.InternalServerError))).VerifyAsync("token", null, default));
        Assert.False(await Check(new Cloudflare(_ => Json("not json"))).VerifyAsync("token", null, default));
        // Cloudflare can't be reached: no bots slip through while it's down.
        Assert.False(await Check(new Cloudflare(_ => throw new HttpRequestException("no route"))).VerifyAsync("token", null, default));
    }

    [Fact]
    public async Task No_token_is_refused_without_asking_Cloudflare()
    {
        var cloudflare = new Cloudflare(_ => Json("""{"success":true}"""));
        Assert.False(await Check(cloudflare).VerifyAsync(null, null, default));
        Assert.False(await Check(cloudflare).VerifyAsync("  ", null, default));
        Assert.False(await Check(cloudflare).VerifyAsync(new string('x', 2049), null, default));
        Assert.Empty(cloudflare.Calls);
    }
}

/// <summary>A stand-in for Turnstile: "person" passes, anything else doesn't.</summary>
public sealed class FakeHumanCheck : IHumanCheck
{
    public string? SiteKey => "test-site-key";
    public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct) => Task.FromResult(token == "person");
}

/// <summary>A site with the sign-up check on.</summary>
public sealed class HumanCheckFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IHumanCheck, FakeHumanCheck>());
    }
}

public class SignUpCheckTests(HumanCheckFactory app) : IClassFixture<HumanCheckFactory>
{
    private Task<HttpResponseMessage> SignUpAsync(string? token) =>
        app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest($"h{Guid.NewGuid():N}@example.com", "password123", "Hal", HumanToken: token));

    [Fact]
    public async Task Signing_up_needs_the_widgets_answer_and_the_site_says_so()
    {
        var options = GameJson.Deserialize<AuthOptionsView>(await app.CreateClient().GetStringAsync("/api/auth/options"));
        Assert.Equal("test-site-key", options.HumanCheckKey);

        foreach (var token in new[] { null, "", "robot" })
        {
            var refused = await SignUpAsync(token);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("finish the check that you're a person", await refused.Content.ReadAsStringAsync());
        }

        // A person gets in: the first account is the admin, whose overview shows the check is on.
        var first = await SignUpAsync("person");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var admin = app.CreateClient(new() { HandleCookies = false });
        admin.DefaultRequestHeaders.Add("Cookie", string.Join("; ", first.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0])));
        var server = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/overview")).RootElement.GetProperty("server");
        Assert.True(server.GetProperty("signUpCheck").GetBoolean());

        // The privacy policy names Cloudflare as soon as it's on.
        var privacy = GameJson.Deserialize<LegalPageView>(await app.CreateClient().GetStringAsync("/api/legal/privacy")).Markdown;
        Assert.Contains("- **Cloudflare Turnstile** checks that the sign-up form is filled in by a person.", privacy);
    }
}

/// <summary>Both keys set, as an owner would: sign-up uses Turnstile.</summary>
public sealed class TurnstileKeysFactory : ApiFactory
{
    protected override IEnumerable<(string Key, string Value)> ExtraSettings => [("Turnstile:SiteKey", "0x4AAA-site"), ("Turnstile:SecretKey", "0x4AAA-secret")];
}

public class TurnstileSetupTests(TurnstileKeysFactory app) : IClassFixture<TurnstileKeysFactory>
{
    [Fact]
    public async Task With_both_keys_the_sign_up_form_uses_Turnstile()
    {
        Assert.Equal("0x4AAA-site", GameJson.Deserialize<AuthOptionsView>(await app.CreateClient().GetStringAsync("/api/auth/options")).HumanCheckKey);
        Assert.IsType<TurnstileCheck>(app.Services.GetRequiredService<IHumanCheck>());
    }
}

public class NoSignUpCheckTests(ApiFactory app) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Without_keys_there_is_no_check()
    {
        Assert.Null(GameJson.Deserialize<AuthOptionsView>(await app.CreateClient().GetStringAsync("/api/auth/options")).HumanCheckKey);
        Assert.IsType<NoHumanCheck>(app.Services.GetRequiredService<IHumanCheck>());
    }
}
