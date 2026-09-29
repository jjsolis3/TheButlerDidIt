using System.Net;
using System.Net.Http.Json;
using ButlerDidIt.Api.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ButlerDidIt.Api.Tests;

/// <summary>Sign-ups can send a confirmation email, so one address can only register a few accounts an hour.</summary>
public class SignUpRateLimitTests(SignUpRateLimitFactory app) : IClassFixture<SignUpRateLimitFactory>
{
    [Fact]
    public async Task One_address_can_register_only_a_few_accounts_an_hour()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        async Task<HttpStatusCode> Register(int n) =>
            (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"host{n}@example.com", "password123", $"Host {n}"))).StatusCode;

        // The limit is 3 an hour in this factory (10 on a real server).
        for (var n = 1; n <= 3; n++) Assert.Equal(HttpStatusCode.OK, await Register(n));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Register(4));

        // Signing in isn't limited by it: the existing hosts can still get in.
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("host1@example.com", "password123"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
