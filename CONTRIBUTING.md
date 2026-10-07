# Contributing to mutate4csharp

## Supported setup

- Install the .NET SDK selected by [`global.json`](global.json) (`10.0.103`, with latest-patch roll-forward).
- Use Git and the repository's committed NuGet configuration and lock files. The root `NuGet.Config` clears user and machine feeds; add only an approved mirror with equivalent source mapping if nuget.org is unavailable.
- Build and test are supported on Linux and Windows as configured in CI. The packaged local-candidate workflow additionally requires Linux Bash, GNU `realpath`, GNU `stat`, and Python 3.

Restore, build, and run the serialized suite from the repository root:

```bash
dotnet restore mutate4csharp.slnx --locked-mode
dotnet build mutate4csharp.slnx -m:1 --no-restore
dotnet test mutate4csharp.slnx -m:1 --no-build --no-restore -- xUnit.MaxParallelThreads=1
```

The package is not published publicly. To exercise the pinned local-tool workflow from a clean commit, follow [`docs/agent-workflow.md`](docs/agent-workflow.md) and use `scripts/agent-gate.sh prepare`; direct `dotnet pack` is intentionally refused.

## Issues and pull requests

- Search existing issues before opening one. Include a minimal reproduction, expected and actual behavior, operating system, and `dotnet --info` output where relevant.
- Keep pull requests focused, explain the behavior and safety implications, and add or update tests for observable changes.
- Run the build and relevant tests locally. Preserve fail-closed behavior, deterministic ordering, locked dependencies, and source-tree immutability unless the change explicitly revises a documented contract.
- Do not claim support, package publication, or hosted-CI results that have not been verified.
- Report security concerns using [the security policy](.github/SECURITY.md), not an ordinary issue containing sensitive details.

Do not commit generated build/test/package outputs or local state such as `bin/`, `obj/`, `TestResults/`, `.fire-off/`, `.mutate4csharp/`, `.nupkg` files, local feeds, receipts, reports, IDE state, credentials, secrets, private paths, or private data. Commit lock files and other generated inputs only when they are intentionally repository-managed and the related dependency change requires them.
