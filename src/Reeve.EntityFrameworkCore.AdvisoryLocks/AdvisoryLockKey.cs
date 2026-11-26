namespace Reeve.EntityFrameworkCore.AdvisoryLocks;

/// <summary>The identity of an advisory lock: two keys that render the same SQL arguments take the same lock.</summary>
/// <remarks>Prefer <c>Of</c>; the <c>From*</c> factories take a key that is fixed elsewhere and renders exactly as given.</remarks>
public sealed class AdvisoryLockKey : IEquatable<AdvisoryLockKey>
{
    private const long NumericIdLimit = 1L << 48;

    private AdvisoryLockKey(
        KeyKind kind, KeyForm form, long number, string? text, int first, int second, string description, AdvisoryLockNamespace? @namespace = null)
    {
        Kind = kind;
        Form = form;
        Namespace = @namespace;
        Number = number;
        Text = text;
        First = first;
        Second = second;
        Description = description;
    }

    internal enum KeyKind
    {
        Bigint,

        Text,

        IntPair,
    }

    internal enum KeyForm
    {
        Raw,
        NamespacedId,
        HashedText,
        NamespacedText,
        IntPair,
    }

    internal KeyKind Kind { get; }

    internal KeyForm Form { get; }

    internal AdvisoryLockNamespace? Namespace { get; }

    internal long Id => Form == KeyForm.NamespacedId ? Number & (NumericIdLimit - 1) : Number;

    internal long Number { get; }

    internal string? Text { get; }

    internal int First { get; }

    internal int Second { get; }

    private string Description { get; }

    /// <summary>A key from a registered namespace and a numeric id: the namespace id is the top 16 bits and the id the low 48.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is negative or does not fit in 48 bits.</exception>
    public static AdvisoryLockKey Of(AdvisoryLockNamespace @namespace, long id)
    {
        ArgumentNullException.ThrowIfNull(@namespace);
        if (id < 0 || id >= NumericIdLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "A namespaced numeric id must fit in 48 bits and not be negative.");
        }

        var number = unchecked((long)(((ulong)@namespace.Id << 48) | (ulong)id));
        return new AdvisoryLockKey(KeyKind.Bigint, KeyForm.NamespacedId, number, null, 0, 0, $"{@namespace.Name}:{id}", @namespace);
    }

    /// <summary>A key from a registered namespace and a name, hashed by the server as <c>hashtextextended(name, namespace id)</c>.</summary>
    public static AdvisoryLockKey Of(AdvisoryLockNamespace @namespace, string name)
    {
        ArgumentNullException.ThrowIfNull(@namespace);
        ArgumentNullException.ThrowIfNull(name);
        return new AdvisoryLockKey(KeyKind.Text, KeyForm.NamespacedText, @namespace.Id, name, 0, 0, $"{@namespace.Name}:'{name}'", @namespace);
    }

    /// <summary>A key that is the bigint itself, for a value fixed elsewhere: a literal, a hash the caller computes, a stored function.</summary>
    public static AdvisoryLockKey FromRaw(long key) =>
        new(KeyKind.Bigint, KeyForm.Raw, key, null, 0, 0, $"raw:{key}");

    /// <summary>A key that is <c>hashtextextended(text, 0)</c>, for a key another statement or release derives that way.</summary>
    public static AdvisoryLockKey FromHashedText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new AdvisoryLockKey(KeyKind.Text, KeyForm.HashedText, 0, text, 0, 0, $"hashed:'{text}'");
    }

    /// <summary>A two-int4 key, which PostgreSQL keeps in a lock space of its own, apart from every bigint key.</summary>
    public static AdvisoryLockKey FromIntPair(int first, int second) =>
        new(KeyKind.IntPair, KeyForm.IntPair, 0, null, first, second, $"pair:{first},{second}");

    /// <summary>Whether two keys of the same kind take the same lock; a text key and a raw key with that hash are the same lock but not equal.</summary>
    public bool Equals(AdvisoryLockKey? other) =>
        other is not null && Kind == other.Kind && Number == other.Number && First == other.First
        && Second == other.Second && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AdvisoryLockKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Number, Text, First, Second);

    /// <summary>A short description for logs; it carries the namespace name, not its id.</summary>
    public override string ToString() => Description;
}
