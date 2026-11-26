using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

public enum Acquire
{
    Wait,
    Try,
    TimedTry,
}

public enum Shape
{
    XactWait,
    XactTry,
    XactZeroTimeoutTry,
    XactTimedTry,
    SessionWait,
    SessionTry,
}

[Collection(PostgresCollection.Name)]
public sealed class TracingTests(PostgresFixture postgres)
{
    public static TheoryData<Transport, Acquire> TransportsAndAcquisitions
    {
        get
        {
            var data = new TheoryData<Transport, Acquire>();
            foreach (var transport in new[] { Transport.EntityFramework, Transport.Npgsql })
            {
                foreach (var acquire in Enum.GetValues<Acquire>())
                {
                    data.Add(transport, acquire);
                }
            }

            return data;
        }
    }

    public static TheoryData<Transport, string, AdvisoryLockMode> KeyFormsAndModes
    {
        get
        {
            var data = new TheoryData<Transport, string, AdvisoryLockMode>();
            foreach (var transport in new[] { Transport.EntityFramework, Transport.Npgsql })
            {
                foreach (var form in new[] { "raw", "namespaced_id", "hashed_text", "namespaced_text", "int_pair" })
                {
                    foreach (var mode in Enum.GetValues<AdvisoryLockMode>())
                    {
                        data.Add(transport, form, mode);
                    }
                }
            }

            return data;
        }
    }

    public static TheoryData<Transport, Shape, AdvisoryLockMode> EveryKindOfAcquisition
    {
        get
        {
            var data = new TheoryData<Transport, Shape, AdvisoryLockMode>();
            foreach (var transport in new[] { Transport.EntityFramework, Transport.Npgsql })
            {
                foreach (var shape in Enum.GetValues<Shape>())
                {
                    foreach (var mode in Enum.GetValues<AdvisoryLockMode>())
                    {
                        data.Add(transport, shape, mode);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TransportsAndAcquisitions))]
    public async Task A_listener_parked_at_the_start_holds_the_transaction_lock_statement_back(Transport transport, Acquire acquire)
    {
        var key = Keys.Fresh();
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await using var other = await TxSession.BeginAsync(postgres, transport);
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = 0;
        using var recorder = new AcquisitionRecorder(_ =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                parked.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });

        var attempt = Task.Run(() => AcquireAsync(contender, acquire, key));
        Assert.True(parked.Wait(TimeSpan.FromSeconds(20)), "the activity never started");

        Assert.Equal(0L, await postgres.ScalarAsync<long>($"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = {contender.Connection.ProcessID}"));
        Assert.True(await other.TryAsync(key));
        release.Set();
        if (acquire == Acquire.Wait)
        {
            await postgres.WaitUntilBlockedAsync(contender.Connection);
            await other.CommitAsync();
        }

        Assert.Equal(acquire == Acquire.Wait, await attempt.Within());
    }

    [Theory]
    [InlineData(Acquire.Wait)]
    [InlineData(Acquire.Try)]
    public async Task A_listener_parked_at_the_start_holds_the_session_lock_statement_back(Acquire acquire)
    {
        var key = Keys.Fresh();
        await using var contender = await postgres.OpenAsync();
        await using var other = await postgres.OpenAsync();
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = 0;
        using var recorder = new AcquisitionRecorder(_ =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                parked.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });

        var attempt = Task.Run(async () =>
        {
            if (acquire == Acquire.Wait)
            {
                await AdvisorySessionLock.LockAsync(contender, key);
                return true;
            }

            return await AdvisorySessionLock.TryLockAsync(contender, key);
        });
        Assert.True(parked.Wait(TimeSpan.FromSeconds(20)), "the activity never started");

        Assert.Equal(0L, await postgres.ScalarAsync<long>($"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = {contender.ProcessID}"));
        var taken = await AdvisorySessionLock.TryLockAsync(other, key);
        release.Set();

        Assert.True(taken);
        if (acquire == Acquire.Wait)
        {
            await postgres.WaitUntilBlockedAsync(contender);
            Assert.True(await AdvisorySessionLock.UnlockAsync(other, key));
        }

        Assert.Equal(acquire == Acquire.Wait, await attempt.Within());
    }

    [Theory]
    [MemberData(nameof(KeyFormsAndModes))]
    public async Task The_key_and_the_mode_are_tagged_before_the_statement_is_sent(Transport transport, string form, AdvisoryLockMode mode)
    {
        var (key, expected) = KeyOfForm(form);
        await using var session = await TxSession.BeginAsync(postgres, transport);
        using var recorder = new AcquisitionRecorder();

        await session.LockAsync(key, mode);

        var seen = Assert.Single(recorder.Stopped);
        expected[Tags.Mode] = mode == AdvisoryLockMode.Shared ? "shared" : "exclusive";
        expected[Tags.Scope] = "transaction";
        expected[Tags.Try] = false;
        Assert.Equal(expected.OrderBy(pair => pair.Key), seen.AtStart.OrderBy(pair => pair.Key));
        Assert.Equal(
            typeof(AdvisoryLockKey).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, seen.SourceVersion);
    }

    [Theory]
    [MemberData(nameof(EveryKindOfAcquisition))]
    public async Task Every_kind_of_acquisition_is_tagged_with_its_mode_scope_and_try_before_the_statement_is_sent(
        Transport transport, Shape shape, AdvisoryLockMode mode)
    {
        var (key, expected) = KeyOfForm("namespaced_id");
        var session = shape is Shape.SessionWait or Shape.SessionTry;
        await using var tx = session ? null : await TxSession.BeginAsync(postgres, transport);
        await using var connection = session && transport == Transport.Npgsql ? await postgres.OpenAsync() : null;
        await using var context = session && transport == Transport.EntityFramework ? await OpenContextAsync() : null;
        using var recorder = new AcquisitionRecorder();

        switch (shape)
        {
            case Shape.XactWait:
                await tx!.LockAsync(key, mode);
                break;
            case Shape.XactTry:
                Assert.True(await tx!.TryAsync(key, mode));
                break;
            case Shape.XactZeroTimeoutTry:
                Assert.True(await tx!.TryAsync(key, TimeSpan.Zero, mode));
                break;
            case Shape.XactTimedTry:
                Assert.True(await tx!.TryAsync(key, TimeSpan.FromMilliseconds(2500), mode));
                break;
            case Shape.SessionWait when connection is not null:
                await AdvisorySessionLock.LockAsync(connection, key, mode);
                break;
            case Shape.SessionWait:
                await AdvisorySessionLock.LockAsync(context!.Database, key, mode);
                break;
            case Shape.SessionTry when connection is not null:
                Assert.True(await AdvisorySessionLock.TryLockAsync(connection, key, mode));
                break;
            default:
                Assert.True(await AdvisorySessionLock.TryLockAsync(context!.Database, key, mode));
                break;
        }

        var seen = Assert.Single(recorder.Stopped);
        expected[Tags.Mode] = mode == AdvisoryLockMode.Shared ? "shared" : "exclusive";
        expected[Tags.Scope] = session ? "session" : "transaction";
        expected[Tags.Try] = shape is not (Shape.XactWait or Shape.SessionWait);
        if (shape is Shape.XactZeroTimeoutTry)
        {
            expected[Tags.TimeoutMs] = 0L;
        }

        if (shape is Shape.XactTimedTry)
        {
            expected[Tags.TimeoutMs] = 2500L;
        }

        Assert.Equal(expected.OrderBy(pair => pair.Key), seen.AtStart.OrderBy(pair => pair.Key));
    }

    private async Task<ProbeContext> OpenContextAsync()
    {
        var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(postgres.ConnectionString).Options);
        await context.Database.OpenConnectionAsync();
        return context;
    }

    [Theory]
    [MemberData(nameof(KeyFormsAndModes))]
    public async Task A_session_lock_carries_the_same_key_and_mode_tags_with_the_session_scope(Transport transport, string form, AdvisoryLockMode mode)
    {
        var (key, expected) = KeyOfForm(form);
        await using var connection = await postgres.OpenAsync();
        await using var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(postgres.ConnectionString).Options);
        await context.Database.OpenConnectionAsync();
        using var recorder = new AcquisitionRecorder();

        if (transport == Transport.EntityFramework)
        {
            Assert.True(await AdvisorySessionLock.TryLockAsync(context.Database, key, mode));
        }
        else
        {
            Assert.True(await AdvisorySessionLock.TryLockAsync(connection, key, mode));
        }

        var seen = Assert.Single(recorder.Stopped);
        expected[Tags.Mode] = mode == AdvisoryLockMode.Shared ? "shared" : "exclusive";
        expected[Tags.Scope] = "session";
        expected[Tags.Try] = true;
        Assert.Equal(expected.OrderBy(pair => pair.Key), seen.AtStart.OrderBy(pair => pair.Key));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task Whether_the_call_is_a_try_and_its_timeout_are_tagged_at_the_start(Transport transport)
    {
        await using var session = await TxSession.BeginAsync(postgres, transport);
        using var recorder = new AcquisitionRecorder();

        await session.LockAsync(Keys.Fresh());
        await session.TryAsync(Keys.Fresh());
        await session.TryAsync(Keys.Fresh(), TimeSpan.FromMilliseconds(2500));
        await session.TryAsync(Keys.Fresh(), TimeSpan.Zero);

        var seen = recorder.Stopped;
        Assert.Equal(4, seen.Count);
        Assert.Equal(new object?[] { false, true, true, true }, seen.Select(s => s.AtStart[Tags.Try]).ToArray());
        Assert.Equal(new object?[] { null, null, 2500L, 0L }, seen.Select(s => s.AtStart.GetValueOrDefault(Tags.TimeoutMs)).ToArray());
    }

    [Theory]
    [MemberData(nameof(TransportsAndAcquisitions))]
    public async Task An_uncontended_acquisition_ends_acquired(Transport transport, Acquire acquire)
    {
        await using var session = await TxSession.BeginAsync(postgres, transport);
        using var recorder = new AcquisitionRecorder();

        await AcquireAsync(session, acquire, Keys.Fresh());

        var seen = Assert.Single(recorder.Stopped);
        Assert.Equal("acquired", seen.Outcome);
        Assert.Equal(ActivityStatusCode.Unset, seen.Status);
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_try_that_answers_false_ends_refused_whether_it_waited_or_not(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        using var recorder = new AcquisitionRecorder();

        Assert.False(await contender.TryAsync(key));
        Assert.False(await contender.TryAsync(key, TimeSpan.Zero));
        Assert.False(await contender.TryAsync(key, TimeSpan.FromMilliseconds(300)).Within());

        var seen = recorder.Stopped;
        Assert.Equal(3, seen.Count);
        Assert.All(seen, s => Assert.Equal("refused", s.Outcome));
        Assert.All(seen, s => Assert.Equal(ActivityStatusCode.Unset, s.Status));
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_wait_ended_by_a_timeout_ends_timed_out_with_an_error_status(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        await contender.ScalarAsync("SELECT set_config('lock_timeout', '300ms', true)");
        using var recorder = new AcquisitionRecorder();

        var thrown = await Assert.ThrowsAsync<PostgresException>(() => contender.LockAsync(key).Within());

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, thrown.SqlState);
        var seen = Assert.Single(recorder.Stopped);
        Assert.Equal("timed_out", seen.Outcome);
        Assert.Equal(ActivityStatusCode.Error, seen.Status);
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task A_wait_ended_by_the_client_command_timeout_ends_timed_out_with_an_error_status(Transport transport)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        using var recorder = new AcquisitionRecorder();

        await Waits.EndsByItsCommandTimeout(token => contender.LockAsync(key, commandTimeout: TimeSpan.FromSeconds(1), cancellationToken: token), TimeSpan.FromSeconds(15));

        var seen = Assert.Single(recorder.Stopped);
        Assert.Equal("timed_out", seen.Outcome);
        Assert.Equal(ActivityStatusCode.Error, seen.Status);
    }

    [Fact]
    public async Task A_session_wait_ended_by_a_timeout_ends_timed_out()
    {
        var key = Keys.Fresh();
        await using var holder = await postgres.OpenAsync();
        await using var contender = await postgres.OpenAsync();
        Assert.True(await AdvisorySessionLock.TryLockAsync(holder, key));
        await using (var set = new NpgsqlCommand("SET statement_timeout = '300ms'", contender))
        {
            await set.ExecuteNonQueryAsync();
        }

        using var recorder = new AcquisitionRecorder();

        await Assert.ThrowsAsync<PostgresException>(() => AdvisorySessionLock.LockAsync(contender, key).Within());

        var seen = Assert.Single(recorder.Stopped);
        Assert.Equal("timed_out", seen.Outcome);
        Assert.Equal("session", seen.AtStart[Tags.Scope]);
        Assert.Equal(ActivityStatusCode.Error, seen.Status);
    }

    [Theory]
    [InlineData(Transport.EntityFramework, Acquire.Wait)]
    [InlineData(Transport.Npgsql, Acquire.Wait)]
    [InlineData(Transport.EntityFramework, Acquire.TimedTry)]
    [InlineData(Transport.Npgsql, Acquire.TimedTry)]
    public async Task A_cancelled_acquisition_ends_cancelled_without_an_error_status(Transport transport, Acquire acquire)
    {
        var key = Keys.Fresh();
        await using var holder = await TxSession.BeginAsync(postgres, transport);
        await using var contender = await TxSession.BeginAsync(postgres, transport);
        await holder.LockAsync(key);
        using var cancel = new CancellationTokenSource();
        using var recorder = new AcquisitionRecorder();

        var waiting = AcquireAsync(contender, acquire, key, cancel.Token, TimeSpan.FromSeconds(30));
        await postgres.WaitUntilBlockedAsync(contender.Connection);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Within());
        var seen = Assert.Single(recorder.Stopped);
        Assert.Equal("cancelled", seen.Outcome);
        Assert.Equal(ActivityStatusCode.Unset, seen.Status);
    }

    [Theory]
    [MemberData(nameof(TransportsAndAcquisitions))]
    public async Task A_server_error_ends_error_with_an_error_status(Transport transport, Acquire acquire)
    {
        await using var session = await TxSession.BeginAsync(postgres, transport);
        await Assert.ThrowsAsync<PostgresException>(() => session.ScalarAsync("SELECT 1 / 0"));
        using var recorder = new AcquisitionRecorder();

        await Assert.ThrowsAsync<PostgresException>(() => AcquireAsync(session, acquire, Keys.Fresh()));

        var seen = Assert.Single(recorder.Stopped);
        Assert.Equal("error", seen.Outcome);
        Assert.Equal(ActivityStatusCode.Error, seen.Status);
    }

    [Fact]
    public async Task A_session_lock_on_a_lost_connection_ends_error()
    {
        await using var victim = await postgres.OpenAsync();
        await postgres.ScalarAsync<bool>($"SELECT pg_terminate_backend({victim.ProcessID})");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await postgres.ScalarAsync<long>($"SELECT count(*) FROM pg_stat_activity WHERE pid = {victim.ProcessID}") > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        using var recorder = new AcquisitionRecorder();

        await Assert.ThrowsAnyAsync<Exception>(() => AdvisorySessionLock.TryLockAsync(victim, Keys.Fresh()));

        var seen = Assert.Single(recorder.Stopped);
        Assert.Equal("error", seen.Outcome);
        Assert.Equal(ActivityStatusCode.Error, seen.Status);
    }

    [Theory]
    [InlineData(Acquire.Wait)]
    [InlineData(Acquire.Try)]
    public async Task A_session_lock_emits_one_activity_with_the_session_scope_on_each_connection_type(Acquire acquire)
    {
        await using var connection = await postgres.OpenAsync();
        await using var context = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseNpgsql(postgres.ConnectionString).Options);
        await context.Database.OpenConnectionAsync();
        using var recorder = new AcquisitionRecorder();

        if (acquire == Acquire.Wait)
        {
            await AdvisorySessionLock.LockAsync(connection, Keys.Fresh());
            await AdvisorySessionLock.LockAsync(context.Database, Keys.Fresh());
        }
        else
        {
            Assert.True(await AdvisorySessionLock.TryLockAsync(connection, Keys.Fresh()));
            Assert.True(await AdvisorySessionLock.TryLockAsync(context.Database, Keys.Fresh()));
        }

        var seen = recorder.Stopped;
        Assert.Equal(2, seen.Count);
        Assert.All(seen, s => Assert.Equal("session", s.AtStart[Tags.Scope]));
        Assert.All(seen, s => Assert.Equal(acquire == Acquire.Try, s.AtStart[Tags.Try]));
        Assert.All(seen, s => Assert.Equal("acquired", s.Outcome));
    }

    [Fact]
    public async Task Unlocking_and_asking_whether_a_lock_is_held_emit_no_activity()
    {
        var key = Keys.Fresh();
        await using var connection = await postgres.OpenAsync();
        Assert.True(await AdvisorySessionLock.TryLockAsync(connection, key));
        using var recorder = new AcquisitionRecorder();

        Assert.True(await AdvisorySessionLock.IsHeldAsync(connection, key));
        Assert.True(await AdvisorySessionLock.UnlockAsync(connection, key));

        Assert.Empty(recorder.Stopped);
    }

    [Theory]
    [MemberData(nameof(Transports.All), MemberType = typeof(Transports))]
    public async Task One_timed_try_is_one_activity_however_many_statements_it_runs(Transport transport)
    {
        await using var session = await TxSession.BeginAsync(postgres, transport);
        using var recorder = new AcquisitionRecorder();

        Assert.True(await session.TryAsync(Keys.Fresh(), TimeSpan.FromSeconds(2)));

        Assert.Single(recorder.Stopped);
    }

    private static Task<bool> AcquireAsync(
        TxSession session, Acquire acquire, AdvisoryLockKey key, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        switch (acquire)
        {
            case Acquire.Wait:
                return WaitAsync(session, key, cancellationToken);
            case Acquire.Try:
                return session.TryAsync(key, cancellationToken: cancellationToken);
            default:
                return session.TryAsync(key, timeout ?? TimeSpan.FromMilliseconds(300), cancellationToken: cancellationToken);
        }
    }

    private static async Task<bool> WaitAsync(TxSession session, AdvisoryLockKey key, CancellationToken cancellationToken)
    {
        await session.LockAsync(key, cancellationToken: cancellationToken);
        return true;
    }

    private static (AdvisoryLockKey Key, Dictionary<string, object?> Tags) KeyOfForm(string form)
    {
        var id = Random.Shared.NextInt64(1L << 40);
        var text = Guid.NewGuid().ToString("N");
        var first = Random.Shared.Next();
        var second = Random.Shared.Next();
        return form switch
        {
            "raw" => (AdvisoryLockKey.FromRaw(id), new Dictionary<string, object?> { [Tags.KeyForm] = "raw", [Tags.KeyId] = id }),
            "namespaced_id" => (AdvisoryLockKey.Of(Keys.Primary, id), new Dictionary<string, object?>
            {
                [Tags.KeyForm] = "namespaced_id", [Tags.KeyNamespace] = "it.primary", [Tags.KeyNamespaceId] = 63001, [Tags.KeyId] = id,
            }),
            "hashed_text" => (AdvisoryLockKey.FromHashedText(text), new Dictionary<string, object?> { [Tags.KeyForm] = "hashed_text", [Tags.KeyText] = text }),
            "namespaced_text" => (AdvisoryLockKey.Of(Keys.Primary, text), new Dictionary<string, object?>
            {
                [Tags.KeyForm] = "namespaced_text", [Tags.KeyNamespace] = "it.primary", [Tags.KeyNamespaceId] = 63001, [Tags.KeyText] = text,
            }),
            "int_pair" => (AdvisoryLockKey.FromIntPair(first, second), new Dictionary<string, object?> { [Tags.KeyForm] = "int_pair", [Tags.KeyFirst] = first, [Tags.KeySecond] = second }),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null),
        };
    }
}
