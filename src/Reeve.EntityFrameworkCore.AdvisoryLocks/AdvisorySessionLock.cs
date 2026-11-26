using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

/// <summary>Session-scoped advisory locks, for leader election and start-up steps that outlive a transaction: held by one open connection until unlocked or the session ends.</summary>
/// <remarks>A pooled connection is only returned to the pool on close, so it keeps the lock; use an unpooled one. After a cancelled or failed lock call the hold is unknown.</remarks>
public static class AdvisorySessionLock
{
    /// <summary>Takes the lock, waiting while another session holds a conflicting one, until the command timeout (null: ambient, infinite: none) or any other end of a statement.</summary>
    public static async Task LockAsync(
        NpgsqlConnection connection,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        using var command = Prepare(connection, key, AdvisoryLockSql.Operation.SessionLock, mode, AdvisoryLockSql.CommandTimeoutSeconds(commandTimeout));
        await AdvisoryLockActivity.LockAsync(command, key, mode, session: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the lock when it is free right now and answers <see langword="false"/> when it is not; never waits.</summary>
    public static async Task<bool> TryLockAsync(
        NpgsqlConnection connection,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        using var command = Prepare(connection, key, AdvisoryLockSql.Operation.TrySessionLock, mode, AdvisoryLockSql.CommandTimeoutSeconds(commandTimeout));
        return await AdvisoryLockActivity.TryAsync(command, key, mode, session: true, timeout: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases one hold of the lock; answers <see langword="false"/> when this session did not hold it.</summary>
    public static async Task<bool> UnlockAsync(
        NpgsqlConnection connection,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        using var command = Prepare(connection, key, AdvisoryLockSql.Operation.SessionUnlock, mode, AdvisoryLockSql.CommandTimeoutSeconds(commandTimeout));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Whether this connection's session holds the lock in this mode; a transaction lock on the same key answers true too.</summary>
    public static async Task<bool> IsHeldAsync(
        NpgsqlConnection connection,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(key);
        AdvisoryLockSql.RequireOpen(connection);
        using var command = AdvisoryLockSql.HeldCommand(connection, mode, key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <inheritdoc cref="LockAsync(NpgsqlConnection, AdvisoryLockKey, AdvisoryLockMode, TimeSpan?, CancellationToken)"/>
    /// <remarks>The context's connection must already be open, as EF otherwise closes it after each operation and the server ends the session.</remarks>
    public static async Task LockAsync(
        DatabaseFacade database,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        using var command = Prepare(database, key, AdvisoryLockSql.Operation.SessionLock, mode, commandTimeout);
        await AdvisoryLockActivity.LockAsync(command, key, mode, session: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="TryLockAsync(NpgsqlConnection, AdvisoryLockKey, AdvisoryLockMode, TimeSpan?, CancellationToken)"/>
    /// <remarks>The context's connection must already be open, as EF otherwise closes it after each operation and the server ends the session.</remarks>
    public static async Task<bool> TryLockAsync(
        DatabaseFacade database,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        using var command = Prepare(database, key, AdvisoryLockSql.Operation.TrySessionLock, mode, commandTimeout);
        return await AdvisoryLockActivity.TryAsync(command, key, mode, session: true, timeout: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="UnlockAsync(NpgsqlConnection, AdvisoryLockKey, AdvisoryLockMode, TimeSpan?, CancellationToken)"/>
    /// <remarks>The context's connection must already be open, as EF otherwise closes it after each operation and the server ends the session.</remarks>
    public static async Task<bool> UnlockAsync(
        DatabaseFacade database,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        using var command = Prepare(database, key, AdvisoryLockSql.Operation.SessionUnlock, mode, commandTimeout);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <inheritdoc cref="IsHeldAsync(NpgsqlConnection, AdvisoryLockKey, AdvisoryLockMode, CancellationToken)"/>
    /// <remarks>The context's connection must already be open, as EF otherwise closes it after each operation and the server ends the session.</remarks>
    public static Task<bool> IsHeldAsync(
        DatabaseFacade database,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        CancellationToken cancellationToken = default) =>
        IsHeldAsync(OpenConnection(database), key, mode, cancellationToken);

    private static NpgsqlCommand Prepare(
        NpgsqlConnection connection, AdvisoryLockKey key, AdvisoryLockSql.Operation operation, AdvisoryLockMode mode, int? commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(key);
        AdvisoryLockSql.RequireOpen(connection);
        return AdvisoryLockSql.Command(connection, null, operation, mode, key, commandTimeout);
    }

    private static NpgsqlCommand Prepare(
        DatabaseFacade database, AdvisoryLockKey key, AdvisoryLockSql.Operation operation, AdvisoryLockMode mode, TimeSpan? commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(key);
        var seconds = AdvisoryLockSql.CommandTimeoutSeconds(commandTimeout);
        var connection = OpenConnection(database);
        return AdvisoryLockSql.Command(connection, null, operation, mode, key, seconds ?? database.GetCommandTimeout());
    }

    private static NpgsqlConnection OpenConnection(DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.GetDbConnection() is not NpgsqlConnection connection)
        {
            throw new InvalidOperationException("Advisory locks need the Npgsql provider; this context's connection is not an Npgsql connection.");
        }

        AdvisoryLockSql.RequireOpen(connection);
        return connection;
    }
}
