# Contributing

## Building and testing

You need the .NET 11 SDK. The integration tests also need Docker: they start PostgreSQL 18 with Testcontainers.

```bash
dotnet build Reeve.AdvisoryLocks.slnx -c Release
dotnet test tests/Reeve.EntityFrameworkCore.AdvisoryLocks.Tests -c Release              # no database
dotnet test tests/Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests -c Release   # needs Docker
```

For how the library works internally, see [docs/architecture](docs/architecture/README.md).

## Pull requests

Open pull requests against `main`. The CI workflow (`.github/workflows/ci.yml`) builds and runs both test
projects on every pull request and every push to `main`. Update the affected page under `docs/` and the
CHANGELOG in the same pull request as the change.

Renovate keeps NuGet packages and GitHub Actions versions up to date. .NET major versions are bumped by hand
together with the target framework.

## Releasing

1. Move the changes under a new version heading in `CHANGELOG.md` and set `<Version>` in
   `Directory.Build.props` to the same version.
2. Merge to `main`, then push the tag `vX.Y.Z` on `main`.

The Release workflow (`.github/workflows/release.yml`) builds, runs the unit tests and packs with the
version taken from the tag (the integration tests run in CI). It publishes the package to nuget.org using
trusted publishing, then creates the GitHub Release for the tag, with the version's `CHANGELOG.md` section as
its notes and the package attached. The workflow stops before building when `CHANGELOG.md` has no section for
the version.

A stable version is published once. To correct a bad release, publish the next version.

Publishing needs the `NUGET_USER` Actions variable (the nuget.org account name) and a nuget.org trusted
publishing policy for this repository and `release.yml`.

`README.md` is packed as the NuGet readme, so it must stay at the repository root and its links to other
repository files must be absolute.
