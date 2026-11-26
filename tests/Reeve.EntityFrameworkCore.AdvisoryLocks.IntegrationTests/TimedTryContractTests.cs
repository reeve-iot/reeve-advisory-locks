using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class TimedTryContractTests(PostgresFixture postgres)
{
    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task Server_statement_timeout_shorter_than_the_wait_does_not_end_it(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        await contender.ScalarAsync("SELECT set_config('statement_timeout', '1000', true)");
        var before = await contender.ScalarAsync("SELECT current_setting('statement_timeout')");

        Assert.False(await contender.TryAsync(key, TimeSpan.FromSeconds(3)).Within());

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
        Assert.Equal(before, await contender.ScalarAsync("SELECT current_setting('statement_timeout')"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_won_timed_try_gives_the_callers_statement_timeout_back(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        await contender.ScalarAsync("SELECT set_config('statement_timeout', '10000', true)");

        var pending = contender.TryAsync(key, TimeSpan.FromSeconds(30));
        await postgres.WaitUntilBlockedAsync(contender.Connection);
        await holder.CommitAsync();

        Assert.True(await pending.Within());
        Assert.Equal("10s", await contender.ScalarAsync("SELECT current_setting('statement_timeout')"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_command_timeout_not_longer_than_the_wait_is_refused_before_anything_runs(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => contender.TryAsync(key, TimeSpan.FromSeconds(3), commandTimeout: TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => contender.TryAsync(key, TimeSpan.FromSeconds(3), commandTimeout: TimeSpan.FromSeconds(3)));

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_command_timeout_longer_than_the_wait_is_used_in_place_of_the_margin(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport, ambientCommandTimeout: 1);
        await holder.LockAsync(key);

        Assert.False(await contender.TryAsync(key, TimeSpan.FromMilliseconds(300), commandTimeout: Timeout.InfiniteTimeSpan).Within());
        Assert.False(await contender.TryAsync(key, TimeSpan.FromMilliseconds(300), commandTimeout: TimeSpan.FromSeconds(10)).Within());

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_command_timeout_just_above_the_wait_does_not_let_the_client_answer_first(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);

        Assert.False(await contender.TryAsync(key, TimeSpan.FromMilliseconds(2999), commandTimeout: TimeSpan.FromSeconds(3)).Within());
        Assert.False(await contender.TryAsync(key, TimeSpan.FromSeconds(2), commandTimeout: TimeSpan.FromSeconds(3)).Within());

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_shared_timed_try_is_admitted_next_to_a_shared_holder_and_refused_next_to_an_exclusive_one(Transport transport)
    {
        var shared = Keys.Fresh();
        var exclusive = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var reader = await TxSession.BeginAsync(postgres, transport);
        await using var writer = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(shared, AdvisoryLockMode.Shared);
        await holder.LockAsync(exclusive);

        Assert.True(await reader.TryAsync(shared, TimeSpan.FromMilliseconds(300), AdvisoryLockMode.Shared).Within());
        Assert.False(await reader.TryAsync(exclusive, TimeSpan.FromMilliseconds(300), AdvisoryLockMode.Shared).Within());
        Assert.False(await writer.TryAsync(shared, TimeSpan.FromMilliseconds(300)).Within());

        Assert.Equal(1, await reader.ScalarAsync("SELECT 1"));
        Assert.Equal(1, await writer.ScalarAsync("SELECT 1"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task Cancelling_a_timed_try_rethrows_the_cancellation_and_leaves_the_transaction_usable(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        await contender.ScalarAsync("SELECT set_config('lock_timeout', '45s', true)");
        using var cancel = new CancellationTokenSource();

        var waiting = contender.TryAsync(key, TimeSpan.FromSeconds(30), cancellationToken: cancel.Token);
        await postgres.WaitUntilBlockedAsync(contender.Connection);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Within());
        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
        Assert.Equal("45s", await contender.ScalarAsync("SELECT current_setting('lock_timeout')"));
        await holder.CommitAsync();
        Assert.True(await contender.TryAsync(key));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_with_an_already_cancelled_token_throws_the_cancellation_and_leaves_the_transaction_usable(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        await contender.ScalarAsync("SELECT set_config('lock_timeout', '45s', true)");
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contender.TryAsync(key, TimeSpan.FromSeconds(30), cancellationToken: cancel.Token).Within());

        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
        Assert.Equal("45s", await contender.ScalarAsync("SELECT current_setting('lock_timeout')"));
        Assert.False(await contender.TryAsync(key, TimeSpan.FromMilliseconds(300)).Within());
        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
    }

    [Fact]
    public async Task A_cleanup_that_fails_on_a_lost_connection_does_not_replace_the_cancellation()
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, Transport.Npgsql);
        await using var proxy = new FaultyProxy(postgres.ConnectionString);
        await using var connection = new NpgsqlConnection(proxy.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await holder.LockAsync(key);
        using var cancel = new CancellationTokenSource();

        var waiting = transaction.TryAdvisoryXactLockAsync(key, TimeSpan.FromSeconds(30), cancellationToken: cancel.Token);
        await postgres.WaitUntilBlockedAsync(connection);
        proxy.SeverOpenConnections();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(40)));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_deadlock_is_rethrown_and_leaves_the_transaction_with_its_earlier_locks(Transport transport)
    {
        var first = Keys.Fresh();
        var second = Keys.Fresh();
        await using var other = await TxSession.BeginAsync(postgres, transport);
        await using var victim = await TxSession.BeginAsync(postgres, transport);
        await other.LockAsync(first);
        await victim.LockAsync(second);
        await other.ScalarAsync("SELECT set_config('deadlock_timeout', '60s', true)");
        var otherWaits = other.LockAsync(second);
        await postgres.WaitUntilBlockedAsync(other.Connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => victim.TryAsync(first, TimeSpan.FromSeconds(30)).Within());

        Assert.Equal(PostgresErrorCodes.DeadlockDetected, error.SqlState);
        Assert.Equal(1, await victim.ScalarAsync("SELECT 1"));
        Assert.False(otherWaits.IsCompleted);
        await victim.CommitAsync();
        await otherWaits.Within();
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_lost_connection_surfaces_the_server_error_not_a_failed_cleanup(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);

        var waiting = contender.TryAsync(key, TimeSpan.FromSeconds(30));
        await postgres.WaitUntilBlockedAsync(contender.Connection);
        await postgres.ScalarAsync<bool>($"SELECT pg_terminate_backend({contender.Connection.ProcessID})");

        var error = await Assert.ThrowsAsync<PostgresException>(() => waiting.Within());
        Assert.Equal(PostgresErrorCodes.AdminShutdown, error.SqlState);
    }
}
