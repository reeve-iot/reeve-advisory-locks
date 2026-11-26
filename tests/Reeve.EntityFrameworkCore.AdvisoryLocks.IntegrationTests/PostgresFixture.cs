using Npgsql;
using Testcontainers.PostgreSql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false }.ConnectionString;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<NpgsqlConnection> OpenAsync(int? commandTimeout = null)
    {
        var connectionString = commandTimeout is { } seconds
            ? new NpgsqlConnectionStringBuilder(ConnectionString) { CommandTimeout = seconds }.ConnectionString
            : ConnectionString;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task WaitUntilNoAdvisoryLocksAsync(int pid)
    {
        await using var observer = await OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = @pid", observer);
            command.Parameters.AddWithValue("pid", pid);
            if ((long)(await command.ExecuteScalarAsync())! == 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The session never released its advisory locks.");
    }

    public async Task WaitUntilBlockedAsync(NpgsqlConnection waiting)
    {
        await using var observer = await OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid))", observer);
            command.Parameters.AddWithValue("pid", waiting.ProcessID);
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The session never blocked on a lock.");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
