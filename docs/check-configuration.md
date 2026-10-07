# Strict check configuration v1

On Linux, strict execution requires the util-linux `setsid` implementation with `setsid --wait` support. The tool
probes this capability before launching child processes and fails closed with an explicit compatibility diagnostic;
BusyBox `setsid` variants without `--wait` are not supported.

Strict repository execution is described by `mutate4csharp.json` at the captured repository root. The
machine-readable contract is [`contracts/check-config-v1.schema.json`](contracts/check-config-v1.schema.json),
and the repository root contains a working example. Unknown properties, duplicate JSON properties or IDs,
unsupported versions/runners, empty mappings, rooted or escaping paths, duplicate mappings, conflicting suite
aliases, and ambiguous source membership are rejected before any test process is launched.

Each production project declares a stable ID, repository-relative `.csproj`, target framework, parse-context
identity, source globs, and one or more test-suite IDs. Each suite declares a repository-relative project or
solution, runner, framework, configuration, and the expected test-assembly members that must be visible in TRX.
Expected-member names are unique case-insensitively at runtime (JSON Schema `uniqueItems` additionally catches exact
duplicates). Alias paths are execution-equivalent after converting backslashes to forward slashes and applying invariant
case folding, independent of the host platform. Equivalent aliases with the same runner, framework, configuration, and
expected members share one execution identity and run one baseline for a captured snapshot; differences in any of those
settings are refused as conflicts.

The runnable path and every projected scope path use the sole existing host-path equivalence class under the planning root.
On a case-sensitive host this selects the alias spelling that exists; on Windows equivalent spellings remain one runnable
path and stable ordinal spelling selects its published representation. If no spelling exists, stable alias-ID ordering
supplies the path and ordinary captured-path validation fails closed. If multiple equivalent spellings identify distinct
existing paths on a case-sensitive host, configuration loading refuses the ambiguity instead of silently omitting one
project. This path-resolution rule does not weaken canonical repository-relative path validation: version 1 configuration
paths still use forward slashes.

Version 1 supports only:

- .NET 10 (`global.json` currently pins SDK 10.0.103);
- Windows and Linux VSTest execution with TRX evidence;
- xUnit through the VSTest adapter; and
- Coverlet `XPlat Code Coverage` producing OpenCover XML, as verified by the repository's real-process
  `VstestSuiteExecutor` integration fixture. The fixture also verifies TRX member accounting, configured framework
  and configuration, owned coverage lifetime, and timeout/cancellation process-tree cleanup.

Microsoft.Testing.Platform, custom shell runners, other test adapters, macOS, and other coverage formats are not
claimed. Configuring a runner other than exact `vstest` fails clearly. Ordinary repository build and test code is
trusted code; orchestration isolation is an input/reproducibility boundary, not a hostile-code sandbox.

The policy defaults are one worker, 100 deterministic evaluation units, a 600-second baseline timeout, a
120-second per-mutant suite timeout, an 1,800-second overall deadline, `allowNotApplicable: false`, and one
stability attempt. Values must be positive and bounded. A phase receives the lesser of its configured timeout and
the remaining overall deadline. Timeout or cancellation is inconclusive: it stops new assignment, awaits the
owned execution path, accounts unassigned units as omitted, and forces `INCOMPLETE`.

All mapped suites must be accounted. A green baseline must be nonempty, have valid TRX for every expected member,
and produce fresh OpenCover output. Baselines complete before mutant assignment. During mutant aggregation, one
suite killing a mutant is conclusive only when every other mapped suite is conclusive; timeout, missing/invalid
TRX, host error, cancellation, or missing membership forces an error even if another suite killed it. Failed-test
identities and diagnostics are bounded. Survivor evidence records the source location and exact change and emits
a diagnostic `--mutation-id` plus `--plan-fingerprint` rerun command. The command preserves the original selection:
base selections remain `--base`, while explicit selections repeat each exact `--input`. If the JSON argv would exceed
the 512-character diagnostic bound it is deterministically omitted with an explicit reconstruction instruction;
targeted reruns cannot prove full-scope success.

Configured `exclusions` are applied during strict scope planning before project ownership is resolved. Every
matching exclusion is published as `CONFIGURED_EXCLUSION` with its required reason; the canonical scope plan binds
that path and reason into report and evaluation fingerprints.

The public command currently validates and fingerprints this configuration but still ends at
`ENUMERATION_NOT_IMPLEMENTED`, because canonical mutation enumeration is intentionally integrated later. No
baseline is launched before that prerequisite exists.
