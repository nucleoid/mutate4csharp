# Verified support matrix

The release candidate is intentionally narrow. Evidence is scoped by mode; this is not a statement that nearby
combinations probably work.

| Mode/evidence | Verified contract |
| --- | --- |
| Packaged strict local-tool fixture | `net10.0`; SDK `10.0.103` with `rollForward: latestPatch`; locally verified Linux Bash prepare/verify/gate workflow and local-only manifest restore/invocation; Windows build/test is CI-configured, while package creation runs only through the Linux Bash prepare workflow; the representative surviving-mutant fixture returns exit `3` / `FAIL`; strict `--no-state` source/index/status/state immutability is established by a receipt captured around the same run |
| In-repository strict fixtures | Snapshot, scope, configuration, canonical enumeration, fresh VSTest/TRX/xUnit/Coverlet baseline coverage, isolated mutants, deterministic selection, PASS/FAIL/INCOMPLETE/NOT_APPLICABLE report reduction, and fail-closed discovery/proven publication contracts |
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
The representative changed expression has a surviving mutant, so the complete nonzero ledger returns `FAIL` / exit
`3`. Separate in-repository fixtures prove an all-killed `PASS`, incomplete execution, and explicit zero-effective
`NOT_APPLICABLE` reduction.
Coverlet 6.0.4 OpenCover reports contain line-granular placeholders. Strict execution can recover precise spans
from a portable PDB only after matching the assembly/PDB identity and checksum, captured source bytes and exact
compiled method signature. Zero visits must account for all relevant PDB sequence-point lines. Ambiguous or
unverified mappings remain unknown and execute conservatively; positive aggregate line visits do not prove
that an individual span executed. The tool never treats an unverified line-only report as uncovered proof.
Build/test/restore timing varies with host, cache, and project size; no universal runtime promise is made.

The uncovered projection is limited to the pinned Coverlet 6.0.4 / VSTest in-process collector
and ordinary straight-line methods without generated/async state-machine attributes or hidden sequence points.
Unsupported placeholder token/offset producers and those methods remain unknown and execute.
Zero visits describe coverage measured in that collector session, not execution in child applications,
child testhosts, or separate uninstrumented assembly copies. Those scenarios do not establish measured
coverage under this profile. Uncovered evidence records the collector profile and PDB aggregate projection.

Methods with calls, branches, exception handlers, generated state or hidden points do not receive aggregate-zero uncovered proof; their mutants execute. This avoids the instruction-skipping cases in [Coverlet 6.0.4 instrumentation](https://github.com/coverlet-coverage/coverlet/blob/v6.0.4/src/coverlet.core/Instrumentation/Instrumenter.cs).

The Bash gate's simple-casing interop is pinned to UnicodeData 15.0.0, with the .NET invariant
dotless-i exception and ordinal ASCII/non-ASCII distinction. The shipped table is checksum-bound;
Greek simple uppercase mappings are tested separately from Python's full uppercase expansions.
Names requiring newer Unicode casing are outside this verified interop profile and fail closed.
The table and Unicode license ship together. Source: [UnicodeData 15.0.0](https://www.unicode.org/Public/15.0.0/ucd/UnicodeData.txt).
Aggregate-zero projection is verified with Release compilation; Debug block-bodied methods commonly
contain return branches and therefore execute conservatively instead of receiving uncovered proof.
