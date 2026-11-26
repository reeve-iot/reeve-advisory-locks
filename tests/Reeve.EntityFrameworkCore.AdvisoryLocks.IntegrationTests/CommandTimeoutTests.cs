using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CommandTimeoutTests(PostgresFixture postgres)
{
    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_longer_than_the_ambient_command_timeout_answers_false_and_leaves_the_transaction_usable(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 1);
        await holder.LockAsync(key);

        Assert.False(await contender.TryAsync(key, TimeSpan.FromSeconds(3)).Within());

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_with_no_ambient_command_timeout_still_ends_at_its_own_timeout(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 0);
        await holder.LockAsync(key);

        Assert.False(await contender.TryAsync(key, TimeSpan.FromMilliseconds(300)).Within());

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_longer_than_the_package_margin_still_answers_false_under_a_short_ambient_timeout(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 1);
        await holder.LockAsync(key);

        Assert.False(await contender.TryAsync(key, TimeSpan.FromSeconds(6)).Within());

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
    }

    [Fact]
    public async Task A_timed_try_answers_false_when_the_servers_answer_arrives_after_the_rounded_up_timeout()
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, Transport.Npgsql);
        await using var proxy = new FaultyProxy(postgres.ConnectionString);
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(proxy.ConnectionString) { CancellationTimeout = 500 }.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await holder.LockAsync(key);
        proxy.DelayAnswersContaining(PostgresErrorCodes.LockNotAvailable, TimeSpan.FromSeconds(2.5));

        var answer = await transaction.TryAdvisoryXactLockAsync(key, TimeSpan.FromMilliseconds(600)).WaitAsync(TimeSpan.FromSeconds(40));

        Assert.False(answer);
        await using var usable = new NpgsqlCommand("SELECT 1", connection, transaction);
        Assert.Equal(1, await usable.ExecuteScalarAsync());
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_sub_second_command_timeout_ends_a_wait_instead_of_removing_the_limit(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var waiter = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 120);
        await holder.LockAsync(key);

        await Waits.EndsByItsCommandTimeout(token => waiter.LockAsync(key, commandTimeout: TimeSpan.FromMilliseconds(500), cancellationToken: token), TimeSpan.FromSeconds(8));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task An_untimed_wait_ends_at_the_explicit_command_timeout_not_the_ambient_one(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var waiter = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 120);
        await holder.LockAsync(key);

        await Waits.EndsByItsCommandTimeout(token => waiter.LockAsync(key, commandTimeout: TimeSpan.FromSeconds(1), cancellationToken: token), TimeSpan.FromSeconds(8));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task An_untimed_wait_without_an_explicit_command_timeout_ends_at_the_ambient_one(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var waiter = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 1);
        await holder.LockAsync(key);

        await Waits.EndsByItsCommandTimeout(token => waiter.LockAsync(key, cancellationToken: token), TimeSpan.FromSeconds(8));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task An_infinite_command_timeout_outlasts_a_short_ambient_one(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var waiter = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 1);
        await holder.LockAsync(key);

        var waiting = waiter.LockAsync(key, commandTimeout: Timeout.InfiniteTimeSpan);
        await postgres.WaitUntilBlockedAsync(waiter.Connection);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(waiting.IsCompleted);

        await holder.CommitAsync();
        await waiting.Within();
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_session_wait_ends_at_the_explicit_command_timeout_not_the_ambient_one(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key);
        await using var waiter = await SessionTarget.OpenAsync(postgres, transport, ambientCommandTimeout: 120);

        await Waits.EndsByItsCommandTimeout(token => waiter.LockAsync(key, TimeSpan.FromSeconds(1), token), TimeSpan.FromSeconds(8));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_session_wait_without_an_explicit_command_timeout_ends_at_the_ambient_one(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key);
        await using var waiter = await SessionTarget.OpenAsync(postgres, transport, ambientCommandTimeout: 1);

        await Waits.EndsByItsCommandTimeout(token => waiter.LockAsync(key, null, token), TimeSpan.FromSeconds(8));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task An_infinite_command_timeout_lets_a_session_wait_outlast_a_short_ambient_one(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key);
        await using var waiter = await SessionTarget.OpenAsync(postgres, transport, ambientCommandTimeout: 1);

        var waiting = waiter.LockAsync(key, Timeout.InfiniteTimeSpan);
        await postgres.WaitUntilBlockedAsync(waiter.Connection);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(waiting.IsCompleted);

        await AdvisorySessionLock.UnlockAsync(holder, key);
        await waiting.Within();
    }

    private sealed class SessionTarget : IAsyncDisposable
    {
        private readonly ProbeContext? _context;
        private readonly NpgsqlConnection? _connection;

        private SessionTarget(ProbeContext? context, NpgsqlConnection? connection)
        {
            _context = context;
            _connection = connection;
        }

        public NpgsqlConnection Connection => _connection ?? (NpgsqlConnection)_context!.Database.GetDbConnection();

        public static async Task<SessionTarget> OpenAsync(PostgresFixture postgres, Transport transport, int ambientCommandTimeout)
        {
            if (transport == Transport.Npgsql)
            {
                return new SessionTarget(null, await postgres.OpenAsync(ambientCommandTimeout));
            }

            var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>()
                .UseNpgsql(postgres.ConnectionString, o => o.CommandTimeout(ambientCommandTimeout)).Options);
            await context.Database.OpenConnectionAsync();
            return new SessionTarget(context, null);
        }

        public Task LockAsync(AdvisoryLockKey key, TimeSpan? commandTimeout, CancellationToken cancellationToken = default) =>
            _context is not null
                ? AdvisorySessionLock.LockAsync(_context.Database, key, commandTimeout: commandTimeout, cancellationToken: cancellationToken)
                : AdvisorySessionLock.LockAsync(_connection!, key, commandTimeout: commandTimeout, cancellationToken: cancellationToken);

        public async ValueTask DisposeAsync()
        {
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
}
