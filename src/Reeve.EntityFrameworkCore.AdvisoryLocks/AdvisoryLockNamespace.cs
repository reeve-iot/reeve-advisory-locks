using System.Collections.Concurrent;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

/// <summary>A claim on part of the database's advisory-lock space, registered per process; a clashing claim throws instead of sharing locks.</summary>
public sealed class AdvisoryLockNamespace
{
    private static readonly ConcurrentDictionary<ushort, AdvisoryLockNamespace> ById = new();
    private static readonly object RegistrationGate = new();

    private AdvisoryLockNamespace(string name, ushort id)
    {
        Name = name;
        Id = id;
    }

    /// <summary>The name the owner registered; it appears in <see cref="AdvisoryLockKey.ToString"/>.</summary>
    public string Name { get; }

    /// <summary>The 16-bit id that keys of this namespace carry. 0 is reserved: it is the seed of <see cref="AdvisoryLockKey.FromHashedText"/>.</summary>
    public ushort Id { get; }

    /// <summary>Every namespace registered in this process.</summary>
    public static IReadOnlyCollection<AdvisoryLockNamespace> Registered => ById.Values.ToArray();

    /// <summary>Claims <paramref name="id"/> under <paramref name="name"/>; the same pair again returns the first registration, a clash throws.</summary>
    /// <exception cref="InvalidOperationException">The id or the name belongs to another registration.</exception>
    public static AdvisoryLockNamespace Register(string name, ushort id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (id == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Namespace id 0 is reserved.");
        }

        lock (RegistrationGate)
        {
            if (ById.TryGetValue(id, out var existing))
            {
                return string.Equals(existing.Name, name, StringComparison.Ordinal)
                    ? existing
                    : throw new InvalidOperationException(
                        $"Advisory-lock namespace id {id} is registered as '{existing.Name}', so '{name}' cannot claim it.");
            }

            var sameName = ById.Values.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));
            if (sameName is not null)
            {
                throw new InvalidOperationException(
                    $"Advisory-lock namespace '{name}' is registered with id {sameName.Id}, so id {id} cannot claim it.");
            }

            var created = new AdvisoryLockNamespace(name, id);
            ById[id] = created;
            return created;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}({Id})";
}
