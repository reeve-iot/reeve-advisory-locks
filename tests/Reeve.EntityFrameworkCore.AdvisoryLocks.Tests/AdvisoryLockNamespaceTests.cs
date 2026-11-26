using Reeve.EntityFrameworkCore.AdvisoryLocks;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.Tests;

public sealed class AdvisoryLockNamespaceTests
{
    [Fact]
    public void Registering_the_same_pair_twice_returns_the_first_registration()
    {
        var first = AdvisoryLockNamespace.Register("unit.same-pair", 61001);
        var second = AdvisoryLockNamespace.Register("unit.same-pair", 61001);

        Assert.Same(first, second);
    }

    [Fact]
    public void An_id_claimed_under_another_name_is_refused()
    {
        AdvisoryLockNamespace.Register("unit.owner", 61002);

        Assert.Throws<InvalidOperationException>(() => AdvisoryLockNamespace.Register("unit.intruder", 61002));
    }

    [Fact]
    public void A_name_registered_with_another_id_is_refused()
    {
        AdvisoryLockNamespace.Register("unit.named", 61003);

        Assert.Throws<InvalidOperationException>(() => AdvisoryLockNamespace.Register("unit.named", 61004));
    }

    [Fact]
    public void A_refused_registration_leaves_the_id_free()
    {
        AdvisoryLockNamespace.Register("unit.held", 61005);
        Assert.Throws<InvalidOperationException>(() => AdvisoryLockNamespace.Register("unit.held", 61006));

        var claimed = AdvisoryLockNamespace.Register("unit.later", 61006);

        Assert.Equal<ushort>(61006, claimed.Id);
    }

    [Fact]
    public void Id_zero_is_refused_because_it_is_the_seed_of_hashed_text_keys()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvisoryLockNamespace.Register("unit.zero", 0));
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => AdvisoryLockNamespace.Register(" ", 61007));
    }

    [Fact]
    public void Concurrent_claims_of_one_id_admit_exactly_one_name()
    {
        var outcomes = Enumerable.Range(0, 16)
            .AsParallel()
            .Select(i =>
            {
                try
                {
                    AdvisoryLockNamespace.Register($"unit.race-{i}", 61008);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })
            .ToArray();

        Assert.Equal(1, outcomes.Count(won => won));
    }
}
