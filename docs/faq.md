# FAQ

## Known problems

### `current transaction is aborted, commands ignored until end of transaction block` after a lock call

Cause: a wait (`AdvisoryXactLockAsync`) ended by a statement error: the command timeout, the server's
`statement_timeout`, a cancellation or a deadlock. A failing statement aborts the whole transaction unless a
savepoint stands behind it, and the plain waits have none. Every later statement in that transaction fails with
the same message.

Fix: roll the transaction back. Where losing the wait is expected, use `TryAdvisoryXactLockAsync(key, timeout)`:
it waits behind a savepoint and leaves the transaction usable.

### A timed try threw after the connection dropped

Cause: the connection was lost mid-wait, or `transaction_timeout` (PostgreSQL 17 and newer) ended the session.
Neither can be rolled back to the savepoint.

Fix: handle the exception and open a new connection. The exception is the original one, not a failed rollback.

### `ArgumentOutOfRangeException` from a timed try

Cause: the `commandTimeout` argument is not longer than `timeout`, so it would cut the wait off before the server
could answer. A negative `timeout`, or one above `int.MaxValue` milliseconds, is refused the same way.

Fix: pass a `commandTimeout` longer than the timeout, or none. Without one the package uses the timeout plus five
seconds.

### `InvalidOperationException`: a transaction-scoped advisory lock needs a transaction

Cause: the lock was requested on a `DatabaseFacade` without an active transaction, or on a transaction that has
ended.

Fix: begin a transaction first, or use `AdvisorySessionLock` where the lock has to outlive one.

### `InvalidOperationException`: advisory locks need the Npgsql provider

Cause: the `DbContext` uses another database provider.

Fix: use the Npgsql provider (`UseNpgsql`). Advisory locks exist only in PostgreSQL.

### A session lock stays held after the connection was closed

Cause: closing a pooled connection returns it to the pool and does not end the session.

Fix: take session locks on an unpooled connection (`Pooling=false`), or call `UnlockAsync` before closing the
connection.

### A session lock is silently lost

Cause: a multiplexed connection or a transaction-pooling proxy sent a later statement to another session.

Fix: use a dedicated, direct, unpooled connection for session locks, or use the transaction forms.

### `UnlockAsync` returns `false`

Cause: this session does not hold the lock in the mode given. A lock taken with `AdvisoryLockMode.Shared` has to be
released with `AdvisoryLockMode.Shared`, and PostgreSQL logs a warning for an unlock of a lock that is not held.

Fix: pass the same mode as the lock call, on the same connection.

### A session-lock call on a `DatabaseFacade` throws that the connection is not open

Cause: EF closes a connection it opened itself after each operation, which would end the session and the lock.

Fix: open the connection first with `Database.OpenConnectionAsync` and keep it open while the lock is held.

### An exception when registering a namespace

Cause: `InvalidOperationException` means the name or the id already belongs to another registration in this
process. `ArgumentOutOfRangeException` means the id is 0, which is reserved.

Fix: choose another id. Registering the same name and id again is fine.

### A tracing outcome says `timed_out` but no SQLSTATE was logged

Cause: the client's command timeout ended the wait, and the server did not report a SQLSTATE. The outcome is still
`timed_out`, and `error.type` names the exception.

Fix: none needed.

### A trace listener sees a lock activity but the lock is still free

Cause: the activity starts before the lock statement is sent. A listener that holds the call in
`ActivityStarted` sees it before the server does.

Fix: read the outcome when the activity stops.

## Questions

### Which PostgreSQL versions are supported?

PostgreSQL 11 or newer, because of `hashtextextended`. The tests run on PostgreSQL 18.

### Can two different names collide?

Name keys are 64-bit hashes, so two different names collide with the probability of any 64-bit hash. A collision
makes two locks share one lock. It never lets two holders in.

### Why is namespace id 0 reserved?

It is the seed of `FromHashedText`, so `FromHashedText(text)` and `Of(namespace, text)` are the same hash with
different seeds.

### Does a transaction lock need releasing?

No. The transaction ends the lock, at commit or rollback.
