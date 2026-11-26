namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

/// <summary>Which PostgreSQL lock mode a call takes: shared locks coexist, an exclusive lock conflicts with every other.</summary>
public enum AdvisoryLockMode
{
    /// <summary>The <c>pg_advisory_*</c> functions without a suffix.</summary>
    Exclusive = 0,

    /// <summary>The <c>pg_advisory_*_shared</c> functions.</summary>
    Shared = 1,
}
