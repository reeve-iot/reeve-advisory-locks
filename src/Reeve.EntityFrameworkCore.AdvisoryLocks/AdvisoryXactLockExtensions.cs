using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

/// <summary>Transaction-scoped advisory locks on the current transaction, released at its commit or rollback; without one they throw.</summary>
/// <remarks>A wait ends by whatever ends any statement: its command timeout, <c>statement_timeout</c> or a cancellation; the transaction is then aborted.</remarks>
public static class AdvisoryXactLockExtensions
{
    private const string SavepointName = "advisory_lock_wait";

    private static readonly TimeSpan CommandTimeoutMargin = TimeSpan.FromSeconds(5);

    /// <summary>Takes the lock, waiting while another transaction holds a conflicting one, until the command timeout or any other end of a statement.</summary>
    public static Task AdvisoryXactLockAsync(
        this DatabaseFacade database,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var (transaction, ambient) = Current(database);
        return LockAsync(transaction, key, mode, AdvisoryLockSql.CommandTimeoutSeconds(commandTimeout) ?? ambient, cancellationToken);
    }

    /// <summary>Takes the lock when it is free right now and answers <see langword="false"/> when it is not; never waits.</summary>
    public static Task<bool> TryAdvisoryXactLockAsync(
        this DatabaseFacade database,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        CancellationToken cancellationToken = default)
    {
        var (transaction, ambient) = Current(database);
        return TryAsync(transaction, key, mode, ambient, cancellationToken);
    }

    /// <summary>Takes the lock, waiting up to <paramref name="timeout"/>; <see langword="false"/> when still held, the transaction usable.
    /// The wait suspends <c>statement_timeout</c> and has a command timeout of at least <paramref name="timeout"/> plus a margin, so the server answers first;
    /// a <paramref name="commandTimeout"/> below that is raised to it, one not longer than <paramref name="timeout"/> is refused.</summary>
    /// <remarks>The wait runs behind a savepoint, because a failing statement aborts the whole transaction unless one stands behind it.
    /// A cancellation or a server error such as a deadlock rolls back to the savepoint and is rethrown, the transaction usable; a lost connection cannot be rolled back and is rethrown as is.
    /// Once the lock is won the token is no longer observed, so a late cancellation cannot abort a transaction that holds the lock.</remarks>
    public static Task<bool> TryAdvisoryXactLockAsync(
        this DatabaseFacade database,
        AdvisoryLockKey key,
        TimeSpan timeout,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var (transaction, ambient) = Current(database);
        return TryAsync(transaction, key, timeout, mode, commandTimeout, ambient, cancellationToken);
    }

    /// <inheritdoc cref="AdvisoryXactLockAsync(DatabaseFacade, AdvisoryLockKey, AdvisoryLockMode, TimeSpan?, CancellationToken)"/>
    public static Task AdvisoryXactLockAsync(
        this NpgsqlTransaction transaction,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default) =>
        LockAsync(transaction, key, mode, AdvisoryLockSql.CommandTimeoutSeconds(commandTimeout), cancellationToken);

    /// <inheritdoc cref="TryAdvisoryXactLockAsync(DatabaseFacade, AdvisoryLockKey, AdvisoryLockMode, CancellationToken)"/>
    public static Task<bool> TryAdvisoryXactLockAsync(
        this NpgsqlTransaction transaction,
        AdvisoryLockKey key,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        CancellationToken cancellationToken = default) =>
        TryAsync(transaction, key, mode, null, cancellationToken);

    /// <inheritdoc cref="TryAdvisoryXactLockAsync(DatabaseFacade, AdvisoryLockKey, TimeSpan, AdvisoryLockMode, TimeSpan?, CancellationToken)"/>
    public static Task<bool> TryAdvisoryXactLockAsync(
        this NpgsqlTransaction transaction,
        AdvisoryLockKey key,
        TimeSpan timeout,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default) =>
        TryAsync(transaction, key, timeout, mode, commandTimeout, null, cancellationToken);

    private static (NpgsqlTransaction Transaction, int? CommandTimeout) Current(DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var current = database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "A transaction-scoped advisory lock needs a transaction to end it; begin one on the context first.");
        if (current.GetDbTransaction() is not NpgsqlTransaction transaction)
        {
            throw new InvalidOperationException("Advisory locks need the Npgsql provider; this context's transaction is not an Npgsql transaction.");
        }

        return (transaction, database.GetCommandTimeout());
    }

    private static NpgsqlConnection ConnectionOf(NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection
            ?? throw new InvalidOperationException("The transaction has ended, so a transaction-scoped advisory lock has nothing to end it.");
    }

    private static async Task LockAsync(
        NpgsqlTransaction transaction, AdvisoryLockKey key, AdvisoryLockMode mode, int? commandTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var connection = ConnectionOf(transaction);
        using var command = AdvisoryLockSql.Command(connection, transaction, AdvisoryLockSql.Operation.XactLock, mode, key, commandTimeout);
        await AdvisoryLockActivity.LockAsync(command, key, mode, session: false, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UntracedLockAsync(
        NpgsqlTransaction transaction, AdvisoryLockKey key, AdvisoryLockMode mode, int? commandTimeout, CancellationToken cancellationToken)
    {
        using var command = AdvisoryLockSql.Command(ConnectionOf(transaction), transaction, AdvisoryLockSql.Operation.XactLock, mode, key, commandTimeout);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryAsync(
        NpgsqlTransaction transaction, AdvisoryLockKey key, AdvisoryLockMode mode, int? commandTimeout, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var connection = ConnectionOf(transaction);
        using var command = AdvisoryLockSql.Command(connection, transaction, AdvisoryLockSql.Operation.TryXactLock, mode, key, commandTimeout);
        return await AdvisoryLockActivity.TryAsync(command, key, mode, session: false, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryAsync(
        NpgsqlTransaction transaction,
        AdvisoryLockKey key,
        TimeSpan timeout,
        AdvisoryLockMode mode,
        TimeSpan? commandTimeout,
        int? ambientCommandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between zero and int.MaxValue milliseconds.");
        }

        var explicitSeconds = AdvisoryLockSql.CommandTimeoutSeconds(commandTimeout);
        if (commandTimeout is { } given && given != Timeout.InfiniteTimeSpan && given <= timeout)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), given, "A command timeout that is not longer than the wait would cut the wait off.");
        }

        if (timeout == TimeSpan.Zero)
        {
            return await TryAsync(transaction, key, mode, explicitSeconds ?? ambientCommandTimeout, cancellationToken, timeout).ConfigureAwait(false);
        }

        var connection = ConnectionOf(transaction);
        var activity = AdvisoryLockActivity.Start(key, mode, session: false, isTry: true, timeout);
        bool acquired;
        try
        {
            acquired = await TimedAsync(transaction, connection, key, timeout, mode, explicitSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AdvisoryLockActivity.Stop(activity, ex);
            throw;
        }

        AdvisoryLockActivity.Stop(activity, acquired ? AdvisoryLockActivity.Outcome.Acquired : AdvisoryLockActivity.Outcome.Refused);
        return acquired;
    }

    private static async Task<bool> TimedAsync(
        NpgsqlTransaction transaction,
        NpgsqlConnection connection,
        AdvisoryLockKey key,
        TimeSpan timeout,
        AdvisoryLockMode mode,
        int? explicitSeconds,
        CancellationToken cancellationToken)
    {
        var margin = AdvisoryLockSql.CommandTimeoutSeconds(timeout + CommandTimeoutMargin)!.Value;
        var waitCommandTimeout = explicitSeconds == 0 ? 0 : Math.Max(explicitSeconds ?? 0, margin);
        await transaction.SaveAsync(SavepointName, cancellationToken).ConfigureAwait(false);
        try
        {
            var (lockTimeout, statementTimeout) = await ReadTimeoutsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await SetTimeoutsAsync(connection, transaction, $"{(int)Math.Ceiling(timeout.TotalMilliseconds)}ms", "0", cancellationToken).ConfigureAwait(false);
            try
            {
                await UntracedLockAsync(transaction, key, mode, waitCommandTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                await UndoAsync(transaction).ConfigureAwait(false);
                return false;
            }

            await SetTimeoutsAsync(connection, transaction, lockTimeout, statementTimeout, CancellationToken.None).ConfigureAwait(false);
            await transaction.ReleaseAsync(SavepointName, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is PostgresException or OperationCanceledException)
        {
            await UndoAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task UndoAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(SavepointName, CancellationToken.None).ConfigureAwait(false);
            await transaction.ReleaseAsync(SavepointName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ObjectDisposedException or IOException)
        {
        }
    }

    private static async Task<(string LockTimeout, string StatementTimeout)> ReadTimeoutsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand("SELECT current_setting('lock_timeout'), current_setting('statement_timeout')", connection, transaction);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task SetTimeoutsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string lockTimeout, string statementTimeout, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand(
            "SELECT set_config('lock_timeout', @lock, true), set_config('statement_timeout', @statement, true)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("lock", NpgsqlTypes.NpgsqlDbType.Text) { Value = lockTimeout });
        command.Parameters.Add(new NpgsqlParameter("statement", NpgsqlTypes.NpgsqlDbType.Text) { Value = statementTimeout });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
