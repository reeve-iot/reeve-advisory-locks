using System.Diagnostics;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.Tests;

public sealed class AdvisoryLockActivityTests
{
    private static readonly AdvisoryLockKey Key = AdvisoryLockKey.FromHashedText("some key");

    [Fact]
    public void Without_a_listener_no_activity_is_made_and_nothing_is_allocated()
    {
        AdvisoryLockActivity.Start(Key, AdvisoryLockMode.Shared, session: false, isTry: true, TimeSpan.FromSeconds(1));
        var before = GC.GetAllocatedBytesForCurrentThread();

        Activity? seen = null;
        for (var i = 0; i < 1000; i++)
        {
            seen = AdvisoryLockActivity.Start(Key, AdvisoryLockMode.Shared, session: false, isTry: true, TimeSpan.FromSeconds(1));
            AdvisoryLockActivity.Stop(seen, AdvisoryLockActivity.Outcome.Acquired);
        }

        Assert.Null(seen);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
