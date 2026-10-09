# Repository-local agent gate

`mutate4csharp` is installed as a pinned repository-local .NET tool. It is not an application
`PackageReference`, an automatic MSBuild target, or evidence that may be updated merely to make a check pass.

## Pack and restore a commit-identified package

Until an owner separately authorizes publication, run the shipped workflow from a clean Git checkout. The
workspace and receipt must be new absolute paths outside that checkout:

```bash
set -euo pipefail
TOOL_SOURCE_REPOSITORY=$(git rev-parse --show-toplevel)
TOOL_PARENT=$(mktemp -d)
TOOL_WORKSPACE="$TOOL_PARENT/workspace"
TOOL_RECEIPT="$TOOL_PARENT/receipt.tsv"
"$TOOL_SOURCE_REPOSITORY/scripts/agent-gate.sh" prepare \
  "$TOOL_SOURCE_REPOSITORY" "$TOOL_WORKSPACE" "$TOOL_RECEIPT"
```

The source repository and both targets must have non-symbolic path boundaries; the targets must be new, canonical,
external paths. `prepare` refuses existing receipt/workspace targets and does not follow repository, parent, or
receipt symlinks. Receipt publication validates all fields before an adjacent temporary file is
atomically moved without clobbering. It prints the package SHA-256, complete payload SHA-256 (including file modes),
tool-source commit, canonical trusted dotnet host, and orchestration SDK version. Receipt schema v3 retains the
legacy field name `runtime_version`, but that value is an SDK identity, not the .NET runtime framework version. A human or trusted
orchestrator must copy those three values into the later gate as **out-of-band approved inputs**. The receipt is
inert tab-separated data containing only the format, tool/runtime versions, and external paths. The gate parses a closed set of
keys; it never sources, evaluates, or executes receipt content, and it never trusts hashes or a commit merely
because the receipt says so.

Direct pack (`dotnet pack`) is fail closed and is not the supported candidate path. CI and local release checks must
use `agent-gate.sh prepare`, which invokes MSBuild with `-noAutoResponse` and the complete isolation contract. This
restriction does not affect ordinary contributor restore, build, or test.
The isolated MSBuild pack target independently runs Git. It refuses a missing repository, a `SourceRevisionId` different
from `git rev-parse HEAD`, any staged/tracked-worktree/untracked change reported by
`git status --porcelain=v1 --untracked-files=all`, and any ignored project
input outside `bin`/`obj`, ignored project-extension imports under `obj`, or ignored MSBuild, NuGet, `.editorconfig`,
`.globalconfig`, response, or SDK-selection controls anywhere in the repository. `prepare` also refuses recognized
build-control files above its clean clone rather than claiming to neutralize SDK semantics it does not control.
Detached CI checkouts are supported when they contain `.git`,
resolve to the exact approved commit, and are clean. Source archives without Git metadata are intentionally
rejected; create the package in CI before producing an archive. The script disables automatic MSBuild response
files, resolves every orchestration SDK command from one external neutral directory containing the clean source
checkout's tracked `global.json`, and redirects
`Directory.Build.props`/`Directory.Build.targets`, project extensions, all output paths, caches, feed, payload, receipt, and
reports outside the source checkout, then rechecks the executable identity. It rejects payload
symlinks rather than silently omitting them from integrity measurement. The committed root `.editorconfig` has
`root = true`, preventing analyzer configuration discovery above the clone.

The .NET installation is a trusted runtime boundary rather than part of the payload hash. `prepare` selects a
canonical host, copies the tracked source `global.json` into the external workspace's neutral SDK directory,
resolves the SDK there, and records the result. Later gate and verify-example checks resolve from that same
canonical directory, so a consumer global.json—whether absent, pinned to another installed feature band, or
requesting an unavailable SDK—cannot change the orchestration SDK identity. Strict evaluation still captures and
validates the consumer's own SDK-selection inputs as target data. Every prepare/gate/verify-example runtime
invocation starts from a deliberate minimal environment allowlist (`PATH`, temporary-directory, locale,
certificate, and explicitly assigned home/CLI/cache variables), then pins `DOTNET_ROOT` and disables multilevel lookup,
telemetry, first-run certificate generation, and first-run experience. Startup hooks, additional deps,
architecture-specific roots, profiler variables, MSBuild executable/SDK/extension hooks, additional SDK resolvers,
and unlisted environment-to-property injection are absent. Pack also redirects `MSBuildUserExtensionsPath`,
disables Common and C# user wildcard imports, and clears Common/C# custom target hooks. The same host and neutral
SDK directory are used for example restore/build/test. Before any receipt-bound runtime or example project operation,
`verify-example` refuses build-control files in ancestor directories and redirects `HOME`, `DOTNET_CLI_HOME`, XDG
state, NuGet packages, HTTP/plugin caches, and NuGet scratch space into its new external report directory. Those
project operations also redirect
`MSBuildUserExtensionsPath`, disable Common and C# user wildcard imports, and clear Common/C# custom target hooks;
the caller's `HOME` therefore cannot contribute user `ImportBefore`/`ImportAfter` files or NuGet user state. A
hostile `dotnet` earlier on `PATH` is excluded because gate execution binds the canonical host from the receipt;
other tools resolved from `PATH` are not claimed to be pinned. The payload hash proves installed tool files, not the trusted
runtime binaries.

The repository-root `NuGet.Config` deliberately clears contributor user and machine package sources for every
restore under this checkout; contributors needing an approved mirror must add it explicitly with equivalent
source mapping. The shipped example's `NuGet.Config` maps only `mutate4csharp` to its sibling local feed and maps
ordinary dependencies to nuget.org. Use that example configuration only after extraction: inside this source
checkout, the root and nested files are multiple applicable NuGet.Config files, which strict dependency
preparation intentionally refuses rather than merging.

The current public strict check **can never produce `PASS` or `FAIL` until mutation execution is connected**.
For a supported Git-backed configuration-v1 context, `check` returns exit `4` with `INCOMPLETE` and the
`EXECUTION_NOT_IMPLEMENTED` incomplete condition after publishing a bounded canonical mutation plan. Unsupported
semantic contexts fail closed with a specific enumeration refusal and an unknown total. Usage rejection, snapshot
refusal, and exception paths may stop earlier and may not publish a report. The PASS/FAIL handling below defines
the stable contract for the future execution connection.

The first enumeration envelope is deliberately narrow: an exact-`net10.0`, plain `Microsoft.NET.Sdk`,
self-contained project with statically provable compile items and no project/package/framework references,
conditions, explicit imports, inherited `Directory.Build.*`, source-generator dependency, or unmodelled project
property/item. Optional `nullable` and `defineConstants` values are assertions: when omitted, enumeration derives
them from the frozen project; when supplied, mismatches are refused. Projects outside this envelope produce a
validated `INCOMPLETE` report with an unknown enumeration count and retain tool exit `4`. The wrapper validates that
specific `ENUMERATION_*` shape but does not confuse it with a successful mutation result. Malformed reports,
receipt/hash drift, symbolic paths and other orchestration or integrity failures remain distinct exit `73`.

## Agent loop

At task start in the **consumer repository**, capture the direct commit that defines the comparison boundary.
Do not recompute it later. The consumer `TASK_START` and current target `HEAD` are unrelated to the tool-source
commit and are approved separately:

Capture `TASK_START` **before making task changes** and retain it unchanged for every rerun. At the final gate,
capture the separately approved current `HEAD` immediately before ordinary tests:

```bash
set -euo pipefail
# Captured once before work begins, for example by the task owner:
TASK_START=the-40-character-task-start-commit
EXPECTED_TARGET_HEAD=$(git rev-parse HEAD)
dotnet test YourSolution.sln -m:1 -- xUnit.MaxParallelThreads=1
TOOL_RECEIPT=/absolute/path/from/prepare.receipt.tsv
EXPECTED_PACKAGE_SHA256=the-64-character-package-hash-approved-out-of-band
EXPECTED_PAYLOAD_SHA256=the-64-character-payload-hash-approved-out-of-band
EXPECTED_TOOL_SOURCE_COMMIT=the-40-character-commit-approved-before-the-agent-started
REPORT_PARENT=$(mktemp -d)
MUTATE4CSHARP_REPORT="$REPORT_PARENT/report.json"
/approved/tool-source/scripts/agent-gate.sh gate \
  "$(git rev-parse --show-toplevel)" "$TOOL_RECEIPT" \
  "$EXPECTED_PACKAGE_SHA256" "$EXPECTED_PAYLOAD_SHA256" \
  "$EXPECTED_TOOL_SOURCE_COMMIT" "$TASK_START" "$EXPECTED_TARGET_HEAD" \
  "$MUTATE4CSHARP_REPORT" no-state
```

The gate requires `TASK_START` to resolve to that exact commit and requires the consumer repository's current
`HEAD` to equal `EXPECTED_TARGET_HEAD`; it does not compare either consumer commit to the tool-source commit.
Working-tree changes are expected task inputs. The receipt does not name an executable: the gate derives the
only allowed regular, non-symlink command (`mutate4csharp`, or `.exe` under a Windows-compatible Bash) from the
canonical payload directory. Package, payload, executable identity, target `HEAD`, and
`TASK_START` are checked before execution, and package/payload/target `HEAD` are checked again afterward.
Missing or malformed receipt fields, duplicate or unknown keys, host/SDK mismatch, hash drift, payload symlinks,
symbolic repository boundaries, identity drift, non-commit baselines, existing/symlinked/internal report paths, or
changed target `HEAD` fail before a result is accepted.
The current gate accepts a tool result only when a newly created regular report parses, its `exitCode` equals the
process exit, and it says `INCOMPLETE` / `4`. A supported plan must contain `EXECUTION_NOT_IMPLEMENTED` and a bounded
nonnegative `counts.enumerated`. A project outside the current semantic envelope may instead contain only validated
`ENUMERATION_*` incomplete conditions or known scope/selection blockers and must keep `counts.enumerated` null.
Both are nonpassing results. A crash
that merely exits 4, a malformed condition, or a contradictory count is refused as orchestration exit `73`.
All orchestration refusals use exit `73`, distinct from tool usage exit `1` and strict incomplete exit `4`.
The preflight and post-run checks reject existing and symbolic report targets, while the tool publishes atomically
under an adjacent lock. Hash and identity checks before and after execution narrow but do not eliminate TOCTOU: a
malicious same-account process with write access to the runtime, payload, package, repository, or report parent can
still race local pathname and file operations. This workflow does not claim protection from that adversary.

The tool and test projects, plus the extracted example projects, commit package locks and set both
`RestorePackagesWithLockFile` and `RestoreLockedMode`; locked restore is
therefore project policy, not merely a command-line convention. The shipped `verify-example` workflow also passes
`--locked-mode`, builds production and test projects with `--no-restore`, runs tests with
`--no-build --no-restore`, performs a plain tool-restore negative control, and executes both `no-state` and
`default-state` strict gates. Before those real project operations it queries each project through the same
receipt-bound host and neutral SDK directory, refuses a `NETCoreSdkVersion` or `MSBuildExtensionsPath` mismatch,
and records the actual values in `sdk-resolution.tsv`. This file describes orchestration of the example projects. Separately,
the accepted default-state report records the SDK that the tool itself resolves from the captured consumer snapshot as
`TOOL_INTERNAL_SDK` evidence; `verify-example` extracts that evidence into `tool-sdk-resolution.tsv`. These identities are
deliberately distinct: a consumer `global.json` may select a different installed SDK for tool-internal evaluation without
changing the receipt-bound orchestration SDK. The verification report directory must be a new external path. Its relative
`NuGet.Config` mapping sends only exact `mutate4csharp` to the
sibling local feed; the public source has explicit dependency families and no `*` candidate. Strict snapshot
capture still applies its documented ancestor-config rule: run it only after extracting the example so the source
checkout's root config is not a second applicable config.

In both `no-state` and `default-state` modes, the shipped gate refuses `SIDECAR_WRITE_FAILED` or
`SIDECAR_PUBLICATION_FAILURE`. For `default-state` only, it additionally requires a regular discovery record whose
report hash and length bind it to the accepted report. When a report was written, inspect both the human summary and JSON report. Review every survivor, known-uncovered site, omitted unit,
and incomplete condition. Improve production code or tests, rerun ordinary tests, and rerun the same configured
check against the same task-start commit. **Never bless state, edit a footer, hide an input, raise an exclusion,
or weaken policy merely to get green.** Sidecar state is provenance, not an agent attestation.

When the executable is directly on `PATH`, the equivalent grammar is:

```bash
mutate4csharp check --base "$TASK_START"
```

## Human and JSON handling

Human output identifies the run and report path when publication succeeds. The shipped gate requires a fresh report,
parses it with isolated Python import resolution, and binds its outcome and exit code to the process result; it never
infers success from prose. Tool exit `1`
is a usage error; specifically, exit `1` is a usage error from the tool and may not write a report. Missing/malformed reports, crashes, and other orchestration refusals become exit `73` rather than
being confused with tool usage or a validated strict incomplete result. An accepted strict result has this shape
(fields omitted here remain defined by the versioned schema):

```json
{
  "schemaVersion": "1",
  "outcome": "INCOMPLETE",
  "exitCode": 4,
  "incompleteConditions": [
    { "code": "EXECUTION_NOT_IMPLEMENTED" }
  ],
  "reasons": [
    { "code": "EXECUTION_NOT_IMPLEMENTED" },
    { "code": "BASELINE_UNKNOWN" },
    { "code": "UNIT_OMITTED" }
  ],
  "counts": { "enumerated": 1, "selected": 1, "executed": 0, "omitted": 1 }
}
```

`incompleteConditions[]` records the intrinsic blocker. `reasons[]` retains that blocker and the
reducer-derived `BASELINE_UNKNOWN` and per-unit omission codes; do not require the two arrays to be identical.

Handle outcomes as follows:

- `PASS` / exit `0`: the complete configured policy passed.
- `FAIL` / exit `3`: evaluation completed and found a policy failure, such as a survivor or known-uncovered site.
- `INCOMPLETE` / exit `2` or `4`: red/empty baseline, missing enumeration, timeout, cancellation, omitted work,
  or infrastructure prevented reusable success. Unknown enumeration is not zero mutations. Exit `1` is outside
  strict outcome mapping because usage rejection may not produce a report; exception exit `4` may likewise have
  no report.
- `NOT_APPLICABLE` / exit `5`: there are no effective valid candidates after complete execution. A future validated `allowNotApplicable: true`
  configuration may map this to exit `0`, while JSON still says `NOT_APPLICABLE`; it is
  never `PASS`. The current enumeration-only boundary reports `INCOMPLETE` even when the known candidate count is zero.

`INCOMPLETE` takes precedence over a known failure while retaining all reasons in JSON. Treat every nonzero exit
as a blocked gate and inspect the report rather than flattening all failures into one message.

## No-Git mode

Outside Git, select one or more explicit files. Inputs are repeatable and mutually exclusive with `--base`:

```bash
/external/pinned-tool/mutate4csharp check \
  --input src/MyLibrary/Flag.cs \
  --input src/MyLibrary/Rules.cs \
  --report artifacts/mutation-report.json
```

The validated root and `mutate4csharp.json` still define project ownership and suites. Explicit mode captures the
current bytes; it does not invent historical comparison state.

## Migrating from legacy file/footer mode

Legacy `mutate4csharp file.cs ...` commands remain available for compatibility, but embedded footers, manual
`--update-manifest`, line filters, and reused coverage are non-gating evidence. Adopt `mutate4csharp.json`, pin the
local tool, capture a task-start commit (or explicit inputs), and use `check`. Strict checks ignore an existing
footer as proof and do not edit production source. Do not silently rewrite an old acceptance command and claim
its prior semantics were strict.
