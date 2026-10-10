# mutate4csharp

`mutate4csharp` is a clean-room mutation-testing CLI for C# and .NET 10. Its established CLI discovers mutation sites in a selected source file with Roslyn, establishes a green test baseline, uses OpenCover line coverage, and runs every selected mutant in a private copy of the owning repository or solution. Its preview agent interface adds deterministic Git-aware multi-file change scope, frozen repository inputs, canonical semantic mutation enumeration, isolated suite orchestration, and versioned reports; strict baseline and mutant execution are not yet connected.

The repository pins .NET SDK `10.0.103` in `global.json` and CI to keep local and hosted builds on the same feature band.

## Install the pinned local candidate

There is currently no publicly published NuGet package. Do not use `dotnet tool install` against a public feed and assume it represents this repository. The supported candidate workflow builds a commit-identified package from a clean local checkout and restores it only from an external local feed.

The local candidate workflow is fail-closed and remains local-only. The shipped
[`scripts/agent-gate.sh`](scripts/agent-gate.sh) requires a clean Git pack input, derives a
unique prerelease version from the full source commit, embeds that revision in the executable, and restores to an
external tool path through a config containing only the external local feed. Feed, caches, identity receipt, and
strict report all remain outside the gated repository. Every later invocation requires independently approved
package and complete-payload hashes, tool-source commit, consumer task baseline, and consumer current HEAD. The
receipt is inert data and is never sourced or allowed to select the executable. Raw direct pack is fail closed;
the only supported candidate path is `prepare`. It supplies the complete isolation contract, rejects recognized
ancestor controls plus ignored MSBuild/NuGet/editorconfig/global-analyzer controls, records one canonical host and
SDK, scrubs runtime injection hooks, and redirects intermediates and outputs outside the clone. Ordinary contributor
restore/build/test remain unchanged; production and test dependency graphs are committed and restored in locked mode.

The repository-level [`NuGet.Config`](NuGet.Config) maps the exact `mutate4csharp` ID only to the local feed while
ordinary dependencies can use nuget.org. Its `<clear />` affects every contributor restore in this checkout by
discarding user and machine feeds; approved mirrors must be added explicitly with equivalent source mapping. The
example's nested configuration is intended only for the extracted example, because strict preparation refuses the
root and nested files as multiple applicable NuGet.Config files inside this source checkout. Public package
publication is not part of this workflow. Follow the literal pack, restore, identity, and agent-loop commands in
[`docs/agent-workflow.md`](docs/agent-workflow.md). The exact verified environment is listed in
[`docs/supported-matrix.md`](docs/supported-matrix.md).

## Usage

```bash
mutate4csharp src/MyLibrary/Flag.cs --test-project tests/MyLibrary.Tests/MyLibrary.Tests.csproj
mutate4csharp src/MyLibrary/Flag.cs --scan
mutate4csharp src/MyLibrary/Flag.cs --update-manifest
mutate4csharp src/MyLibrary/Flag.cs --lines 12,18
mutate4csharp src/MyLibrary/Flag.cs --since-last-run
mutate4csharp src/MyLibrary/Flag.cs --mutate-all
mutate4csharp src/MyLibrary/Flag.cs --reuse-coverage --coverage-report TestResults/coverage.opencover.xml
```

Strict agent evaluation is an additive, versioned **preview** interface:

```bash
/external/pinned-tool/mutate4csharp check --base HEAD~1
/external/pinned-tool/mutate4csharp check --input src/MyLibrary/Flag.cs --report /external/report.json
```

Strict checks now capture bounded current working-tree bytes, validate `mutate4csharp.json`, and produce a deterministic versioned
change-scope plan and, for the supported captured project boundary, canonical mutation and evaluation-unit IDs. Git mode compares the once-resolved direct base commit (never a merge base) with
captured working-tree bytes, including staged, unstaged, and eligible untracked changes without
duplicating paths. Explicit repeated inputs also work outside Git for scope planning, but strict semantic enumeration
requires a Git-backed project snapshot. Suite orchestration prepares frozen dependencies, runs fresh
baseline/coverage evidence, and executes selected mutants in isolated clones with bounded scheduling.
Public strict `check` reconciles the complete trusted unit ledger after original-tree revalidation and owned-resource
cleanup. Complete nonzero all-killed work returns `PASS`; survivors or conclusively uncovered mutations return
`FAIL`; known zero effective work follows the explicit `NOT_APPLICABLE` policy. Partial, unstable, cancelled, or
errored work remains `INCOMPLETE`. Unsupported semantic contexts publish precise enumeration
refusals with an unknown count. Usage rejection,
snapshot refusal, and exception paths differ and may not reach enumeration or report publication. The strict interface writes an
atomic JSON report and uses fail-closed outcomes. Its report schema,
exit precedence, accounting rules, and current frozen-capture gate are documented in
[`docs/evaluation-contract.md`](docs/evaluation-contract.md). Legacy single-file invocations and
embedded-manifest behavior retain their existing CLI and mutation semantics and are not strict gating evidence;
they do not receive the strict snapshot mode's MSBuild isolation arguments. All launched test processes now share
bounded output capture and descendant-aware timeout/cancellation cleanup.
Snapshot boundaries, dependency freezing, drift checks, and unsupported external resources are documented in
[`docs/snapshot-inputs.md`](docs/snapshot-inputs.md).
Scope discovery, configuration, declaration expansion, exclusions, and current limitations are
documented in [`docs/change-scope.md`](docs/change-scope.md).
Repository configuration, suite identity/deduplication, supported runners, and execution policy are
documented in [`docs/check-configuration.md`](docs/check-configuration.md).
Stable mutation/evaluation-unit identity, canonical budget/report ordering, targeted reruns, fixed policy limits,
and stability evidence are documented in [`docs/reproducibility.md`](docs/reproducibility.md).

Run `mutate4csharp --help` for all flags and exit codes. `--project`, `--test-project`, and `--root` remove ambiguity in unusual layouts and paths containing spaces.

### Modes and safety

- Exactly one non-test `.cs` target is accepted.
- `--scan` runs no child command and writes nothing.
- `--update-manifest` runs no tests or coverage command; its only write is the target's embedded manifest.
- A normal run executes `dotnet test -m:1`, aggregates every emitted TRX, and requests fresh Coverlet OpenCover coverage. `--reuse-coverage` or `--coverage-report` explicitly opts into existing coverage. Only an explicit valid `vc="0"` sequence point is uncovered; absent targets/lines, hidden points, unsupported XML, and incomplete reports are *unknown*, so those sites execute with a warning rather than being silently skipped.
- A red baseline or a baseline discovering zero tests aborts before mutation.
- Every mutant—including `--max-workers 1`—runs in a fresh private repository copy. `.git`, `bin`, `obj`, `TestResults`, tool artifacts, common secret files, and IDE state are excluded. External project references and symlink/reparse-point trees are rejected.
- Mutant processes have a baseline-derived timeout and are killed as a process tree. Results are printed in source order as `KILLED`, `SURVIVED`, `TIMEOUT`, `UNCOVERED`, `COMPILE_ERROR`, or `ERROR`.
- Custom shell test commands are intentionally deferred. Use `--test-project` with a project or solution so argument handling, TRX classification, and process-tree cleanup remain reliable.

## Mutations

Roslyn syntax and semantic analysis supports:

- `true` ↔ `false`
- `==` ↔ `!=`
- `<` ↔ `<=` and `>` ↔ `>=`
- `+` ↔ `-` and `*` ↔ `/` (`+` excludes semantic string concatenation)
- `&&` ↔ `||`
- removal of unary `!` and numeric unary `-`
- integer `0` ↔ `1`
- direct rvalues replaced by `null` only when Roslyn reliably resolves a reference type

Comments, strings as operator text, character literals, and generic angle brackets are never token-scanned as mutations.

## Differential manifest

The target may contain a versioned Base64/JSON footer comment recording stable declaration IDs, semantic hashes, source hash, line ranges, and a context fingerprint. The context includes the selected project and test suite, relevant execution options, reused coverage inputs, source dependencies, tests, project files, props/targets, run settings, and relevant JSON/configuration. It is recomputed before automatic manifest advancement. A malformed manifest causes a conservative rerun.

The footer uses a conditional atomic replacement: the source is checked against the analyzed text before staging and again immediately before replacement. This narrows, but cannot eliminate, the final check-to-replace race because portable file APIs do not provide compare-and-swap. Automatic advancement does not occur after a red baseline, survivor, timeout, compile/error result, cancellation, infrastructure failure, or line-filtered partial run. `--update-manifest` is the explicit manual exception.

## Build and test

```bash
dotnet build mutate4csharp.slnx -m:1
dotnet test mutate4csharp.slnx -m:1
TOOL_PARENT=$(mktemp -d)
TOOL_WORKSPACE="$TOOL_PARENT/workspace"
scripts/agent-gate.sh prepare "$(git rev-parse --show-toplevel)" \
  "$TOOL_WORKSPACE" "$TOOL_PARENT/receipt.tsv"
```

The suite includes Roslyn unit coverage and bounded real `dotnet test` integrations for killed/survived/timeout/red-baseline behavior, Coverlet OpenCover collection, path spaces, source isolation, scan/update side effects, flags, and context invalidation.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for the supported setup, build/test workflow, and pull request expectations. To report a vulnerability, follow [.github/SECURITY.md](.github/SECURITY.md) and never put secrets or exploit details in a public issue.

## Attribution and license

The behavior and workflow were inspired by Robert C. Martin's [`unclebob/mutate4java`](https://github.com/unclebob/mutate4java), pinned for behavioral study at commit [`7b05fdd71e8fe36327aff837806dfbff86af0572`](https://github.com/unclebob/mutate4java/tree/7b05fdd71e8fe36327aff837806dfbff86af0572). See [ATTRIBUTION.md](ATTRIBUTION.md).

No upstream Java source or tests are vendored, copied, translated, or executed here. The upstream snapshot had no declared license, so its code is **not** relicensed. The MIT license in this repository covers only the independently authored `mutate4csharp` code and documentation.
