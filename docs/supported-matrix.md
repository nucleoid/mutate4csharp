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

The installed contract fixture additionally packs a clean exact commit and installs from a single-source
local feed into an external tool path. Separate clean consumers prove strong-test PASS, weak-test survivor
FAIL, fresh uncovered FAIL, red/empty baseline INCOMPLETE and genuine zero-site NOT_APPLICABLE, in both
default-state and no-state modes. It checks source bytes, index/status, payload hash and complete unit counts;
only the strong fixture publishes proven state. Installed certification runs in the Linux packaging CI job.
Windows real-process CI covers the strict implementation; it does not certify the Bash gate or installed
contract fixture on Windows.
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

The Bash source-state guard inventories all ordinary consumer files, including ignored outputs, up to
100,000 files and 1 GiB total. Symlinks, special files, configured Git filters and undocumented state paths
are orchestration refusals, as are submodules, gitlinks and nested repositories. It uses `/usr/bin/git`, disables fsmonitor/untracked-cache/hooks, clears Git
environment overrides and system/global configuration, and binds local configuration into the digest.
Default state permits only the documented one-level JSON/lock directories and canonical `*\n` ignore bytes.
Repositories outside these bounds require a separately reviewed orchestration profile.

Installed fixtures cover policy-authorized N/A exit zero, plan-only operation, actual budget omissions,
unsupported encoding, baseline timeout, cancellation during baseline execution and original-input drift.
Ordinary outcome fixtures invoke the gate extracted from the exact package in both state modes; drift
must normalize to orchestration ERROR. Plan-only operation and baseline cancellation invoke the installed
tool directly. The cancellation test signals only that owned process and checks its baseline child exits.
After execution teardown, original-input revalidation and SDK identity finalization share one 30-second
post-cancellation grace budget. Ordinary runs have no added audit deadline; cancellation during finalization
starts that grace budget. Execution teardown has its own process-control limits. An exhausted integrity
audit is validation failure and orchestration ERROR, not accepted cancellation evidence. Missing identity
or discovery likewise remains ERROR. Cancellation is checked during success publication,
which revokes proof and rewrites the report when interrupted. A bound ledger is retained, while cancellation
before binding cannot claim known unit counts. Fixture observations record before, observed-after and
restored source/index/status/head/tree digests; drift restoration happens only after refusal is verified.
Cancellation is honored up to the final publication check; a signal after that completion point cannot
retroactively invalidate an already completed invocation. Real installed cancellation reports are also
parsed by the shipped report validator in both state modes. Sourcing the Bash script exposes diagnostic
helpers only; functional certification still requires the identity-bound `gate` invocation.
