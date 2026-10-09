# Verified support matrix

The release candidate is intentionally narrow. Evidence is scoped by mode; this is not a statement that nearby
combinations probably work.

| Mode/evidence | Verified contract |
| --- | --- |
| Packaged strict local-tool fixture | `net10.0`; SDK `10.0.103` with `rollForward: latestPatch`; locally verified Linux Bash prepare/verify/gate workflow and local-only manifest restore/invocation; Windows build/test is CI-configured, while package creation runs only through the Linux Bash prepare workflow; neither hosted OS is claimed successful until its CI run completes; supported semantic enumeration returns exit `4` / `INCOMPLETE` / `EXECUTION_NOT_IMPLEMENTED`; strict `--no-state` source/index/status/state immutability is established only by a receipt captured around the same run |
| In-repository strict fixtures | Snapshot, scope, configuration, canonical semantic enumeration, deterministic selection, report, and fail-closed orchestration contracts. Current strict execution does not run VSTest, TRX, xUnit, or Coverlet. |
| Legacy/in-repository real-process fixtures | VSTest runner with TRX; xUnit through `xunit.runner.visualstudio`; Coverlet `XPlat Code Coverage` in OpenCover format; `xUnit.MaxParallelThreads=1` for serialized real-process suites |

Microsoft.Testing.Platform, NUnit, MSTest, macOS, older target frameworks, other coverage formats, and custom
shell runners are not claimed. Ordinary project/test execution is trusted code; the tool is not a security
sandbox. The shipped orchestration script is currently a Linux Bash workflow and requires GNU `realpath`, GNU
`stat`, and Python 3 with isolated imports for report validation and safe XML rewriting. Windows CI currently
covers build/test configuration, not package creation or execution of the Bash orchestration script. Direct pack
is intentionally refused on every platform.

## Representative observation

The packaged strict end-to-end fixture uses one changed production file and one mapped xUnit suite. It validates
local restore, invocation, strict configuration/scope loading, and the current fail-closed boundary; it does not
run that suite. At the current integration boundary a supported self-contained project reports `INCOMPLETE` with
a known `counts.enumerated` value and `counts.executed: 0` because execution is deliberately not connected.
VSTest/TRX/xUnit/Coverlet
claims above come only from legacy/in-repository real-process fixtures. That is a contract measurement, not zero mutation sites.
Build/test/restore timing varies with host, cache, and project size; no universal runtime promise is made.
