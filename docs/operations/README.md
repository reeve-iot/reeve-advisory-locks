# Using Reeve.EntityFrameworkCore.AdvisoryLocks in an application

The package is a library, not a service. It has no settings file, environment variables, ports or background
work. The application configures everything in code.

## Requirements

- .NET 11 (`net11.0`).
- `Npgsql` and `Microsoft.EntityFrameworkCore.Relational` 11.0.0 or newer. A `DbContext` must use the Npgsql provider;
  any other provider gets an `InvalidOperationException`.
- PostgreSQL 11 or newer (`hashtextextended`). The tests run on PostgreSQL 18.

## Installation

```bash
dotnet add package Reeve.EntityFrameworkCore.AdvisoryLocks
```

## Keys

### Namespaces

Register one namespace per owner of locks (the application, and each library that takes locks) with a stable
id:

```csharp
private static readonly AdvisoryLockNamespace Orders = AdvisoryLockNamespace.Register("acme.orders", 2001);
```

- The application chooses the ids. A library that takes locks should take its namespace name and id as an
  option.
- Never change or reuse an id: it is part of the lock identity.
- Id 0 is reserved for `FromHashedText`, and `Register` refuses it with `ArgumentOutOfRangeException`.
- Registering the same name and id again returns the existing namespace. A name or an id that belongs to
  another registration throws `InvalidOperationException`.
- The registry is per process and per `AssemblyLoadContext`. `AdvisoryLockNamespace.Registered` lists what is
  registered.

A registration in a `static readonly` field runs when the type is first used, so a clash shows up then. Register
in an explicit start-up step if you want a clash to stop the start-up.

### Factories

| Factory | SQL arguments | For |
|---|---|---|
| `AdvisoryLockKey.Of(namespace, long id)` | `(namespace id << 48) \| id`, id in 0 .. 2^48-1 | numeric ids |
| `AdvisoryLockKey.Of(namespace, string name)` | `hashtextextended(name, namespace id)` | names |
| `AdvisoryLockKey.FromRaw(long)` | the bigint as given | a literal, a constant or a client-side hash |
| `AdvisoryLockKey.FromHashedText(string)` | `hashtextextended(text, 0)` | a hand-written `hashtextextended({0}, 0)` |
| `AdvisoryLockKey.FromIntPair(int, int)` | the two `int4` as given | the two-`int4` form |

`Of(namespace, id)` throws `ArgumentOutOfRangeException` for an id outside 0 .. 2^48-1. Two-`int4` keys are a
lock space of their own and never conflict with a bigint key.

Numeric keys of two namespaces are never equal. A namespaced key can equal a `FromRaw` key whose top 16 bits are
a registered namespace id, so register the prefix of any raw keys you use. Name keys are 64-bit hashes: two
different names collide with the probability of any 64-bit hash, and a collision makes two locks share one lock.
It never lets two holders in.

`AdvisoryLockKey.Equals` compares the SQL arguments a key renders, so a namespaced id key equals the `FromRaw`
key of the same bigint. A text key compares by its text and seed, so it does not equal the `FromRaw` key of its
hash, although both take the same lock. `ToString()` gives a short description for logs, which
names the namespace of a namespaced key.

## Timeouts

### Command timeouts

A lock statement waits inside one command, and the client's command timeout cuts that wait off.

| Call | Command timeout |
|---|---|
| timed try (`TryAdvisoryXactLockAsync(key, timeout)`) | `timeout` plus five seconds, or `commandTimeout` when that is longer |
| non-waiting transaction try (`TryAdvisoryXactLockAsync(key)`, or a timed try with a zero timeout) | the ambient value; a timed try with a zero timeout also takes `commandTimeout` |
| `AdvisorySessionLock.TryLockAsync`, `UnlockAsync` | `commandTimeout`, else the ambient value |
| waits (`AdvisoryXactLockAsync`, `AdvisorySessionLock.LockAsync`) | `commandTimeout`, else the ambient value |

`commandTimeout` is an optional `TimeSpan?`. `null` keeps the ambient value, which is the context's or the
connection's `Command Timeout`; for a timed try it keeps the package's own timeout plus five seconds.
`Timeout.InfiniteTimeSpan` removes the limit. Any other value must be positive. For a timed try it must also be
longer than `timeout`, otherwise the call throws `ArgumentOutOfRangeException` before anything runs.

The `timeout` of a timed try must be between zero and `int.MaxValue` milliseconds. A zero timeout runs the
non-waiting try.

### How a timed try ends

The timed try takes a savepoint and reads the caller's `lock_timeout` and `statement_timeout`. It sets
`lock_timeout` to the timeout and `statement_timeout` to 0 for the wait, and puts the caller's values back
afterwards, on a won wait and on a lost one.

| The lock statement ends by | Outcome |
|---|---|
| the lock is acquired | `true` |
| its own `lock_timeout` | `false`; rolled back to the savepoint, the transaction usable |
| the server's `statement_timeout` | cannot happen: the wait suspends `statement_timeout` |
| the client's command timeout | cannot happen first: it is at least the timeout plus five seconds, or there is none |
| the `CancellationToken` | `OperationCanceledException`; rolled back to the savepoint, the transaction usable |
| a deadlock or any other server error | the `PostgresException` rethrown; rolled back to the savepoint, the transaction usable, earlier locks of the transaction kept |
| a lost connection, or `transaction_timeout` (PostgreSQL 17 and newer, which ends the session) | the original exception rethrown; there is nothing to restore |

Once the lock is won, the cancellation token is no longer observed, so a late cancellation cannot abort a
transaction that holds the lock.

The plain waits (`AdvisoryXactLockAsync`, `AdvisorySessionLock.LockAsync`) have no timeout of their own and no
savepoint. They end by the command timeout, the caller's `lock_timeout` or `statement_timeout`, a cancellation
or a deadlock, and
the transaction they ran in is then aborted.

## Connections for session locks

Use a dedicated, unpooled connection (`Pooling=false` in the connection string), direct to PostgreSQL, not
through a multiplexer or a transaction-pooling proxy. Open the connection before calling the `DatabaseFacade`
overloads and keep it open while the lock is held: EF closes a connection it opened itself after each operation.
Every session-lock call on a closed connection throws `InvalidOperationException`.

Prefer transaction locks for anything that fits in a transaction.

## Tracing

The `ActivitySource` is named `Reeve.EntityFrameworkCore.AdvisoryLocks`
(`AdvisoryLockTelemetry.ActivitySourceName`) and its version is the package version. With OpenTelemetry:

```csharp
tracing.AddSource(AdvisoryLockTelemetry.ActivitySourceName);
```

Each acquisition attempt makes one activity, `advisory_lock.acquire`: transaction waits, tries and timed tries,
session waits and tries, in both modes, on EF and on Npgsql. A timed try is one activity for all its statements.
The activity starts before the lock statement is sent, with the key, mode, scope and timeout already set, so a
listener can see which lock is about to be requested. `UnlockAsync`, `IsHeldAsync` and calls refused before they
run (an argument out of range, no transaction, a closed connection) make no activity. Without a listener the
package creates no activity and no tags.

| Tag | Type | Set | Value |
|---|---|---|---|
| `advisory_lock.key.form` | string | at start | `raw`, `namespaced_id`, `hashed_text`, `namespaced_text` or `int_pair`: the factory that made the key |
| `advisory_lock.key.namespace` | string | at start, namespaced keys | the namespace name |
| `advisory_lock.key.namespace_id` | int | at start, namespaced keys | the namespace id |
| `advisory_lock.key.id` | long | at start, `raw` and `namespaced_id` | the bigint of a raw key, the id within the namespace of a namespaced one |
| `advisory_lock.key.text` | string | at start, text keys | the hashed text |
| `advisory_lock.key.first`, `advisory_lock.key.second` | int | at start, `int_pair` | the two `int4` |
| `advisory_lock.mode` | string | at start | `exclusive` or `shared` |
| `advisory_lock.scope` | string | at start | `transaction` or `session` |
| `advisory_lock.try` | bool | at start | `true` for every try, including a timed try and one with a zero timeout |
| `advisory_lock.timeout_ms` | long | at start, timed tries | the timeout in milliseconds, 0 for a zero timeout |
| `advisory_lock.outcome` | string | at stop | one of the outcomes below |
| `error.type` | string | at stop, `timed_out` and `error` | the full name of the exception type |

| Outcome | The attempt ended by | Activity status |
|---|---|---|
| `acquired` | the lock was taken | unset |
| `refused` | a try answered `false`: the lock was held, or still held when a timed try's `lock_timeout` ran out | unset |
| `timed_out` | a wait cut off by the caller's `lock_timeout` (SQLSTATE `55P03`), by `statement_timeout` (`57014`) or by the client's command timeout | error |
| `cancelled` | the `CancellationToken` | unset |
| `error` | any other exception, including a deadlock and a lost connection | error |

SQLSTATE `57014` also covers a cancel request from another session (`pg_cancel_backend`), which cannot be told
apart from `statement_timeout`.

The key tags carry the key's text and ids as given. Leave the source unsubscribed, or drop the tags, where a lock
name is confidential.

A plain `ActivityListener` works too:

```csharp
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == AdvisoryLockTelemetry.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity => Console.WriteLine($"{activity.GetTagItem("advisory_lock.outcome")} after {activity.Duration}"),
};
ActivitySource.AddActivityListener(listener);
```

## Upgrading

The package follows semantic versioning, and the [CHANGELOG](../../CHANGELOG.md) lists each release. Key
encodings do not change between releases, so replicas running different releases during a rolling deployment
contend on the same locks. Changing the namespace id of a lock changes its identity, so do it only when no
replica that uses the old id is running.

## Troubleshooting

See the [FAQ](../faq.md).
