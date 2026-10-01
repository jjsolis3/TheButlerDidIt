using Npgsql;

namespace ButlerDidIt.Api.Scale;

/// <summary>Settings for running more than one copy of the app (see "Running more than one server" in docs/deploy-coolify.md).</summary>
public sealed class ScaleOptions
{
    /// <summary>
    /// True when several app servers share one database. Party locks, the ticker and other
    /// once-only work then coordinate through the database instead of inside one process.
    /// </summary>
    public bool MultiInstance { get; set; }

    /// <summary>Redis connection string for the SignalR backplane, so a guest on one server sees updates made on another.</summary>
    public string? Redis { get; set; }
}

public static class ScaleDefaults
{
    /// <summary>
    /// With several servers, a "Running" job with no progress for this long is treated as interrupted.
    /// Jobs report progress after every step (every picture, every draft), which is far more often.
    /// </summary>
    public static readonly TimeSpan StaleJobAfter = TimeSpan.FromMinutes(10);
}

/// <summary>
/// A lock shared by every app server, built on PostgreSQL advisory locks.
///
/// Why Postgres and not Redis: every server already talks to the database, so this adds
/// nothing to run, and the lock lives exactly as long as the database connection that holds it.
/// If a server crashes mid-command, its connection drops and Postgres frees the lock by itself.
///
/// Each lock is a transaction-level lock (pg_advisory_xact_lock) on its own connection, so it
/// is released when that transaction ends, even if the connection goes back to the pool.
///
/// On a single server (MultiInstance off) every call returns a lock that does nothing, and
/// the in-process locks (PartyLocks) are all that's needed.
/// </summary>
public sealed class ClusterLock(NpgsqlDataSource? dataSource)
{
    public bool Enabled => dataSource is not null;

    /// <summary>
    /// Waits until no other server holds <paramref name="key"/>: for up to <paramref name="timeout"/>,
    /// or the 30 seconds any database command gets when it's null.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct, TimeSpan? timeout = null) =>
        await LockAsync(key, wait: true, ct, timeout) ?? throw new InvalidOperationException("A waiting lock always succeeds.");

    /// <summary>Takes <paramref name="key"/> if it's free, or returns null at once if another server holds it.</summary>
    public Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken ct) => LockAsync(key, wait: false, ct, null);

    private async Task<IAsyncDisposable?> LockAsync(string key, bool wait, CancellationToken ct, TimeSpan? timeout)
    {
        if (dataSource is null) return NoLock.Instance;

        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            // hashtextextended turns any text key into the 64-bit number advisory locks use.
            await using var cmd = new NpgsqlCommand(
                wait ? "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))" : "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))",
                connection, transaction);
            cmd.Parameters.AddWithValue("key", key);
            if (timeout is { } limit) cmd.CommandTimeout = (int)Math.Ceiling(limit.TotalSeconds);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (!wait && result is false)
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
                return null;
            }
            return new Held(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync(); // closing the connection also frees any lock it took
            throw;
        }
    }

    private sealed class Held(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.CommitAsync(); // ending the transaction releases the lock
            }
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
