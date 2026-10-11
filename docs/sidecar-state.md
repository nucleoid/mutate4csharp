# Sidecar evaluation state (schema v2)

Strict evaluation never treats comments in production source as proof. The legacy
`mutate4csharp-manifest` footer remains available only to the deprecated single-file workflow;
`check`, `check --plan`, and sidecar publication neither read it as evidence nor rewrite it.

## Owned layout

State is local and rooted beneath the validated repository root. Before publishing any state, the
tool requires a regular `.mutate4csharp/.gitignore` whose final active rule is `*`; it creates that
two-byte contract when absent and refuses conflicting or special files without replacing them.
Trailing spaces are ignored as Git ignores them, while tabs, other whitespace, and leading whitespace
remain part of a pattern
and therefore cannot satisfy the owned `*` rule:

```text
.mutate4csharp/
  reports/       default reports when the invoking current directory is the repository root
  discovery/     scope/input observations; never reusable PASS evidence
  proven/        validated, policy-complete PASS provenance
  locks/         persistent cooperating-writer lock inodes
```

Keys are lowercase SHA-256 names derived from bounded identities; user paths never become state
filenames. State directories and destinations must be ordinary paths, not links or reparse points.
Writers take an exclusive persistent lock, write a same-directory `CreateNew` temporary file, flush
it, recheck the destination, and atomically publish. Lock files are deliberately not deleted: doing
so while another process holds the old inode could split coordination. Existing conflicting bytes,
active writers, malformed destinations, partial records, unknown schemas, and records over 1 MiB
fail closed. Cleanup removes only a writer's temporary file and never unrelated files.

The default report root is the invoking process's current directory; every default report path ensures
the owned ignore contract before capture, including plan, `--no-state`, and capture-failure paths.
`--report` may safely place a report elsewhere. `--no-state` disables discovery/provenance publication
but does not skip evaluation or its report.
If default/requested state publication fails, the current report is finalized as `INCOMPLETE` with
`SIDECAR_WRITE_FAILED`; an older report or sidecar is not current evidence.
Proof eligibility is checked before any conclusive report publication. A fresh stateful run revokes an older
proof for the same evaluation fingerprint before publishing its report. Proof is published last. A publication
fault revokes proof again and replaces the current report and its owned discovery observation with incomplete
evidence. If report recovery cannot write safely, the tool invalidates only its own current-run report or exposes
the unresolved I/O failure; process/report disagreement is an orchestration error.
Report publication necessarily precedes discovery publication. A process or machine crash in that
window can leave a report without discovery; such a report is not
reusable proof. Consumers may treat only a separately validated proven record as reusable evidence.

## Discovery is not proof

[`contracts/discovery-state-v2.schema.json`](contracts/discovery-state-v2.schema.json) records the
frozen snapshot ID, exact evaluation fingerprint, scope completeness/exclusions, report digest, and
observed outcome. Manual scan/update operations and legacy manifests can at most inform discovery;
they cannot write a proven record. Current strict checks publish discovery after immutable capture
and never consult proven state to skip execution.

## Proven state

[`contracts/proven-state-v2.schema.json`](contracts/proven-state-v2.schema.json) is a future cache
input boundary, not an enabled cache. Publication is accepted only from an internally validated
`check` report that is `PASS`, has a green baseline, known and reconciled enumeration, complete
scope, no omissions/errors/fresh-uncovered required units, no incomplete conditions or reasons,
green suite evidence, complete policy, matching report bytes, and one fresh exact-input coverage
provenance entry for every report suite. Each entry must bind the report run ID, snapshot ID,
evaluation fingerprint, suite ID, positive coverage length, and green baseline. `FAIL`, `INCOMPLETE`,
`NOT_APPLICABLE`, plan reports, partial reruns, and synthetic or
suite-less reports cannot populate proven state.
The fingerprint material's snapshot ID must equal the report's sole captured snapshot ID, and its
scope bytes must equal the canonical serialization of the report's complete scope plan.

MVP deliberately performs **zero proven-state reads during evaluation**. Inspection validates a
record conservatively, but no result is reused until the later cache-soundness issue enables that
path.

## Fingerprints and coverage provenance

Evaluation fingerprints use `sha256-length-framed-v1`. Each field is UTF-8 and length-framed before
hashing. Inputs are sorted by ordinal `(kind, normalized relative path)` and bind exact length and
SHA-256 bytes for source, tests, assets, projects, configuration, and dependencies. The identity also
binds the complete scope plan, configuration contract, exact tool build, resolved SDK, runtime/OS/
architecture identity, operator contract, runner, dependencies, and every execution-policy value.
The SDK version is resolved with the frozen capture as the working directory before that capture is
disposed, so a later edit to the original repository's `global.json` cannot change the identity.
Resolution also refuses a `global.json` inherited from a parent of the private capture root. Execution
clone pinning still resolves the SDK from the live original tree; the later execution-wiring issue must
share this captured resolution before proven reuse can be enabled.
Discovery may record a runner or dependency identity as not yet prepared because it is never reusable;
the proven publication API rejects such placeholder material. Wall-clock time, generated timestamps, temporary
directories, report destinations, and worker scratch paths are intentionally excluded.

The evaluation fingerprint binds canonical coverage states and source spans using captured relative
paths. Temporary worker paths, collector-generated module identifiers and raw XML ordering do not
change that identity. Exact raw coverage-report hashes and lengths remain per-run provenance.

Coverage provenance separately binds the evaluation fingerprint, frozen snapshot, suite/context,
green fresh baseline, exact coverage-report bytes/length, path-map version, and runner identity.
Raw or reused coverage is not trusted to classify required sites as uncovered.

These records protect against stale, mismatched, partial, and accidentally conflicting local state.
They are not signatures and do not attest against an actor who can maliciously rewrite the trusted
repository state directory. Test/build execution remains trusted code, not a sandbox. SDK selection
and startup are part of that same trusted-build boundary; resolving from the capture does not sandbox
repository-selected SDK behavior. External services, native tools, and environment inputs that are
not captured and fingerprinted are
unsupported for proven reuse and must force fresh execution or an incomplete result.
