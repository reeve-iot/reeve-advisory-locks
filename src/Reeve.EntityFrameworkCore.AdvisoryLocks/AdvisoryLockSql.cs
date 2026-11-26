using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

internal static class AdvisoryLockSql
{
    internal enum Operation
    {
        XactLock,
        TryXactLock,
        SessionLock,
        TrySessionLock,
        SessionUnlock,
    }

    internal static NpgsqlCommand Command(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Operation operation, AdvisoryLockMode mode, AdvisoryLockKey key, int? commandTimeout)
    {
        var command = new NpgsqlCommand(null, connection, transaction);
        command.CommandText = $"SELECT {FunctionName(operation, mode)}({Arguments(key)})";
        AddKeyParameters(command, key);
        if (commandTimeout is { } seconds)
        {
            command.CommandTimeout = seconds;
        }

        return command;
    }

    internal static NpgsqlCommand HeldCommand(NpgsqlConnection connection, AdvisoryLockMode mode, AdvisoryLockKey key)
    {
        var (subId, classId, objectId) = key.Kind == AdvisoryLockKey.KeyKind.IntPair
            ? (2, "(@first::bigint & 4294967295)", "(@second::bigint & 4294967295)")
            : (1, "((k.v >> 32) & 4294967295)", "(k.v & 4294967295)");
        var source = key.Kind switch
        {
            AdvisoryLockKey.KeyKind.Bigint => "@key::bigint",
            AdvisoryLockKey.KeyKind.Text => "hashtextextended(@text, @seed)",
            _ => "0::bigint",
        };
        var command = new NpgsqlCommand(null, connection)
        {
            CommandText =
                $"WITH k AS (SELECT {source} AS v) SELECT EXISTS (SELECT 1 FROM pg_locks l, k WHERE l.locktype = 'advisory' AND l.granted " +
                $"AND l.pid = pg_backend_pid() AND l.mode = '{(mode == AdvisoryLockMode.Shared ? "ShareLock" : "ExclusiveLock")}' " +
                $"AND l.objsubid = {subId} AND l.classid::bigint = {classId} AND l.objid::bigint = {objectId})",
        };
        AddKeyParameters(command, key);
        return command;
    }

    internal static int? CommandTimeoutSeconds(TimeSpan? commandTimeout, string paramName = "commandTimeout")
    {
        if (commandTimeout is not { } value)
        {
            return null;
        }

        if (value == Timeout.InfiniteTimeSpan)
        {
            return 0;
        }

        if (value <= TimeSpan.Zero || value.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "A command timeout is positive, or Timeout.InfiniteTimeSpan for none.");
        }

        return (int)Math.Ceiling(value.TotalSeconds);
    }

    private static string FunctionName(Operation operation, AdvisoryLockMode mode)
    {
        var suffix = mode == AdvisoryLockMode.Shared ? "_shared" : string.Empty;
        return operation switch
        {
            Operation.XactLock => "pg_advisory_xact_lock" + suffix,
            Operation.TryXactLock => "pg_try_advisory_xact_lock" + suffix,
            Operation.SessionLock => "pg_advisory_lock" + suffix,
            Operation.TrySessionLock => "pg_try_advisory_lock" + suffix,
            Operation.SessionUnlock => "pg_advisory_unlock" + suffix,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }

    private static string Arguments(AdvisoryLockKey key) => key.Kind switch
    {
        AdvisoryLockKey.KeyKind.Bigint => "@key",
        AdvisoryLockKey.KeyKind.Text => "hashtextextended(@text, @seed)",
        AdvisoryLockKey.KeyKind.IntPair => "@first, @second",
        _ => throw new InvalidOperationException("The key was not made by one of its factories."),
    };

    private static void AddKeyParameters(NpgsqlCommand command, AdvisoryLockKey key)
    {
        switch (key.Kind)
        {
            case AdvisoryLockKey.KeyKind.Bigint:
                command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bigint) { Value = key.Number });
                break;
            case AdvisoryLockKey.KeyKind.Text:
                command.Parameters.Add(new NpgsqlParameter("text", NpgsqlDbType.Text) { Value = key.Text });
                command.Parameters.Add(new NpgsqlParameter("seed", NpgsqlDbType.Bigint) { Value = key.Number });
                break;
            case AdvisoryLockKey.KeyKind.IntPair:
                command.Parameters.Add(new NpgsqlParameter("first", NpgsqlDbType.Integer) { Value = key.First });
                command.Parameters.Add(new NpgsqlParameter("second", NpgsqlDbType.Integer) { Value = key.Second });
                break;
            default:
                throw new InvalidOperationException("The key was not made by one of its factories.");
        }
    }

    internal static void RequireOpen(NpgsqlConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "A session advisory lock lives on one open connection; open it and keep it open for as long as the lock is held.");
        }
    }
}
