using System.Diagnostics;
using System.Reflection;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

/// <summary>The names the package traces under; the tags of an acquisition activity are listed in docs/operations/README.md.</summary>
public static class AdvisoryLockTelemetry
{
    /// <summary>The name of the package's <see cref="ActivitySource"/>, which is the name of its assembly: pass it to <c>AddSource</c> or compare it in an <see cref="ActivityListener"/>.</summary>
    public const string ActivitySourceName = "Reeve.EntityFrameworkCore.AdvisoryLocks";

    internal static readonly ActivitySource Source = new(ActivitySourceName, PackageVersion());

    private static string PackageVersion()
    {
        var version = typeof(AdvisoryLockTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }
}
