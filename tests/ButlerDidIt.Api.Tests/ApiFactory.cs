using System.Net.Http.Json;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Game;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ButlerDidIt.Api.Tests;

/// <summary>
/// Starts the real app in memory against a real, throwaway PostgreSQL database.
///
/// Set TEST_DATABASE_URL to point at your server; the default matches the local
/// dev setup and the CI service container. Each factory creates its own database
/// and drops it afterwards, so tests never see each other's data.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _database = $"butler_test_{Guid.NewGuid():N}";

    /// <summary>A throwaway folder for generated and uploaded files.</summary>
    public string MediaRoot { get; } = Path.Combine(Path.GetTempPath(), $"butler_media_{Guid.NewGuid():N}");

    private static string ServerConnection =>
        Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
        ?? "Host=localhost;Port=5432;Username=butler;Password=butler;Database=postgres";

    private string ConnectionString => new NpgsqlConnectionStringBuilder(ServerConnection) { Database = _database }.ConnectionString;

    protected virtual bool AllowRegistration => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Auth:AllowRegistration", AllowRegistration.ToString());
        builder.UseSetting("Media:Root", MediaRoot);
        foreach (var (key, value) in ExtraSettings) builder.UseSetting(key, value);
    }

    protected virtual IEnumerable<(string Key, string Value)> ExtraSettings => [];

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ServerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(ServerConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
        if (Directory.Exists(MediaRoot)) Directory.Delete(MediaRoot, recursive: true);
    }

    /// <summary>Registers a host account and returns a client that sends its auth cookie, plus the cookie itself for hub connections.</summary>
    public async Task<(HttpClient Client, string Cookie)> RegisterHostAsync(string email = "host@example.com")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var res = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "password123", "The Host"));
        res.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", res.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return (client, cookie);
    }

    /// <summary>A SignalR connection, authenticated by seat token and/or the host's cookie.</summary>
    public async Task<HubConnection> ConnectAsync(string? seatToken = null, string? cookie = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "hubs/party"), o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling; // the in-memory TestServer has no real sockets
                if (seatToken is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(seatToken);
                if (cookie is not null) o.Headers["Cookie"] = cookie;
            })
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = GameJson.Options.PropertyNamingPolicy;
                foreach (var c in GameJson.Options.Converters) o.PayloadSerializerOptions.Converters.Add(c);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }
}

public sealed class ClosedRegistrationFactory : ApiFactory
{
    protected override bool AllowRegistration => false;
}

/// <summary>The app with every AI role pointed at the Fake provider (canned answers, no network).</summary>
public class FakeAiFactory : ApiFactory
{
    protected virtual string Budget => "0";

    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        ("Ai:AllowFakeProvider", "true"),
        ("Ai:MonthlyBudgetUsd", Budget),
        ("Ai:Providers:0:Name", "Fake"),
        ("Ai:Providers:0:Kind", "Fake"),
        ("Ai:Roles:Storyteller:Provider", "Fake"),
        ("Ai:Roles:Storyteller:Model", "fake-model"),
        ("Ai:Roles:Actor:Provider", "Fake"),
        ("Ai:Roles:Actor:Model", "fake-model"),
        ("Ai:Roles:Inspector:Provider", "Fake"),
        ("Ai:Roles:Inspector:Model", "fake-model"),
        ("Ai:Roles:Voice:Provider", "Fake"),
        ("Ai:Roles:Voice:Model", "fake-voice"),
        ("Ai:Roles:Illustrator:Provider", "Fake"),
        ("Ai:Roles:Illustrator:Model", "fake-image"),
    ];
}

/// <summary>A tiny budget, so one call uses it up.</summary>
public sealed class TinyBudgetAiFactory : FakeAiFactory
{
    protected override string Budget => "0.0001";
}

/// <summary>Email "sent" in tests is kept here, so a test can read it and click the link.</summary>
public sealed class CapturingEmailSender : ButlerDidIt.Api.Auth.IEmailSender
{
    public System.Collections.Concurrent.ConcurrentQueue<(string To, string Subject, string Text)> Sent { get; } = new();
    public bool IsConfigured => true;

    public Task SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        Sent.Enqueue((to, subject, text));
        return Task.CompletedTask;
    }
}

/// <summary>A server that can send email and requires hosts to confirm their address.</summary>
public sealed class EmailFactory : ApiFactory
{
    public const string PublicUrl = "https://mystery.example.com";
    public CapturingEmailSender Email { get; } = new();

    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        ("App:PublicUrl", PublicUrl),
        ("Auth:RequireConfirmedEmail", "true"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<ButlerDidIt.Api.Auth.IEmailSender>(Email));
    }
}
