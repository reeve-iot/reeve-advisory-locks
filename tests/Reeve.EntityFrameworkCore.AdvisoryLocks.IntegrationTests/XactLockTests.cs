using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class XactLockTests(PostgresFixture postgres)
{
    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task An_exclusive_lock_blocks_another_exclusive_lock_until_the_holder_commits(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var waiter = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);

        var waiting = waiter.LockAsync(key);
        await postgres.WaitUntilBlockedAsync(waiter.Connection);
        Assert.False(waiting.IsCompleted);

        await holder.CommitAsync();
        await waiting.Within();
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task Shared_locks_admit_each_other_and_block_an_exclusive_lock(Transport transport)
    {
        var key = Keys.Fresh();
        await using var firstReader = await TxSession.BeginAsync(postgres, transport);
        await using var secondReader = await TxSession.BeginAsync(postgres, transport);
        await using var writer = await TxSession.BeginAsync(postgres, transport);
        await firstReader.LockAsync(key, AdvisoryLockMode.Shared);

        await secondReader.LockAsync(key, AdvisoryLockMode.Shared).Within();
        Assert.False(await writer.TryAsync(key));

        var waiting = writer.LockAsync(key);
        await postgres.WaitUntilBlockedAsync(writer.Connection);
        await firstReader.CommitAsync();
        Assert.False(waiting.IsCompleted);

        await secondReader.CommitAsync();
        await waiting.Within();
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_shared_holder_refuses_an_exclusive_try_and_an_exclusive_holder_refuses_a_shared_try(Transport transport)
    {
        var shared = Keys.Fresh();
        var exclusive = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(shared, AdvisoryLockMode.Shared);
        await holder.LockAsync(exclusive);

        Assert.False(await contender.TryAsync(shared));
        Assert.True(await contender.TryAsync(shared, AdvisoryLockMode.Shared));
        Assert.False(await contender.TryAsync(exclusive, AdvisoryLockMode.Shared));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task Try_takes_a_free_lock_and_returns_false_when_another_transaction_holds_it(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);

        Assert.True(await holder.TryAsync(key));
        Assert.False(await contender.TryAsync(key));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task The_lock_is_released_at_commit(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        Assert.False(await contender.TryAsync(key));

        await holder.CommitAsync();

        Assert.True(await contender.TryAsync(key));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task The_lock_is_released_at_rollback(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        Assert.False(await contender.TryAsync(key));

        await holder.RollbackAsync();

        Assert.True(await contender.TryAsync(key));
    }

    [Fact]
    public async Task Without_a_transaction_the_entity_framework_forms_throw()
    {
        await using var context = new ProbeContext(
            new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(postgres.ConnectionString).Options);
        var key = Keys.Fresh();

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.AdvisoryXactLockAsync(key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.TryAdvisoryXactLockAsync(key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.TryAdvisoryXactLockAsync(key, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_finished_npgsql_transaction_throws()
    {
        await using var connection = await postgres.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.CommitAsync();
        var key = Keys.Fresh();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.AdvisoryXactLockAsync(key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.TryAdvisoryXactLockAsync(key));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_gives_up_after_the_timeout_and_leaves_the_transaction_usable(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        var before = await contender.ScalarAsync("SELECT current_setting('lock_timeout')");

        var taken = await contender.TryAsync(key, TimeSpan.FromMilliseconds(200));

        Assert.False(taken);
        Assert.Equal(1, await contender.ScalarAsync("SELECT 1"));
        Assert.Equal(before, await contender.ScalarAsync("SELECT current_setting('lock_timeout')"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_takes_the_lock_when_the_holder_ends_within_the_timeout(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);

        var pending = contender.TryAsync(key, TimeSpan.FromSeconds(30));
        await postgres.WaitUntilBlockedAsync(contender.Connection);
        await holder.CommitAsync();

        Assert.True(await pending.Within());
        Assert.Equal("0", await contender.ScalarAsync("SELECT current_setting('lock_timeout')"));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_on_a_free_lock_takes_it_and_keeps_the_callers_lock_timeout(Transport transport)
    {
        var key = Keys.Fresh();
        await using var session = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await session.ScalarAsync("SELECT set_config('lock_timeout', '45s', true)");

        Assert.True(await session.TryAsync(key, TimeSpan.FromMilliseconds(200)));

        Assert.Equal("45s", await session.ScalarAsync("SELECT current_setting('lock_timeout')"));
        Assert.False(await contender.TryAsync(key));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_timed_try_with_a_zero_timeout_does_not_wait(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);

        Assert.False(await contender.TryAsync(key, TimeSpan.Zero).Within());
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_negative_timeout_is_refused(Transport transport)
    {
        await using var session = await TxSession.BeginAsync(postgres, transport);

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => session.TryAsync(Keys.Fresh(), TimeSpan.FromMilliseconds(-5)));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task Cancelling_a_blocked_wait_ends_it_with_a_cancellation(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var waiter = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        using var cancel = new CancellationTokenSource();

        var waiting = waiter.LockAsync(key, cancellationToken: cancel.Token);
        await postgres.WaitUntilBlockedAsync(waiter.Connection);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Within());
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task The_same_id_in_two_namespaces_is_two_locks(Transport transport)
    {
        var id = Random.Shared.NextInt64(1L << 40);
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(AdvisoryLockKey.Of(Keys.Primary, id));

        Assert.True(await contender.TryAsync(AdvisoryLockKey.Of(Keys.Other, id)));
        Assert.False(await contender.TryAsync(AdvisoryLockKey.Of(Keys.Primary, id)));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task The_same_name_in_two_namespaces_is_two_locks(Transport transport)
    {
        var name = Guid.NewGuid().ToString("N");
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(AdvisoryLockKey.Of(Keys.Primary, name));

        Assert.True(await contender.TryAsync(AdvisoryLockKey.Of(Keys.Other, name)));
        Assert.False(await contender.TryAsync(AdvisoryLockKey.Of(Keys.Primary, name)));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_name_with_quotes_and_unicode_is_one_stable_lock(Transport transport)
    {
        var key = AdvisoryLockKey.Of(Keys.Primary, "it's \"odd\" ; żółw-" + Guid.NewGuid());
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);

        Assert.False(await contender.TryAsync(key));
    }

    [Fact]
    public async Task A_won_timed_try_leaves_lock_timeout_unchanged_after_commit()
    {
        var key = Keys.Fresh();
        await using var connection = await postgres.OpenAsync();
        var before = await CurrentLockTimeoutAsync(connection);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await transaction.TryAdvisoryXactLockAsync(key, TimeSpan.FromMilliseconds(200)));
            await transaction.CommitAsync();
        }

        Assert.Equal(before, await CurrentLockTimeoutAsync(connection));
    }

    [Fact]
    public async Task A_won_timed_try_does_not_turn_a_local_lock_timeout_into_a_session_one()
    {
        var key = Keys.Fresh();
        await using var connection = await postgres.OpenAsync();
        var before = await CurrentLockTimeoutAsync(connection);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var local = new NpgsqlCommand("SELECT set_config('lock_timeout', '45s', true)", connection, transaction))
            {
                await local.ExecuteScalarAsync();
            }

            Assert.True(await transaction.TryAdvisoryXactLockAsync(key, TimeSpan.FromMilliseconds(200)));
            await transaction.CommitAsync();
        }

        Assert.Equal(before, await CurrentLockTimeoutAsync(connection));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_lost_timed_try_keeps_the_callers_own_lock_timeout(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        await contender.ScalarAsync("SELECT set_config('lock_timeout', '45s', true)");

        Assert.False(await contender.TryAsync(key, TimeSpan.FromMilliseconds(200)));

        Assert.Equal("45s", await contender.ScalarAsync("SELECT current_setting('lock_timeout')"));
    }

    [Fact]
    public async Task A_won_timed_try_does_not_turn_a_local_statement_timeout_into_a_session_one()
    {
        var key = Keys.Fresh();
        await using var connection = await postgres.OpenAsync();
        var before = await CurrentSettingAsync(connection, "statement_timeout");

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var local = new NpgsqlCommand("SELECT set_config('statement_timeout', '45s', true)", connection, transaction))
            {
                await local.ExecuteScalarAsync();
            }

            Assert.True(await transaction.TryAdvisoryXactLockAsync(key, TimeSpan.FromMilliseconds(200)));
            await transaction.CommitAsync();
        }

        Assert.Equal(before, await CurrentSettingAsync(connection, "statement_timeout"));
    }

    private static async Task<object?> CurrentLockTimeoutAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT current_setting('lock_timeout')", connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<object?> CurrentSettingAsync(NpgsqlConnection connection, string name)
    {
        await using var command = new NpgsqlCommand("SELECT current_setting(@name)", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync();
    }
}
