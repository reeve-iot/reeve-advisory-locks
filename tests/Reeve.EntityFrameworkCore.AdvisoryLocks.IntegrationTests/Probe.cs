using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

public sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options);

public enum Transport
{
    EntityFramework,
    Npgsql,
}

public static class Transports
{
    public static TheoryData<Transport> All => new() { Transport.EntityFramework, Transport.Npgsql };
}

public sealed class TxSession : IAsyncDisposable
{
    private readonly Transport _transport;
    private readonly ProbeContext? _context;
    private readonly NpgsqlConnection? _connection;
    private NpgsqlTransaction? _npgsqlTransaction;
    private bool _finished;

    private TxSession(Transport transport, ProbeContext? context, NpgsqlConnection? connection)
    {
        _transport = transport;
        _context = context;
        _connection = connection;
    }

    public NpgsqlConnection Connection =>
        _connection ?? (NpgsqlConnection)_context!.Database.GetDbConnection();

    public static async Task<TxSession> BeginAsync(PostgresFixture postgres, Transport transport, int? ambientCommandTimeout = null)
    {
        if (transport == Transport.EntityFramework)
        {
            var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>()
                .UseNpgsql(postgres.ConnectionString, o => { if (ambientCommandTimeout is { } seconds) { o.CommandTimeout(seconds); } }).Options);
            await context.Database.BeginTransactionAsync();
            return new TxSession(transport, context, null);
        }

        var connection = await postgres.OpenAsync(ambientCommandTimeout);
        var session = new TxSession(transport, null, connection);
        session._npgsqlTransaction = await connection.BeginTransactionAsync();
        return session;
    }

    public Task LockAsync(
        AdvisoryLockKey key, AdvisoryLockMode mode = AdvisoryLockMode.Exclusive, TimeSpan? commandTimeout = null, CancellationToken cancellationToken = default) =>
        _transport == Transport.EntityFramework
            ? _context!.Database.AdvisoryXactLockAsync(key, mode, commandTimeout, cancellationToken)
            : _npgsqlTransaction!.AdvisoryXactLockAsync(key, mode, commandTimeout, cancellationToken);

    public Task<bool> TryAsync(AdvisoryLockKey key, AdvisoryLockMode mode = AdvisoryLockMode.Exclusive, CancellationToken cancellationToken = default) =>
        _transport == Transport.EntityFramework
            ? _context!.Database.TryAdvisoryXactLockAsync(key, mode, cancellationToken)
            : _npgsqlTransaction!.TryAdvisoryXactLockAsync(key, mode, cancellationToken);

    public Task<bool> TryAsync(
        AdvisoryLockKey key,
        TimeSpan timeout,
        AdvisoryLockMode mode = AdvisoryLockMode.Exclusive,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default) =>
        _transport == Transport.EntityFramework
            ? _context!.Database.TryAdvisoryXactLockAsync(key, timeout, mode, commandTimeout, cancellationToken)
            : _npgsqlTransaction!.TryAdvisoryXactLockAsync(key, timeout, mode, commandTimeout, cancellationToken);

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _npgsqlTransaction ?? _context!.Database.CurrentTransaction!.GetDbTransaction() as NpgsqlTransaction;
        return await command.ExecuteScalarAsync();
    }

    public async Task CommitAsync()
    {
        _finished = true;
        if (_context is not null)
        {
            await _context.Database.CommitTransactionAsync();
        }
        else
        {
            await _npgsqlTransaction!.CommitAsync();
        }
    }

    public async Task RollbackAsync()
    {
        _finished = true;
        if (_context is not null)
        {
            await _context.Database.RollbackTransactionAsync();
        }
        else
        {
            await _npgsqlTransaction!.RollbackAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            try
            {
                await RollbackAsync();
            }
            catch (Exception)
            {
            }
        }

        if (_context is not null)
        {
            await _context.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}

public static class Keys
{
    public static readonly AdvisoryLockNamespace Primary = AdvisoryLockNamespace.Register("it.primary", 63001);
    public static readonly AdvisoryLockNamespace Other = AdvisoryLockNamespace.Register("it.other", 63002);

    public static AdvisoryLockKey Fresh() => AdvisoryLockKey.Of(Primary, Random.Shared.NextInt64(1L << 40));

    public static AdvisoryLockKey FreshText() => AdvisoryLockKey.Of(Primary, Guid.NewGuid().ToString("N"));
}

public static class Waits
{
    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(20));

    public static Task Within(this Task task) => task.WaitAsync(TimeSpan.FromSeconds(20));

    public static async Task<NpgsqlException> EndsByItsCommandTimeout(Func<CancellationToken, Task> wait, TimeSpan bound)
    {
        using var cancel = new CancellationTokenSource(bound);
        return await Assert.ThrowsAnyAsync<NpgsqlException>(() => wait(cancel.Token));
    }
}
