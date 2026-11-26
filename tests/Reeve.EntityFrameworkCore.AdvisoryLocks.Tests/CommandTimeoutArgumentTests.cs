using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.Tests;

public sealed class CommandTimeoutArgumentTests
{
    private static readonly AdvisoryLockKey Key = AdvisoryLockKey.FromRaw(1);

    [Theory]
    [InlineData(0)]
    [InlineData(-5000)]
    public async Task A_command_timeout_that_is_neither_positive_nor_infinite_is_refused(long milliseconds)
    {
        using var connection = new NpgsqlConnection();
        var commandTimeout = TimeSpan.FromMilliseconds(milliseconds);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AdvisorySessionLock.LockAsync(connection, Key, commandTimeout: commandTimeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AdvisorySessionLock.TryLockAsync(connection, Key, commandTimeout: commandTimeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AdvisorySessionLock.UnlockAsync(connection, Key, commandTimeout: commandTimeout));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1500)]
    public async Task An_infinite_or_positive_command_timeout_passes_validation_and_reaches_the_connection_check(long milliseconds)
    {
        using var connection = new NpgsqlConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AdvisorySessionLock.TryLockAsync(connection, Key, commandTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }
}
