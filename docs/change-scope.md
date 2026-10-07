# Change scope planning

Strict `check` has two additive selection modes:

```text
mutate4csharp check --base REV [--plan] [--report PATH]
mutate4csharp check --input PATH [--input PATH ...] [--plan] [--report PATH]
```

Exactly one mode is required. Legacy single-file mode remains available, deprecated as gating
evidence, and retains its existing footer and execution behavior. Strict mode rejects legacy line,
footer/manual-update, and reused-coverage switches.

## Git comparison

`--base` resolves `REV` once to an immutable commit and performs a **direct-base** comparison. It
does not calculate a merge base. Current content always comes from the immutable working-tree
capture defined in [snapshot-inputs.md](snapshot-inputs.md), so an unstaged edit wins over a staged
version. The index supplies bounded inventory/provenance only. Staged, unstaged, deleted, and
eligible untracked paths are deduplicated. An exact-content one-to-one C# move is displayed as a
rename; edited or ambiguous moves remain explicit deletion and addition obligations.
Comparison hashes captured bytes through Git's path-aware clean/EOL normalization, using the same
bounded, sanitized Git runner as capture. Base blobs are read by the object IDs from the captured
tree inventory; caller `GIT_*` variables cannot redirect those reads.

Repeated `--input` paths provide non-Git operation. They must be regular C# files under one planning
root. Their bytes and `mutate4csharp.json` are rechecked before the plan is returned. Explicit input
mode uses the same link, special-file, and byte bounds as snapshot capture, parses configuration
from the captured bytes once, and does not infer a fake Git base or search above the chosen root.

## Configuration and ownership

Place `mutate4csharp.json` at the planning root:

```json
{
  "version": 1,
  "includeGenerated": false,
  "includeTests": false,
  "projects": [
    {
      "project": "src/MyLibrary/MyLibrary.csproj",
      "tests": ["tests/MyLibrary.Tests/MyLibrary.Tests.csproj"],
      "sources": ["src/MyLibrary/**/*.cs", "shared/contracts/*.cs"]
    }
  ]
}
```

Paths and optional `sources` globs are repository-relative and deterministically ordered. Without
`sources`, a production source is owned by a configured project whose directory contains it. With
`sources`, those patterns define ownership and can represent linked/shared compile inputs. Zero matches are `UNMAPPED_PROJECT`; overlapping
matches are `AMBIGUOUS_PROJECT_OWNERSHIP` and list every candidate instead of choosing one.
Missing captured project/test paths are also explicit incomplete reasons. Changed configured test
sources (including additions, deletions, and renames),
project files, shared props/targets, or the scope configuration expand the affected production
project rather than suggesting that no production behavior changed.
Every changed captured path is represented. Unsupported changed input types are visible exclusions
and make the plan incomplete; build/configuration inputs without a configured owner are
`UNMAPPED_PROJECT`, never a complete no-applicable result. Exact renames retain both the destination
and base-side removal ownership, including cross-project and production-to-test moves. An in-place
edit that turns production source into a generated or test exclusion likewise retains its base-side
removal obligation and expands the owning production project.

Files named `*.g.cs`, `*.generated.cs`, `*.designer.cs`, or carrying an auto-generated header are
excluded as `GENERATED_SOURCE` by default. Exact `*.Tests.cs` names and sources in `test`/`tests`
directories are excluded as `TEST_SOURCE`; a configured `.csproj` also classifies sources under its
non-root directory. Solution entries never act as source roots, and a repository-root test project
does not classify the entire repository as tests. Header
detection only inspects leading comment trivia, not string literals. The two configuration booleans
are explicit overrides; an admitted source remains visible with `overridden: true`. A path matching
both generated and test conventions receives one deterministic final exclusion/admission record.

## Declaration scope

The v1 plan records deterministic declaration IDs and complete line spans for methods,
constructors, destructors, operators, conversions, accessors, expression-bodied/indexed
properties, field/property/event initializers, and top-level statements. Namespace, containing
type/generic arity, member signature, and a stable duplicate ordinal disambiguate declarations.
Accessor and property scopes are not duplicated.
Declaration comparison preserves exact token text, including whitespace inside ordinary, verbatim,
character, and raw literals, while ignoring formatting-only whitespace between tokens. Comments and
directives remain conservative inputs. Published declaration IDs use bounded hash suffixes.

Added and changed current declarations are mutation candidates. Removed declarations are retained
as removal obligations and never turned into imaginary executable mutation sites. Partial types,
removed/shared signatures, type headers, using/parse-context edits, and changes outside supported
declarations conservatively expand to the configured production project. Parse errors and changes
with no supported declaration are visible and make the scope plan incomplete. Whitespace-only
changes do not invent executable scope.

The plan distinguishes changed declarations from `FULL_PROJECT` risk expansion. It does not claim
that unchanged callers or dependencies were proven correct. Roslyn parsing here is deterministic
syntax planning, not full MSBuild semantic fidelity; strict execution must verify project/TFM and
semantic assumptions before any future `PASS`.

## Plan mode and trust boundary

`check --plan` creates the same fresh scope plan, writes it inside the version-1 evaluation report,
prints a human summary, and runs no restore, build, coverage, or test process. It writes neither
production sources nor the Git index. A plan report is audit data, not reusable success, and always
remains `INCOMPLETE` while execution is absent.

Ordinary configured project/test execution is trusted code. Scope planning and snapshot safety do
not claim a hostile-build sandbox.

Scope arrays are capped at 100,000 items. Exceeding a capture or publication bound fails closed to
an incomplete unavailable scope rather than dropping paths or preventing report publication.
