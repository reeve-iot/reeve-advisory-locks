using System.Collections.Concurrent;
using System.Diagnostics;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

public sealed record Acquisition(
    IReadOnlyDictionary<string, object?> AtStart,
    IReadOnlyDictionary<string, object?> AtStop,
    ActivityStatusCode Status,
    string? SourceVersion)
{
    public string? Outcome => AtStop.TryGetValue(Tags.Outcome, out var value) ? (string?)value : null;
}

public static class Tags
{
    public const string KeyForm = "advisory_lock.key.form";
    public const string KeyNamespace = "advisory_lock.key.namespace";
    public const string KeyNamespaceId = "advisory_lock.key.namespace_id";
    public const string KeyId = "advisory_lock.key.id";
    public const string KeyText = "advisory_lock.key.text";
    public const string KeyFirst = "advisory_lock.key.first";
    public const string KeySecond = "advisory_lock.key.second";
    public const string Mode = "advisory_lock.mode";
    public const string Scope = "advisory_lock.scope";
    public const string Try = "advisory_lock.try";
    public const string TimeoutMs = "advisory_lock.timeout_ms";
    public const string Outcome = "advisory_lock.outcome";
}

public sealed class AcquisitionRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentDictionary<Activity, Dictionary<string, object?>> _starts = new();
    private readonly ConcurrentQueue<Acquisition> _stopped = new();

    public AcquisitionRecorder(Action<IReadOnlyDictionary<string, object?>>? onStarted = null)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AdvisoryLockTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                var snapshot = Snapshot(activity);
                _starts[activity] = snapshot;
                onStarted?.Invoke(snapshot);
            },
            ActivityStopped = activity =>
            {
                _starts.TryRemove(activity, out var atStart);
                _stopped.Enqueue(new Acquisition(atStart ?? new Dictionary<string, object?>(), Snapshot(activity), activity.Status, activity.Source.Version));
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Acquisition> Stopped => _stopped.ToArray();

    public void Dispose() => _listener.Dispose();

    private static Dictionary<string, object?> Snapshot(Activity activity) =>
        activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);
}
