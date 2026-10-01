using System.Net;
using System.Net.Http.Json;
using Amazon.Runtime;
using Amazon.S3;
using ButlerDidIt.Api.Data;
using ButlerDidIt.Api.Endpoints;
using ButlerDidIt.Api.Media;
using ButlerDidIt.Api.Parties;
using ButlerDidIt.Api.Scale;
using ButlerDidIt.Game;
using ButlerDidIt.Game.Engine;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ButlerDidIt.Api.Tests;

/// <summary>A test that needs an extra service (Redis, S3). It's skipped, saying which variable to set, when that service isn't configured.</summary>
public sealed class RequiresEnvFactAttribute : FactAttribute
{
    public RequiresEnvFactAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable))) Skip = $"Set {variable} to run this test.";
    }
}

public class ClusterLockTests
{
    private static ClusterLock NewServerLock() => new(NpgsqlDataSource.Create(ApiFactory.ServerConnection));

    [Fact]
    public async Task Commands_for_one_party_on_two_servers_run_one_at_a_time()
    {
        // Two PartyLocks with their own ClusterLock are two servers: they share nothing but the database.
        var serverA = new PartyLocks(NewServerLock());
        var serverB = new PartyLocks(NewServerLock());
        var party = Guid.NewGuid();
        var gate = new object();
        int inside = 0, mostInside = 0, done = 0;

        async Task Command(PartyLocks locks)
        {
            await using (await locks.AcquireAsync(party, CancellationToken.None))
            {
                lock (gate) mostInside = Math.Max(mostInside, ++inside);
                await Task.Delay(15);
                lock (gate) inside--;
                Interlocked.Increment(ref done);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Command(i % 2 == 0 ? serverA : serverB)));
        Assert.Equal(12, done);
        Assert.Equal(1, mostInside);
    }

    [Fact]
    public async Task Different_parties_never_wait_for_each_other()
    {
        var serverA = new PartyLocks(NewServerLock());
        var serverB = new PartyLocks(NewServerLock());
        await using (await serverA.AcquireAsync(Guid.NewGuid(), CancellationToken.None))
        {
            var other = serverB.AcquireAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.Same(other, await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5))));
            await (await other).DisposeAsync();
        }
    }

    [Fact]
    public async Task Only_one_server_gets_the_ticker_turn_until_it_lets_go()
    {
        var serverA = NewServerLock();
        var serverB = NewServerLock();
        var key = $"ticker-test:{Guid.NewGuid()}";

        var first = await serverA.TryAcquireAsync(key, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await serverB.TryAcquireAsync(key, CancellationToken.None));

        await first.DisposeAsync();
        var second = await serverB.TryAcquireAsync(key, CancellationToken.None);
        Assert.NotNull(second);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task A_waiting_lock_gives_up_only_when_its_own_timeout_runs_out()
    {
        // Startup waits up to 10 minutes for another server's migrations and seeding (#93); before, every
        // wait was cut off at the 30 seconds a database command gets, and the waiting server crashed.
        var serverA = NewServerLock();
        var serverB = NewServerLock();
        var key = $"startup-test:{Guid.NewGuid()}";

        var held = await serverA.AcquireAsync(key, CancellationToken.None);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<NpgsqlException>(() => serverB.AcquireAsync(key, CancellationToken.None, TimeSpan.FromSeconds(1)));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10)); // its own 1 second, not the default 30

        // A longer wait keeps waiting past that, and gets the lock once the holder lets go.
        var waiting = serverB.AcquireAsync(key, CancellationToken.None, TimeSpan.FromMinutes(1));
        await Task.Delay(1500);
        Assert.False(waiting.IsCompleted);
        await held.DisposeAsync();
        await (await waiting).DisposeAsync();
    }

    [Fact]
    public async Task A_single_server_needs_no_database_locks()
    {
        var single = new ClusterLock(null);
        Assert.False(single.Enabled);
        await using var a = await single.TryAcquireAsync("ticker", CancellationToken.None);
        await using var b = await single.TryAcquireAsync("ticker", CancellationToken.None);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }
}

/// <summary>Switching to database-stored keys keeps the keys already in the old folder.</summary>
public sealed class KeyImportFactory : ApiFactory
{
    public string KeysPath { get; } = Path.Combine(Path.GetTempPath(), $"butler_keys_{Guid.NewGuid():N}");
    public string Secret { get; }

    public KeyImportFactory()
    {
        // What an existing server has: a key folder, and something encrypted with it (like a saved AI API key).
        var old = DataProtectionProvider.Create(new DirectoryInfo(KeysPath), b => b.SetApplicationName("ButlerDidIt"));
        Secret = old.CreateProtector("test").Protect("sk-remember-me");
    }

    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
        [("DataProtection:Store", "Database"), ("DataProtection:KeysPath", KeysPath)];
}

public class DataProtectionKeyImportTests(KeyImportFactory app) : IClassFixture<KeyImportFactory>
{
    [Fact]
    public void Keys_from_the_old_folder_are_copied_into_the_database_and_still_decrypt()
    {
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
        Assert.Equal("sk-remember-me", protector.Unprotect(app.Secret));

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(db.DataProtectionKeys, k => k.FriendlyName!.StartsWith("key-", StringComparison.Ordinal));
    }
}

public class S3MediaStoreTests
{
    [RequiresEnvFact("TEST_S3_URL")]
    public async Task Files_round_trip_through_an_s3_compatible_bucket()
    {
        var options = new MediaOptions
        {
            Storage = "S3",
            S3 = new S3MediaOptions
            {
                ServiceUrl = Environment.GetEnvironmentVariable("TEST_S3_URL"),
                Region = "us-east-1",
                Bucket = $"butler-test-{Guid.NewGuid():N}"[..30],
                AccessKey = Environment.GetEnvironmentVariable("TEST_S3_ACCESS_KEY") ?? "minioadmin",
                SecretKey = Environment.GetEnvironmentVariable("TEST_S3_SECRET_KEY") ?? "minioadmin",
            },
        };
        using (var admin = new AmazonS3Client(new BasicAWSCredentials(options.S3.AccessKey, options.S3.SecretKey),
                   new AmazonS3Config { ServiceURL = options.S3.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = options.S3.Region }))
        {
            await admin.PutBucketAsync(options.S3.Bucket);
        }

        using var store = new S3MediaStore(Options.Create(options));
        var bytes = "RIFF-pretend-audio"u8.ToArray();
        var path = await store.SaveAsync(bytes, "mp3", "audio/mpeg", CancellationToken.None);
        Assert.Matches(@"^\d{4}-\d{2}/[0-9a-f]{32}\.mp3$", path);

        await using (var read = await store.OpenReadAsync(path, CancellationToken.None))
        {
            Assert.NotNull(read);
            Assert.True(read.CanSeek, "seekable, so audio can be served in ranges");
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        await store.DeleteAsync(path, CancellationToken.None);
        Assert.Null(await store.OpenReadAsync(path, CancellationToken.None));
    }
}

/// <summary>One of several app servers sharing a database and a Redis backplane.</summary>
public sealed class ScaledServer(string database, bool ownsDatabase, string redis) : ApiFactory(database, ownsDatabase)
{
    protected override IEnumerable<(string Key, string Value)> ExtraSettings =>
    [
        ("Scale:MultiInstance", "true"),
        ("Scale:Redis", redis),
        ("DataProtection:Store", "Database"),
    ];
}

public class MultiServerTests
{
    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return GameJson.Deserialize<T>(body);
    }

    [RequiresEnvFact("TEST_REDIS_URL")]
    public async Task A_tv_on_one_server_follows_a_guest_on_another()
    {
        var redis = Environment.GetEnvironmentVariable("TEST_REDIS_URL")!;
        var database = $"butler_scale_{Guid.NewGuid():N}";
        var a = new ScaledServer(database, ownsDatabase: true, redis);
        var b = new ScaledServer(database, ownsDatabase: false, redis);
        await ((IAsyncLifetime)a).InitializeAsync();
        try
        {
            // Both start at once: the startup lock makes them migrate and seed one after the other.
            await Task.WhenAll(Task.Run(() => a.Server), Task.Run(() => b.Server));

            // Sign in on server A; the same cookie works on server B because the keys are shared.
            var (hostA, cookie) = await a.RegisterHostAsync($"scale{Guid.NewGuid():N}@example.com");
            var hostB = b.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            hostB.DefaultRequestHeaders.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await hostB.GetAsync("/api/auth/me")).StatusCode);

            // The party is created on A and the TV watches it there; the guest joins and plays through B.
            var party = await Read<PartyInfo>(await hostA.PostAsJsonAsync("/api/parties",
                new CreatePartyRequest("death-at-blackwood-manor", PartyMode.SharedScreen, null), GameJson.Options));
            var seat = await Read<SeatResponse>(await b.CreateClient().PostAsJsonAsync($"/api/parties/{party.Code}/join", new JoinRequest("Ada")));

            await using var tv = await a.ConnectAsync(cookie: cookie);
            var seen = new List<StageView>();
            tv.On<StageView>("stage", v => { lock (seen) seen.Add(v); });
            await tv.InvokeAsync<StageView>("WatchParty", party.Code);

            await using var guest = await b.ConnectAsync(seat.Token);
            await guest.InvokeAsync<PlayerView>("JoinSeat");
            await guest.InvokeAsync("ChooseCharacter", "violet");

            // Up to 30 seconds: the whole Api suite runs at once, and the relay through Redis can be slow then (#93).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                lock (seen)
                    if (seen.Any(v => v.Cast.Any(c => c.CharacterId == "violet" && c.PlayedBy == "Ada"))) return;
                await Task.Delay(100);
            }
            Assert.Fail("The TV on server A never heard about the choice made on server B.");
        }
        finally
        {
            await ((IAsyncLifetime)b).DisposeAsync();
            await ((IAsyncLifetime)a).DisposeAsync(); // the owner drops the shared database last
        }
    }
}
