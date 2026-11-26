# Reeve.EntityFrameworkCore.AdvisoryLocks

Typed PostgreSQL advisory locks for Entity Framework Core and Npgsql.

- Transaction-scoped locks on the current transaction of an EF context (`DatabaseFacade`) or of an
  `NpgsqlTransaction`: blocking, `try`, and `try` with a timeout, exclusive or shared. The transaction ends the
  lock; there is nothing to release.
- Session locks (`AdvisorySessionLock`) for leader election and start-up steps, where a lock has to outlive a
  transaction.
- A typed key (`AdvisoryLockKey`) and a namespace registry (`AdvisoryLockNamespace`), so an application and
  the libraries it uses, sharing one database, cannot take each other's locks by accident.
- Tracing through a standard `ActivitySource`.

## Installation

```bash
dotnet add package Reeve.EntityFrameworkCore.AdvisoryLocks
```

Requirements: .NET 11 (`net11.0`), `Npgsql` 11, `Microsoft.EntityFrameworkCore.Relational` 11 and PostgreSQL 11
or newer (`hashtextextended`). The tests run on PostgreSQL 18.

## Usage

```csharp
public sealed class OrderWorker(DbContext db)
{
    private static readonly AdvisoryLockNamespace Orders = AdvisoryLockNamespace.Register("acme.orders", 2001);

    public async Task ProcessAsync(long orderId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var key = AdvisoryLockKey.Of(Orders, orderId);
        if (!await db.Database.TryAdvisoryXactLockAsync(key, TimeSpan.FromSeconds(2), cancellationToken: ct))
        {
            return; // another transaction is working on this order
        }

        // ... the order is ours until the transaction ends ...
        await transaction.CommitAsync(ct);
    }
}
```

## Keys

PostgreSQL has one 64-bit advisory-lock space per database, shared by everything that uses the database. A
namespace gives each owner of locks its own part of it:

```csharp
// Registered once per process. The id is part of the lock identity: never change it.
private static readonly AdvisoryLockNamespace Orders = AdvisoryLockNamespace.Register("acme.orders", 2001);

AdvisoryLockKey.Of(Orders, 42L);       // (2001 << 48) | 42; ids are 0 .. 2^48-1
AdvisoryLockKey.Of(Orders, "A-1001");  // hashtextextended('A-1001', 2001), computed by the server
```

The application chooses the namespace ids, and id 0 is reserved. Registering the same name and id again returns
the existing namespace; a name or an id that belongs to another registration throws.

A key that has to match one fixed elsewhere (a literal, a hash the caller computes, a stored function, another
language) comes from `AdvisoryLockKey.FromRaw(long)`, `FromHashedText(string)` or `FromIntPair(int, int)`, which
render exactly as given. See the [application guide](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/docs/operations/README.md#keys)
for the exact SQL of each factory and how keys can collide.

## Transaction-scoped locks

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);

await db.Database.AdvisoryXactLockAsync(key, cancellationToken: ct);                 // waits for a conflicting holder
if (!await db.Database.TryAdvisoryXactLockAsync(key, cancellationToken: ct)) { ... } // never waits
if (!await db.Database.TryAdvisoryXactLockAsync(key, TimeSpan.FromSeconds(2), cancellationToken: ct)) { ... }

await db.Database.AdvisoryXactLockAsync(key, AdvisoryLockMode.Shared, cancellationToken: ct); // shared locks admit each other

await transaction.CommitAsync(ct);                                                    // the locks end here
```

The same three methods extend `NpgsqlTransaction`. Without an active transaction they throw
`InvalidOperationException`, because a transaction lock outside a transaction would last for one statement.

The timed try (`TryAdvisoryXactLockAsync(key, timeout)`) waits inside a savepoint with its own `lock_timeout`.
When the time runs out it answers `false` and the transaction stays usable. A plain wait has no timeout of its own:
it ends by the command timeout, the caller's `lock_timeout` or `statement_timeout`, a cancellation or a deadlock, and the
transaction is then aborted.

Every lock method except the non-waiting transaction try and `IsHeldAsync` takes an optional `commandTimeout`. The
[application guide](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/docs/operations/README.md#timeouts) lists every
way a timed try can end and which command timeout each call uses.

## Session locks

```csharp
await using var connection = new NpgsqlConnection(unpooledConnectionString);
await connection.OpenAsync(ct);

if (await AdvisorySessionLock.TryLockAsync(connection, key, cancellationToken: ct))
{
    // ... start-up step ...
    await AdvisorySessionLock.UnlockAsync(connection, key, cancellationToken: ct);
}
```

A session lock belongs to a connection, not to a transaction, and survives a rollback. It ends at `UnlockAsync`
or when the server ends the session. Taken twice, it needs two unlocks. `UnlockAsync` and `IsHeldAsync` take
the same `mode` as the lock (the default is `Exclusive`): `UnlockAsync` answers `false` when the session did not
hold the lock in that mode, and `IsHeldAsync` answers whether the session holds it in that mode, a transaction
lock on the same key included.

Use session locks only where a lock has to outlive a transaction, and take them on an unpooled connection
(`Pooling=false`) that goes straight to PostgreSQL. Closing a pooled connection does not end its session, so the
lock stays held, and a multiplexer or a transaction-pooling proxy can send the next statement to another
session. After a lock call that was cancelled or failed, close the connection. The `DatabaseFacade` overloads
need the context's connection to be open already (`Database.OpenConnectionAsync`).

## Tracing

The `ActivitySource` is named `Reeve.EntityFrameworkCore.AdvisoryLocks` (`AdvisoryLockTelemetry.ActivitySourceName`)
and its version is the package version. Every acquisition attempt makes one `advisory_lock.acquire` activity,
which starts before the lock statement is sent and records the key, mode, scope and outcome as tags. Unlock and
`IsHeldAsync` make none. Without a listener no activity is created.

```csharp
tracing.AddSource(AdvisoryLockTelemetry.ActivitySourceName); // OpenTelemetry
```

The tags carry the key's text and ids as given, so leave the source unsubscribed where a lock name is
confidential. The [application guide](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/docs/operations/README.md#tracing)
lists every tag and outcome.

## Documentation

- [Using it in an application](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/docs/operations/README.md): namespaces, keys, timeouts, connections and tracing tags in detail.
- [FAQ](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/docs/faq.md): aborted transactions, lost connections, timeouts.
- [Architecture](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/docs/architecture/README.md): how a call runs, for contributors.
- [Contributing](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/CONTRIBUTING.md) and the [changelog](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/CHANGELOG.md).

## Licence

MIT, see [LICENSE](https://github.com/reeve-iot/reeve-advisory-locks/blob/main/LICENSE).
