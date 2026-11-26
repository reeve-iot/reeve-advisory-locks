namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class HandWrittenXactLockTests(PostgresFixture postgres)
{
    public static TheoryData<string, Transport> Cases => HandWrittenForms.XactCases();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_hand_written_exclusive_lock_refuses_the_package_until_it_ends(string form, Transport transport)
    {
        var resolved = await HandWrittenForms.ResolveAsync(form, postgres);
        await using var handWritten = await TxSession.BeginAsync(postgres, Transport.Npgsql);
        await using var package = await TxSession.BeginAsync(postgres, transport);
        await handWritten.ScalarAsync($"SELECT pg_advisory_xact_lock({resolved.RawArguments})");

        Assert.False(await package.TryAsync(resolved.Key));
        Assert.False(await package.TryAsync(resolved.Key, AdvisoryLockMode.Shared));

        await handWritten.CommitAsync();
        Assert.True(await package.TryAsync(resolved.Key));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_package_lock_refuses_a_hand_written_exclusive_and_shared_try(string form, Transport transport)
    {
        var resolved = await HandWrittenForms.ResolveAsync(form, postgres);
        await using var package = await TxSession.BeginAsync(postgres, transport);
        await using var handWritten = await TxSession.BeginAsync(postgres, Transport.Npgsql);
        await package.LockAsync(resolved.Key);

        Assert.Equal(false, await handWritten.ScalarAsync($"SELECT pg_try_advisory_xact_lock({resolved.RawArguments})"));
        Assert.Equal(false, await handWritten.ScalarAsync($"SELECT pg_try_advisory_xact_lock_shared({resolved.RawArguments})"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_hand_written_shared_lock_admits_a_shared_package_lock_and_refuses_an_exclusive_one(string form, Transport transport)
    {
        var resolved = await HandWrittenForms.ResolveAsync(form, postgres);
        await using var handWritten = await TxSession.BeginAsync(postgres, Transport.Npgsql);
        await using var package = await TxSession.BeginAsync(postgres, transport);
        await handWritten.ScalarAsync($"SELECT pg_advisory_xact_lock_shared({resolved.RawArguments})");

        Assert.True(await package.TryAsync(resolved.Key, AdvisoryLockMode.Shared));
        Assert.False(await package.TryAsync(resolved.Key));
    }
}
