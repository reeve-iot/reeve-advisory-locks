using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

public sealed record HandWrittenForm(AdvisoryLockKey Key, string RawArguments);

public static class HandWrittenForms
{
    public static readonly string[] Names =
    [
        "bigint literal",
        "negative bigint literal",
        "bigint from a client-side SHA-256 of a name",
        "hashtextextended with seed 0",
        "hashtextextended with seed 0 of a name with quotes",
        "two int4 with a server-side hashtext",
        "namespaced name",
        "namespaced number",
    ];

    public static TheoryData<string, Transport> XactCases()
    {
        var data = new TheoryData<string, Transport>();
        foreach (var name in Names)
        {
            data.Add(name, Transport.EntityFramework);
            data.Add(name, Transport.Npgsql);
        }

        return data;
    }

    public static TheoryData<string> SessionCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Names)
        {
            data.Add(name);
        }

        return data;
    }

    public static async Task<HandWrittenForm> ResolveAsync(string name, PostgresFixture postgres)
    {
        var unique = Guid.NewGuid().ToString("N");
        switch (name)
        {
            case "bigint literal":
                var literal = 1_000_000_000_000L + Random.Shared.NextInt64(1L << 40);
                return new HandWrittenForm(AdvisoryLockKey.FromRaw(literal), literal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case "negative bigint literal":
                var negative = -1_000_000_000_000L - Random.Shared.NextInt64(1L << 40);
                return new HandWrittenForm(AdvisoryLockKey.FromRaw(negative), negative.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case "bigint from a client-side SHA-256 of a name":
                var hashed = BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes("orders-bootstrap_" + unique)));
                return new HandWrittenForm(AdvisoryLockKey.FromRaw(hashed), hashed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case "hashtextextended with seed 0":
                return new HandWrittenForm(AdvisoryLockKey.FromHashedText("gate:" + unique), $"hashtextextended('gate:{unique}', 0)");
            case "hashtextextended with seed 0 of a name with quotes":
                return new HandWrittenForm(AdvisoryLockKey.FromHashedText("it's-" + unique), $"hashtextextended('it''s-{unique}', 0)");
            case "two int4 with a server-side hashtext":
                var hash = await postgres.ScalarAsync<int>($"SELECT hashtext('slot-{unique}')");
                return new HandWrittenForm(AdvisoryLockKey.FromIntPair(1397509972, hash), $"1397509972, hashtext('slot-{unique}')");
            case "namespaced name":
                return new HandWrittenForm(AdvisoryLockKey.Of(Keys.Primary, unique), $"hashtextextended('{unique}', {Keys.Primary.Id})");
            case "namespaced number":
                var id = Random.Shared.NextInt64(1L << 40);
                return new HandWrittenForm(AdvisoryLockKey.Of(Keys.Primary, id), $"({Keys.Primary.Id}::bigint << 48) | {id}");
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }
}
