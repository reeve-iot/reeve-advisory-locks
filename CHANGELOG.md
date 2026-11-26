# Changelog

All notable changes to `Reeve.EntityFrameworkCore.AdvisoryLocks` are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 1.0.1 - 2026-11-26

### Changed

- The README is shorter. The full key, timeout and tracing reference moved to the application guide
  (`docs/operations/README.md`), which also documents `AdvisoryLockNamespace.Registered`, the `mode` of
  `UnlockAsync` and `IsHeldAsync`, key equality, argument limits and error cases.
- Build and test commands are documented in CONTRIBUTING only, not in the package README.
- Each release now also has a GitHub Release with its CHANGELOG section.

## 1.0.0 - 2026-11-25

First release. The package targets `net11.0`, depends on `Npgsql` 11 and
`Microsoft.EntityFrameworkCore.Relational` 11, and ships its XML documentation.

### Added

- `AdvisoryLockKey`: `Of(namespace, long)`, `Of(namespace, string)`, and `FromRaw`, `FromHashedText` and
  `FromIntPair` for keys fixed elsewhere.
- `AdvisoryLockNamespace`: a per-process registry that refuses a name or an id claimed twice.
- Transaction-scoped locks on `DatabaseFacade` and `NpgsqlTransaction`: `AdvisoryXactLockAsync`,
  `TryAdvisoryXactLockAsync` and a timed try, exclusive or shared.
- `AdvisorySessionLock`: `LockAsync`, `TryLockAsync`, `UnlockAsync` and `IsHeldAsync` over `NpgsqlConnection`
  and `DatabaseFacade`.
- An optional `commandTimeout` on the waiting methods, the session `try` and `unlock`, and the timed try. The
  timed try always answers within its own timeout.
- The timed try suspends `statement_timeout` for its wait and, on a cancellation or a server error, rolls back
  to its savepoint so the transaction stays usable.
- Tracing: an `ActivitySource` named like the assembly (`AdvisoryLockTelemetry.ActivitySourceName`, version
  equal to the package version) emits one activity per acquisition attempt, started before the lock
  statement is sent, with the key, mode, scope, try flag and timeout as tags and the outcome (`acquired`,
  `refused`, `timed_out`, `cancelled`, `error`) when it stops. Without a listener no activity is created.
  Unlock and `IsHeldAsync` emit nothing.
