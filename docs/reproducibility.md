# Reproducibility and mutation identity

Mutation evaluation separates durable mutation identity from exact-run applicability.

## Stable identities

`MutationId` uses `mutation:v1:<sha256>` and length-frames each UTF-8 component before hashing:

1. canonical slash-relative, case-preserving repository path;
2. namespace, nested type/generic arity, and member signature identity;
3. declaration-local structural syntax path, including sibling ordinals for repeated sites;
4. versioned operator identity; and
5. exact replacement text.

Absolute offsets and whole-file hashes are deliberately excluded. Moving a file intentionally changes its IDs,
as does changing a containing declaration or the structural path within that declaration. Inserting an unrelated
earlier declaration does not. Repeated identical expressions remain separate because their declaration-local
paths differ. Constructor kind, checked operator/conversion form, and extension receiver are declaration identity
inputs. Type attributes, primary-constructor/base arguments, parameter defaults, and enum members anchor to their
nearest stable declaration rather than a file-wide ordinal. Operators, conversions, indexers, and indexer
accessors include any explicit-interface owner. Properties, indexers, custom events, and delegates are independent
member anchors, including sites in their attributes and parameter defaults. Enumeration refuses equal identity
material reported from different source spans within one project/target-framework/parse context. The same durable
mutation may legitimately bind different spans in distinct contexts; its context-bound evaluation-unit IDs remain
unique and an exact-ID rerun selects every matching context deterministically.

`EvaluationUnitId` uses its own hash domain and separately binds a mutation to its canonical project path, target
framework, and parse context. A report may therefore contain one mutation ID more than once, but every
evaluation-unit ID remains unique.
The evaluation fingerprint binds exact captured source/test/project/configuration/dependency bytes, scope, tool,
SDK/runtime/runner, operator contract, and recorded policy. A stable `MutationId` is therefore not evidence that an
old result applies to changed bytes or context.

## Deterministic planning and accounting

Candidates are ordered with ordinal comparisons over path, declaration, structural site, operator/version,
replacement, mutation ID, and evaluation-unit ID. This order is independent of current culture, worker count,
completion order, timestamps, and temporary directories. Reports must be emitted in the planned order.

The default policy is recorded in every report:

- workers: `1`;
- unique evaluation-unit cap: `100`;
- baseline timeout: `600` seconds;
- per-mutant suite timeout: `120` seconds;
- overall deadline: `1800` seconds; and
- stability repetitions: `1`.

Overrides must be positive whole finite values and are part of the evaluation fingerprint. Timeouts are fixed
policy limits, not values derived from a measured baseline and not runtime guarantees.

The validated recorded policy supplies the mutation cap; callers cannot substitute a different budget. The cap
selects a canonical prefix. Every remaining unit is explicitly `OMITTED`; omissions force
`INCOMPLETE`. MVP has no sampled profile that can claim full-scope `PASS`. Final accounting remains:

```text
selected = executed + freshUncovered + omitted
compileInvalid <= executed
```

Compile-invalid units are executed units, not an additional accounting bucket. Future validated-cache units need
a separate ledger bucket before caching can participate in this equation.

## Targeted reruns and unstable tests

An exact-ID targeted rerun replans the fresh capture, rejects missing/stale IDs, and requires the fingerprint of a
fresh plan constructed from live evaluation-fingerprint material and canonical candidate material. The targeted
gate recomputes that live material; caller-supplied strings or a previously bound plan cannot establish freshness.
The plan stamps every pending and omitted ledger unit with a deterministic selection-plan fingerprint and diagnostic
state. Stability reduction preserves both fields, including across repeated attempts. Final ordering validates the
fingerprint, diagnostic state, and mutation ID against the planned evaluation unit rather than replacing provenance
from whichever plan happens to finalize the ledger. It also supplies the intrinsic `TARGETED_DIAGNOSTIC` incomplete
condition. A targeted ledger therefore cannot be finalized by a full plan, and a targeted run remains diagnostic
partial evidence even if every available mutation is requested and killed; it cannot advance full-scope success or
proven state.

One attempt is the default. Optional stability repetitions are explicitly enforced by the recorded policy and are
reduced through the issuing selection plan. Each selected evaluation unit can be reduced only once per plan, and
only the exact completion digest issued by that reduction can finalize or publish proven state. Reduction is
thread-safe and fail-closed: a failed reduction attempt consumes that unit's one reduction opportunity. The
`StabilityAttempt` values supplied to this internal boundary remain trusted executor input; executor provenance is
outside the current orchestration contract. Caller-completed or foreign-policy results cannot finalize or publish
proven state. Repetitions are bounded to 100. Evidence retains bounded attempt, disposition, and original-kind
metadata; if all attempt entries
would exceed the 200-item unit limit, a deterministic first/last sample and omitted-count summary are retained. Incompatible
identical-input dispositions produce `UNSTABLE`, which is inconclusive and forces `INCOMPLETE`; a survivor is not
rewritten into a kill. Compatible repetitions report their observed disposition. Any finite number of compatible
runs demonstrates only those observations—it does not prove that a test is free of flakes.

## Linux process boundaries

Strict Linux executions use an isolated session and clean up the launch-owned process boundary after success,
timeout, cancellation, or failure. Legacy non-strict executions retain their historical successful-exit behavior:
detached descendants are not reaped after a normal exit, while timeout, cancellation, and failure still trigger
PID-and-start-time-bound descendant cleanup. Process-group/session discovery is strict-only and requires a live,
owned identity to pin the isolated boundary before any group members are admitted.

## Deterministic and nondeterministic surfaces

The tool makes mutation IDs, evaluation-unit IDs, planning, budget selection, ledger/report order, policy values,
and exact-input applicability checks deterministic. It does not make arbitrary test behavior, process scheduling,
machine load, or wall-clock duration deterministic. Ordinary project and test execution remains trusted code; this
contract does not add a hostile-code sandbox.

Exact-ID reruns bind to the current fresh canonical coverage identity as well as captured bytes.
They collect the configured baselines before validating the supplied fingerprint. A suite with unstable
coverage can therefore invalidate a rerun fingerprint even when source is unchanged; the tool refuses
that stale request rather than applying an old plan. Raw XML GUIDs, timestamps, and temporary paths
are excluded from this identity. A rerun remains diagnostic and never reusable proof.
