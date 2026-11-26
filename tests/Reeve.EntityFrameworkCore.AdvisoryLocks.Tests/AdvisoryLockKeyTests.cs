using Reeve.EntityFrameworkCore.AdvisoryLocks;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.Tests;

public sealed class AdvisoryLockKeyTests
{
    private static readonly AdvisoryLockNamespace First = AdvisoryLockNamespace.Register("unit.key-first", 62001);
    private static readonly AdvisoryLockNamespace Second = AdvisoryLockNamespace.Register("unit.key-second", 62002);

    [Fact]
    public void The_same_id_in_two_namespaces_is_two_keys()
    {
        Assert.NotEqual(AdvisoryLockKey.Of(First, 7), AdvisoryLockKey.Of(Second, 7));
    }

    [Fact]
    public void The_same_name_in_two_namespaces_is_two_keys()
    {
        Assert.NotEqual(AdvisoryLockKey.Of(First, "queue"), AdvisoryLockKey.Of(Second, "queue"));
    }

    [Fact]
    public void Two_ids_in_one_namespace_are_two_keys()
    {
        Assert.NotEqual(AdvisoryLockKey.Of(First, 1), AdvisoryLockKey.Of(First, 2));
    }

    [Fact]
    public void Equal_inputs_make_equal_keys()
    {
        Assert.Equal(AdvisoryLockKey.Of(First, "queue"), AdvisoryLockKey.Of(First, "queue"));
        Assert.Equal(AdvisoryLockKey.Of(First, 9).GetHashCode(), AdvisoryLockKey.Of(First, 9).GetHashCode());
    }

    [Fact]
    public void A_hashed_text_key_is_not_equal_to_a_namespaced_key_of_that_text()
    {
        Assert.NotEqual(AdvisoryLockKey.FromHashedText("queue"), AdvisoryLockKey.Of(First, "queue"));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1L << 48)]
    [InlineData(long.MaxValue)]
    public void A_numeric_id_outside_48_bits_is_refused(long id)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvisoryLockKey.Of(First, id));
    }

    [Fact]
    public void The_largest_numeric_id_is_accepted()
    {
        var largest = AdvisoryLockKey.Of(First, (1L << 48) - 1);

        Assert.NotEqual(largest, AdvisoryLockKey.Of(Second, (1L << 48) - 1));
    }

    [Fact]
    public void A_null_text_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => AdvisoryLockKey.FromHashedText(null!));
        Assert.Throws<ArgumentNullException>(() => AdvisoryLockKey.Of(First, (string)null!));
    }

    [Fact]
    public void Describing_a_key_names_the_namespace_for_the_log()
    {
        Assert.Contains("unit.key-first", AdvisoryLockKey.Of(First, "queue").ToString());
    }
}
