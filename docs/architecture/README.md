# Architecture

## Scope

`Reeve.EntityFrameworkCore.AdvisoryLocks` wraps PostgreSQL advisory locks for Entity Framework Core and
Npgsql with typed keys: transaction-scoped locks on the current transaction (blocking, `try` and `try` with
a timeout; exclusive or shared), session locks for leader election and start-up steps, a namespace registry
that partitions the database's lock space, and tracing through an `ActivitySource`. All `pg_advisory_*` SQL
lives in one internal file.

It does not manage connections or transactions, retry, or provide a lock abstraction over other stores. It
depends on Npgsql and `Microsoft.EntityFrameworkCore.Relational` 11 and on nothing else.

## Building blocks

| Type | Role |
|---|---|
| `AdvisoryLockKey` | The identity of a lock. Factories: `Of(namespace, long)`, `Of(namespace, string)`, `FromRaw`, `FromHashedText`, `FromIntPair`. Two keys that render the same SQL arguments take the same lock. |
| `AdvisoryLockNamespace` | A per-process registry of 16-bit namespace ids claimed under a name. |
| `AdvisoryLockMode` | `Exclusive` (the `pg_advisory_*` functions) or `Shared` (the `*_shared` functions). |
| `AdvisoryXactLockExtensions` | `AdvisoryXactLockAsync`, `TryAdvisoryXactLockAsync` and the timed try, as extensions on `DatabaseFacade` and `NpgsqlTransaction`. |
| `AdvisorySessionLock` | `LockAsync`, `TryLockAsync`, `UnlockAsync` and `IsHeldAsync` over `NpgsqlConnection` and `DatabaseFacade`. |
| `AdvisoryLockTelemetry` | The `ActivitySource` name. |
| `AdvisoryLockSql` (internal) | The only file that spells a `pg_advisory_*` statement, builds the command and sets its timeout. |
| `AdvisoryLockActivity` (internal) | Starts and stops the trace activity around an acquisition. |

## Runtime model

Every call except the timed try builds one `NpgsqlCommand` through `AdvisoryLockSql`, starts a trace activity
for an acquisition if anyone listens, and executes the statement on the transaction's or the connection's own connection. Nothing
is cached and nothing runs in the background. The library holds only the process-wide namespace registry.

A transaction lock ends with its transaction, so there is nothing to release. A session lock ends at
`UnlockAsync` or when the server drops the session; taken twice, it needs two unlocks.

### The timed try

`TryAdvisoryXactLockAsync(key, timeout)` is the only form that runs more than one statement. Its lock statement
comes from `AdvisoryLockSql`; the savepoint and the two settings are plain commands:

1. Take a savepoint.
2. Read the caller's `lock_timeout` and `statement_timeout`.
3. Set `lock_timeout` to the timeout and `statement_timeout` to 0, locally to the transaction.
4. Run the lock statement with a command timeout of at least the timeout plus five seconds, or none when the
   caller passes an infinite `commandTimeout`.
5. If the server refuses with `lock_not_available`, roll back to the savepoint, release it and answer `false`.
6. If the lock is won, restore the caller's two settings and release the savepoint, ignoring the cancellation
   token, and answer `true`.
7. If a cancellation or a server error interrupts the wait, roll back to the savepoint and rethrow.

A zero timeout skips all of this and runs the non-waiting `try` statement.

## Invariants

- A key renders the same SQL in every release, so replicas running different versions contend on the same
  locks.
- The timed try sets the command timeout of its lock statement above its `lock_timeout`, so the server answers
  first. Otherwise a lost wait would surface as a client timeout that aborts the whole transaction. The other
  calls take the caller's `commandTimeout`, else the ambient one.
- A failing statement aborts the entire transaction unless a savepoint stands behind it. The timed try runs
  inside one; the plain waits do not, and abort the transaction when they end by an error.
- Once a lock is won, restoring the settings and releasing the savepoint ignore the cancellation token. A lost
  connection cannot be rolled back, so the error being handled is the one reported.
- A namespace id is never reused or renumbered by its owner. The library reserves only id 0.
- Two-`int4` keys are a lock space of their own: `pg_advisory_xact_lock(first, second)` never conflicts with a
  bigint key.
- `pg_locks` cannot tell a session lock from a transaction lock, so "does this session hold the key in this
  mode" is answered from `pg_locks` for the mode asked.
- Each acquisition attempt is one trace activity that starts before the lock statement is sent; a timed try
  is one acquisition as a whole. A release is not an acquisition. Cancellation is not recorded as a failure;
  timeouts and other errors are.

How to build and run the tests is in [CONTRIBUTING](../../CONTRIBUTING.md).
