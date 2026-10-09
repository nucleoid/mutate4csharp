# Verified support matrix

The release candidate is intentionally narrow. Evidence is scoped by mode; this is not a statement that nearby
combinations probably work.

| Mode/evidence | Verified contract |
| --- | --- |
| Packaged strict local-tool fixture | `net10.0`; SDK `10.0.103` with `rollForward: latestPatch`; locally verified Linux Bash prepare/verify/gate workflow and local-only manifest restore/invocation; Windows build/test is CI-configured, while package creation runs only through the Linux Bash prepare workflow; supported frozen execution returns exit `4` / `INCOMPLETE` / `FINALIZATION_PENDING`; strict `--no-state` source/index/status/state immutability is established only by a receipt captured around the same run |
| In-repository strict fixtures | Snapshot, scope, configuration, canonical enumeration, fresh VSTest/TRX/xUnit/Coverlet baseline coverage, isolated mutants, deterministic selection, report, and fail-closed orchestration contracts. Final PASS publication remains disabled. |
| Legacy/in-repository real-process fixtures | VSTest runner with TRX; xUnit through `xunit.runner.visualstudio`; Coverlet `XPlat Code Coverage` in OpenCover format; `xUnit.MaxParallelThreads=1` for serialized real-process suites |

Neither hosted OS is claimed successful until its CI run completes for the exact release candidate.

Microsoft.Testing.Platform, NUnit, MSTest, macOS, older target frameworks, other coverage formats, and custom
shell runners are not claimed. Ordinary project/test execution is trusted code; the tool is not a security
sandbox. The shipped orchestration script is currently a Linux Bash workflow and requires GNU `realpath`, GNU
`stat`, and Python 3 with isolated imports for report validation and safe XML rewriting. Windows CI currently
covers build/test configuration, not package creation or execution of the Bash orchestration script. Direct pack
is intentionally refused on every platform.

## Representative observation

The packaged strict end-to-end fixture uses one changed production file and one mapped xUnit suite. It validates
local restore, invocation, strict configuration/scope loading, fresh coverage, and isolated mutation execution.
At the current integration boundary a supported self-contained project reports real unit dispositions and a
nonzero executed count, but remains `INCOMPLETE` with `FINALIZATION_PENDING` until final verification is connected.
That is a deliberate release boundary, not a claim that execution is absent.
Coverlet 6.0.4 OpenCover reports are line-granular (`sc=1`, `ec=2` placeholders), so strict execution treats
their exact-span coverage as unknown and executes those mutants conservatively. Strict `Uncovered` evidence is
available only from a collector that supplies non-placeholder complete sequence-point spans; the tool never falls
back to line-only coverage for this decision.
Build/test/restore timing varies with host, cache, and project size; no universal runtime promise is made.
