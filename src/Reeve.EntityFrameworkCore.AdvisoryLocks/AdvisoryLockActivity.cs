using System.Diagnostics;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

internal static class AdvisoryLockActivity
{
    internal const string OperationName = "advisory_lock.acquire";

    internal static class Outcome
    {
        internal const string Acquired = "acquired";
        internal const string Refused = "refused";
        internal const string TimedOut = "timed_out";
        internal const string Cancelled = "cancelled";
        internal const string Error = "error";
    }

    internal static Activity? Start(AdvisoryLockKey key, AdvisoryLockMode mode, bool session, bool isTry, TimeSpan? timeout)
    {
        if (!AdvisoryLockTelemetry.Source.HasListeners())
        {
            return null;
        }

        var tags = new List<KeyValuePair<string, object?>>(9)
        {
            new("advisory_lock.key.form", FormName(key.Form)),
        };
        switch (key.Form)
        {
            case AdvisoryLockKey.KeyForm.Raw:
                tags.Add(new("advisory_lock.key.id", key.Id));
                break;
            case AdvisoryLockKey.KeyForm.NamespacedId:
                AddNamespace(tags, key);
                tags.Add(new("advisory_lock.key.id", key.Id));
                break;
            case AdvisoryLockKey.KeyForm.HashedText:
                tags.Add(new("advisory_lock.key.text", key.Text));
                break;
            case AdvisoryLockKey.KeyForm.NamespacedText:
                AddNamespace(tags, key);
                tags.Add(new("advisory_lock.key.text", key.Text));
                break;
            default:
                tags.Add(new("advisory_lock.key.first", key.First));
                tags.Add(new("advisory_lock.key.second", key.Second));
                break;
        }

        tags.Add(new("advisory_lock.mode", mode == AdvisoryLockMode.Shared ? "shared" : "exclusive"));
        tags.Add(new("advisory_lock.scope", session ? "session" : "transaction"));
        tags.Add(new("advisory_lock.try", isTry));
        if (timeout is { } wait)
        {
            tags.Add(new("advisory_lock.timeout_ms", (long)Math.Ceiling(wait.TotalMilliseconds)));
        }

        return AdvisoryLockTelemetry.Source.StartActivity(OperationName, ActivityKind.Internal, default(ActivityContext), tags);
    }

    internal static void Stop(Activity? activity, string outcome)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("advisory_lock.outcome", outcome);
        activity.Dispose();
    }

    internal static void Stop(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        var outcome = Classify(exception);
        activity.SetTag("advisory_lock.outcome", outcome);
        if (outcome != Outcome.Cancelled)
        {
            activity.SetTag("error.type", exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        }

        activity.Dispose();
    }

    internal static async Task LockAsync(NpgsqlCommand command, AdvisoryLockKey key, AdvisoryLockMode mode, bool session, CancellationToken cancellationToken)
    {
        var activity = Start(key, mode, session, isTry: false, timeout: null);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Stop(activity, ex);
            throw;
        }

        Stop(activity, Outcome.Acquired);
    }

    internal static async Task<bool> TryAsync(
        NpgsqlCommand command, AdvisoryLockKey key, AdvisoryLockMode mode, bool session, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var activity = Start(key, mode, session, isTry: true, timeout);
        bool acquired;
        try
        {
            acquired = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch (Exception ex)
        {
            Stop(activity, ex);
            throw;
        }

        Stop(activity, acquired ? Outcome.Acquired : Outcome.Refused);
        return acquired;
    }

    private static string Classify(Exception exception) => exception switch
    {
        OperationCanceledException => Outcome.Cancelled,
        PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled } => Outcome.TimedOut,
        NpgsqlException { InnerException: TimeoutException } => Outcome.TimedOut,
        _ => Outcome.Error,
    };

    private static void AddNamespace(List<KeyValuePair<string, object?>> tags, AdvisoryLockKey key)
    {
        tags.Add(new("advisory_lock.key.namespace", key.Namespace!.Name));
        tags.Add(new("advisory_lock.key.namespace_id", (int)key.Namespace.Id));
    }

    private static string FormName(AdvisoryLockKey.KeyForm form) => form switch
    {
        AdvisoryLockKey.KeyForm.Raw => "raw",
        AdvisoryLockKey.KeyForm.NamespacedId => "namespaced_id",
        AdvisoryLockKey.KeyForm.HashedText => "hashed_text",
        AdvisoryLockKey.KeyForm.NamespacedText => "namespaced_text",
        _ => "int_pair",
    };
}
