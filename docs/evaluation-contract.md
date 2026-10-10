# Strict evaluation contract (report schema v1)

Schema v1 permits additive enum values and optional properties when they preserve the existing outcome and
accounting rules. `diagnosticPartial` is optional at report and unit level, with absence meaning `false`; the writer
omits false values so ordinary new reports remain valid against the pre-diagnostic v1 schema and archived reports
remain valid against the current schema. Diagnostic reports explicitly emit `true` and require an updated v1 schema.
Consumers that validate against a frozen older copy may reject additive values or diagnostic reports and should
update their schema before accepting them. `UNSTABLE` is an additive unit disposition: it is always inconclusive,
forces `INCOMPLETE`, and is included in `counts.errors` (along with `ERROR`).

The additive `check` command is the machine-gating interface:

```text
/external/pinned-tool/mutate4csharp check --base <task-start-commit>
/external/pinned-tool/mutate4csharp check --input src/A.cs --input src/B.cs --report /external/report.json
/external/pinned-tool/mutate4csharp check --plan --base <task-start-commit>
```

Exactly one selection mode is required: one `--base` or one or more `--input` values. Strict mode rejects legacy footer, line-filter, manual-update, and reused-coverage flags. Legacy single-file mode remains available and non-gating.

## Outcomes and exits

- `PASS` / `0`: complete evaluation and no policy failure.
- `FAIL` / `3`: complete evaluation with a survivor or known-uncovered required site.
- `INCOMPLETE` / `2`: red or empty baseline. Baseline facts are retained; survivor claims are not reusable.
- `INCOMPLETE` / `4`: unknown enumeration, omitted work, execution error, unavailable snapshot, cancellation, deadline, or other inconclusive evidence.
- `NOT_APPLICABLE` / `5`: no effective valid candidates. A future validated `allowNotApplicable` configuration may map this outcome to exit `0`; the report outcome remains `NOT_APPLICABLE`.
- Usage errors remain exit `1` and cannot produce an evaluation outcome.

`INCOMPLETE` takes precedence over `FAIL`, while every known policy failure remains in `reasons`. Unknown enumeration is represented by `counts.enumerated: null`; it is never interpreted as zero sites. Run-level failures such as unavailable capture, deadline exhaustion, cancellation, and unmapped suites are recorded in `incompleteConditions`; any such condition forces `INCOMPLETE` even when the baseline, ledger, and counts are otherwise complete. Exact-ID reruns additionally carry schema-visible `diagnosticPartial` state on their units and report plus the intrinsic `TARGETED_DIAGNOSTIC` incomplete condition. Report validation and proven-state publication consume that structural state rather than evidence prose.

## Accounting and evidence

Every enumerated evaluation unit appears exactly once in the final ledger, keyed by `EvaluationUnitId`. The same
`MutationId` may appear in distinct project, target-framework, or parse contexts, including contexts where the
same structural mutation has a different source span. Collision refusal remains strict within a single context,
and final ordering verifies that each result's mutation ID matches its planned evaluation unit. Version 1 enforces:

```text
selected = executed + freshUncovered + omitted
compileInvalid <= executed
```

`COMPILE_INVALID` requires an attributable C# diagnostic plus a healthy unmutated control using the same captured input and environment. Compiler-looking text, restore failures, and generic failed-build banners are inconclusive. Diagnostics are sanitized and bounded to 20 entries of 512 characters. TRX evidence similarly bounds failed-test identities and diagnostics; a run/host error cannot be clean kill evidence even when failed tests are present. Repeated identical-input attempts with incompatible dispositions are `UNSTABLE` and force `INCOMPLETE`; stability evidence retains a deterministic first/last sample plus an omitted-count summary when the 200-item per-unit cap would otherwise be exceeded.

Every unit and every report outcome carries evidence, including synthetic evidence for zero-site, unavailable-input, and other outcomes where no mutant process ran. Duplicate or oversized IDs, pending entries, empty unit evidence, unreconciled counts, and an outcome inconsistent with the baseline/ledger/incomplete conditions are report-contract errors. Plan reports cannot claim `PASS`.

## Report durability

The versioned schema is [`contracts/evaluation-report-v1.schema.json`](contracts/evaluation-report-v1.schema.json). A strict run writes a unique report under `.mutate4csharp/reports/` by default. `--report PATH` selects an explicit destination.

Writes use an OS-released exclusive lock in a hidden `.<report-name>.lock` file beside the report, a same-directory `CreateNew` report file, a flush of the report bytes, and atomic replacement. A crash may leave an inert lock file, but not an active lock, so later runs recover without manual cleanup. Lock files persist beside reports; do not delete them while writers are active, because unlinking an active lock can split coordination across different file handles. Overwrite eligibility comes from the regular, bounded v1 report envelope (including mode and run ID), never from lock-file existence. The report directory must be writable and trusted: its existing permissions control access to both report and lock, avoiding a shared multi-user temporary lock root. This is cooperating-writer coordination, not a sandbox against actors able to modify the destination directory. Destination eligibility is checked while the lock is held and rechecked before replacement. A failed or concurrent write returns nonzero and does not authorize consumers to trust an older report at that path. Explicit destinations must use `.json`, cannot alias an input, and cannot overwrite an unrelated file, non-regular file, or symbolic link. Consumers must correlate both `schemaVersion` and `runId`; every successful strict report write prints the same run ID, and refused/failed report writes print the current attempted run ID so a stale explicit report cannot be mistaken for the current invocation. The flush narrows but does not eliminate power-loss durability risk.

Human status is written to the console; report JSON is written only to the report file. `check --plan` uses `mode: "plan"` and never represents reusable success.

Strict discovery and future proven-evaluation provenance are stored separately from reports and
legacy source footers. See [sidecar-state.md](sidecar-state.md). MVP evaluation never reads proven
state as an execution cache; `--no-state` disables sidecar publication without skipping evaluation.

Every report also contains `scopePlan`, a separately versioned (`schemaVersion: "1"`) deterministic
ledger of changed files, selected and removed declarations, transparent exclusions, configured
project/test units, conservative expansion, and incomplete reasons. See
[change-scope.md](change-scope.md). The coordinator consumes this plan before publishing the report;
plan mode runs no restore, build, coverage, or tests.

Stable mutation/evaluation-unit identity, canonical ordering, deterministic budget selection, exact-ID diagnostic
reruns, fixed execution-policy defaults, and stability repetitions are specified in
[`reproducibility.md`](reproducibility.md). Budget or target omissions cannot produce full-scope success.

## Snapshot status

The strict report contract records bounded immutable working-tree capture in `INPUT_SNAPSHOT` evidence; see [snapshot-inputs.md](snapshot-inputs.md). Capture refusal or drift writes a current-run `INCOMPLETE` report when the report destination itself remains writable. Supported Git-backed configuration-v1 contexts enumerate a deterministic bound plan, run fresh baselines and coverage, and execute selected units in isolated clones. Reports retain `FINALIZATION_PENDING` until issue #4 adds the final completeness and original-tree gate, so public strict invocations cannot manufacture `PASS` from partial evidence.

An execution-time package-root, restore-source, lock-file, worker-root, or private package-cache escape is reported
as `EXECUTION_BOUNDARY_INTEGRITY`, not as an unsupported capture or ordinary mutant error. It aborts the run and
preserves cleanup failure evidence because continuing after a proven frozen-boundary violation would make later
unit evidence untrustworthy. Ordinary feed, restore, SDK, host, workload, and per-mutant timeout failures remain
nonpassing execution evidence rather than being promoted to boundary violations.
When cleanup also fails, the report retains both `SNAPSHOT_CLEANUP_FAILED` and the typed primary divergence,
boundary, limit, or strict-execution-refusal condition with separate evidence. Exact-ID reruns and partial
stability markers share the report's bounded 20-diagnostic evidence contract and summarize any additional
identities or attempts with an explicit truncation count. Generated reason and incomplete-condition messages
are bounded to the report schema's 1,024-character limit, including dependency-preparation failures. Later
validation cancellation or native/runtime failure retains any integrity condition already observed.
