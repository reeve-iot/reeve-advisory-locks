using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SessionLockTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_exclusive_session_lock_blocks_another_session_until_it_is_unlocked()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await using var waiter = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key);

        var waiting = AdvisorySessionLock.LockAsync(waiter, key);
        await postgres.WaitUntilBlockedAsync(waiter);
        Assert.False(waiting.IsCompleted);

        Assert.True(await AdvisorySessionLock.UnlockAsync(holder, key));
        await waiting.Within();
    }

    [Fact]
    public async Task Shared_session_locks_admit_each_other_and_block_an_exclusive_try()
    {
        var key = Keys.Fresh();
        await using var first = await postgres.OpenAsync();
        await using var second = await postgres.OpenAsync();
        await using var writer = await postgres.OpenAsync();

        Assert.True(await AdvisorySessionLock.TryLockAsync(first, key, AdvisoryLockMode.Shared));
        Assert.True(await AdvisorySessionLock.TryLockAsync(second, key, AdvisoryLockMode.Shared));
        Assert.False(await AdvisorySessionLock.TryLockAsync(writer, key));
    }

    [Fact]
    public async Task Try_returns_false_under_contention_and_true_once_the_holder_unlocks()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await using var contender = await postgres.OpenAsync();
        Assert.True(await AdvisorySessionLock.TryLockAsync(holder, key));

        Assert.False(await AdvisorySessionLock.TryLockAsync(contender, key));

        Assert.True(await AdvisorySessionLock.UnlockAsync(holder, key));
        Assert.True(await AdvisorySessionLock.TryLockAsync(contender, key));
    }

    [Fact]
    public async Task Closing_the_connection_releases_the_lock()
    {
        var key = Keys.Fresh();
        var holder = await postgres.OpenAsync();
        await using var contender = await postgres.OpenAsync();
        Assert.True(await AdvisorySessionLock.TryLockAsync(holder, key));
        Assert.False(await AdvisorySessionLock.TryLockAsync(contender, key));

        var holderPid = holder.ProcessID;
        await holder.DisposeAsync();
        await postgres.WaitUntilNoAdvisoryLocksAsync(holderPid);

        Assert.True(await AdvisorySessionLock.TryLockAsync(contender, key).Within());
    }

    [Fact]
    public async Task Unlocking_a_lock_the_session_does_not_hold_answers_false()
    {
        await using var connection = await postgres.OpenAsync();

        Assert.False(await AdvisorySessionLock.UnlockAsync(connection, Keys.Fresh()));
    }

    [Fact]
    public async Task A_lock_taken_twice_is_held_until_it_is_unlocked_twice()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await using var contender = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key);
        await AdvisorySessionLock.LockAsync(holder, key);

        await AdvisorySessionLock.UnlockAsync(holder, key);
        Assert.False(await AdvisorySessionLock.TryLockAsync(contender, key));

        await AdvisorySessionLock.UnlockAsync(holder, key);
        Assert.True(await AdvisorySessionLock.TryLockAsync(contender, key));
    }

    [Fact]
    public async Task A_session_lock_survives_the_end_of_a_transaction_on_the_same_connection()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await using var contender = await postgres.OpenAsync();
        await using (var transaction = await holder.BeginTransactionAsync())
        {
            await AdvisorySessionLock.LockAsync(holder, key);
            await transaction.RollbackAsync();
        }

        Assert.False(await AdvisorySessionLock.TryLockAsync(contender, key));
    }

    [Fact]
    public async Task IsHeld_is_true_for_the_holder_and_false_for_a_session_that_only_waits_or_never_asked()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await using var other = await postgres.OpenAsync();

        Assert.False(await AdvisorySessionLock.IsHeldAsync(holder, key));
        await AdvisorySessionLock.LockAsync(holder, key);

        Assert.True(await AdvisorySessionLock.IsHeldAsync(holder, key));
        Assert.False(await AdvisorySessionLock.IsHeldAsync(other, key));

        await AdvisorySessionLock.UnlockAsync(holder, key);
        Assert.False(await AdvisorySessionLock.IsHeldAsync(holder, key));
    }

    [Fact]
    public async Task IsHeld_tells_the_two_modes_apart()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key, AdvisoryLockMode.Shared);

        Assert.True(await AdvisorySessionLock.IsHeldAsync(holder, key, AdvisoryLockMode.Shared));
        Assert.False(await AdvisorySessionLock.IsHeldAsync(holder, key, AdvisoryLockMode.Exclusive));
    }

    [Fact]
    public async Task IsHeld_also_sees_a_transaction_lock_for_as_long_as_the_transaction_lasts()
    {
        var key = Keys.Fresh();
        await using var connection = await postgres.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.AdvisoryXactLockAsync(key);

        Assert.True(await AdvisorySessionLock.IsHeldAsync(connection, key));
        await transaction.CommitAsync();
        Assert.False(await AdvisorySessionLock.IsHeldAsync(connection, key));
    }

    [Theory]
    [MemberData(nameof(KeyKinds))]
    public async Task IsHeld_finds_every_key_kind_and_only_that_key(string kind)
    {
        var key = kind switch
        {
            "name" => Keys.FreshText(),
            "hashed text" => AdvisoryLockKey.FromHashedText(Guid.NewGuid().ToString("N")),
            "int pair" => AdvisoryLockKey.FromIntPair(int.MinValue + 7, -2),
            "negative raw" => AdvisoryLockKey.FromRaw(long.MinValue + Random.Shared.NextInt64(1L << 40)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var sibling = kind == "int pair" ? AdvisoryLockKey.FromIntPair(int.MinValue + 7, -3) : Keys.FreshText();
        await using var holder = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key);

        Assert.True(await AdvisorySessionLock.IsHeldAsync(holder, key));
        Assert.False(await AdvisorySessionLock.IsHeldAsync(holder, sibling));
    }

    public static TheoryData<string> KeyKinds => ["name", "hashed text", "int pair", "negative raw"];

    [Fact]
    public async Task A_closed_connection_is_refused()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AdvisorySessionLock.TryLockAsync(connection, Keys.Fresh()));
    }

    [Fact]
    public async Task The_entity_framework_forms_hold_the_lock_on_the_contexts_open_connection()
    {
        var key = Keys.Fresh();
        await using var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(postgres.ConnectionString).Options);
        await using var contender = await postgres.OpenAsync();
        await context.Database.OpenConnectionAsync();

        Assert.True(await AdvisorySessionLock.TryLockAsync(context.Database, key));
        Assert.True(await AdvisorySessionLock.IsHeldAsync(context.Database, key));
        Assert.False(await AdvisorySessionLock.TryLockAsync(contender, key));

        Assert.True(await AdvisorySessionLock.UnlockAsync(context.Database, key));
        Assert.True(await AdvisorySessionLock.TryLockAsync(contender, key));
    }

    [Fact]
    public async Task Unlocking_a_shared_session_lock_frees_it_for_an_exclusive_one()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await using var writer = await postgres.OpenAsync();
        await AdvisorySessionLock.LockAsync(holder, key, AdvisoryLockMode.Shared);
        Assert.False(await AdvisorySessionLock.TryLockAsync(writer, key));

        Assert.True(await AdvisorySessionLock.UnlockAsync(holder, key, AdvisoryLockMode.Shared));
        Assert.True(await AdvisorySessionLock.TryLockAsync(writer, key));
    }

    [Fact]
    public async Task Closing_a_pooled_connection_keeps_the_session_lock_until_the_pool_drops_the_session()
    {
        var key = Keys.Fresh();
        var pooled = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Pooling = true,
            ApplicationName = "pooled-" + Guid.NewGuid().ToString("N")[..8],
        }.ConnectionString;
        try
        {
            var holder = new NpgsqlConnection(pooled);
            await holder.OpenAsync();
            Assert.True(await AdvisorySessionLock.TryLockAsync(holder, key));
            await holder.DisposeAsync();

            await using var contender = await postgres.OpenAsync();
            Assert.False(await AdvisorySessionLock.TryLockAsync(contender, key));

            NpgsqlConnection.ClearPool(new NpgsqlConnection(pooled));
            await WaitUntilAsync(async () => await AdvisorySessionLock.TryLockAsync(contender, key));
        }
        finally
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(pooled));
        }
    }

    [Fact]
    public async Task The_entity_framework_forms_refuse_a_connection_that_is_not_open_yet()
    {
        await using var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(postgres.ConnectionString).Options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AdvisorySessionLock.TryLockAsync(context.Database, Keys.Fresh()));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition never became true.");
            await Task.Delay(50);
        }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class HandWrittenSessionLockTests(PostgresFixture postgres)
{
    public static TheoryData<string> Cases => HandWrittenForms.SessionCases();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_package_lock_is_the_lock_a_hand_written_try_and_unlock_see(string form)
    {
        var resolved = await HandWrittenForms.ResolveAsync(form, postgres);
        await using var package = await postgres.OpenAsync();
        await using var handWritten = await postgres.OpenAsync();
        Assert.True(await AdvisorySessionLock.TryLockAsync(package, resolved.Key));

        Assert.False(await Scalar<bool>(handWritten, $"SELECT pg_try_advisory_lock({resolved.RawArguments})"));
        Assert.False(await Scalar<bool>(handWritten, $"SELECT pg_try_advisory_lock_shared({resolved.RawArguments})"));
        Assert.True(await Scalar<bool>(package, $"SELECT pg_advisory_unlock({resolved.RawArguments})"));
        Assert.True(await Scalar<bool>(handWritten, $"SELECT pg_try_advisory_lock({resolved.RawArguments})"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_hand_written_lock_refuses_the_package_and_the_package_can_release_it(string form)
    {
        var resolved = await HandWrittenForms.ResolveAsync(form, postgres);
        await using var handWritten = await postgres.OpenAsync();
        await using var package = await postgres.OpenAsync();
        Assert.True(await Scalar<bool>(handWritten, $"SELECT pg_try_advisory_lock({resolved.RawArguments})"));

        Assert.False(await AdvisorySessionLock.TryLockAsync(package, resolved.Key));
        Assert.True(await AdvisorySessionLock.IsHeldAsync(handWritten, resolved.Key));
        Assert.True(await AdvisorySessionLock.UnlockAsync(handWritten, resolved.Key));
        Assert.True(await AdvisorySessionLock.TryLockAsync(package, resolved.Key));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_hand_written_shared_lock_admits_a_shared_package_lock_and_refuses_an_exclusive_one(string form)
    {
        var resolved = await HandWrittenForms.ResolveAsync(form, postgres);
        await using var handWritten = await postgres.OpenAsync();
        await using var package = await postgres.OpenAsync();
        Assert.True(await Scalar<bool>(handWritten, $"SELECT pg_try_advisory_lock_shared({resolved.RawArguments})"));

        Assert.True(await AdvisorySessionLock.TryLockAsync(package, resolved.Key, AdvisoryLockMode.Shared));
        Assert.False(await AdvisorySessionLock.TryLockAsync(package, resolved.Key));
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
