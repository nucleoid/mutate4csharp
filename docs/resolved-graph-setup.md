# Resolved graph setup and acceptance

Configuration v2 activates the real capture and selected-compiler worker. Version
1 keeps its existing strict self-contained implementation. Missing deployment,
unsupported compiler effects, stale bytes and incomplete native closure fail
closed; no configuration document or manifest is an accepted graph result.

The official tool/default suite still builds with the unchanged global.json SDK
10.0.103. Graph helper compilation and the genuine inherited compiler fixture use
the separately supported **10.0.401** profile. Install both SDKs under the same
explicit dotnet host; do not rewrite the repository global.json or substitute
103 compiler images for 401. A supported full-suite environment must prepare this
deployment before running the entire suite, including the genuine fixture.
There is no silent skip or filtered-full-suite alternative.

The existing preparation entry point supports this explicit profile:
`scripts/agent-gate.sh prepare SOURCE WORKSPACE RECEIPT graph-sdk401`.
Its ordinary three-argument preparation keeps its previous behavior. The graph
profile makes a second complete clone at the same actual revision, preserves its
original SDK103 global.json as DATA and changes only that owned clone to the
fixed SDK401 profile. It builds both helpers, publishes the bounded deployment,
then packs the ordinary SDK103 source with that exact deployment. The producer's
resource owner must cover the entire preparation and all child processes; this
option does not issue an execution capability or establish resource closure.

Use the actual clean source revision for all builds (`SourceRevisionId` must be
its full 40 lowercase hex characters). Under the existing admitted process owner,
build GraphCapture and GraphWorker from this same source with SDK401, passing
`-p:GraphSelectedSdkVersion=10.0.401 -p:SourceRevisionId=<actual-commit>` and
`-m:1 -nr:false -p:UseSharedCompilation=false`. Select SDK401 through a separate
owned build profile/global.json and explicit project paths; preserve the official
103 profile as input DATA and separately compile/test it. Build helper outputs in
distinct `logger` and `worker` directories. Each helper project itself checks the
actual selected SDK before CoreCompile. Normal restore/build barriers apply;
never copy missing transitive dependencies by hand or adopt earlier outputs.

The ordinary SDK103 tool/test build also requires that same explicit
`-p:SourceRevisionId=<actual-commit>` for deployment-byte validation. A plain
`dotnet test` which rebuilds without it is not this supported full-suite route:
the deployment tests report `GRAPH_TEST_SOURCE_REVISION_REQUIRED` before their
image-drift controls. Obtain the actual revision from the clean source checkout;
do not use a placeholder or add an implicit Git command to MSBuild evaluation.

After the owned builds, use the metadata-only script:

```text
python scripts/create-graph-deployment.py --external-manifest --host <absolute-dotnet-host> \
  --logger-dir <complete-logger-output> --worker-dir <complete-worker-output> \
  --task <sdk401/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll> \
  --source-revision <actual-commit> --output <fresh-absolute-deployment.json>
```

For a package, `deployment.json`, `logger/` and `worker/` must share one parent.
The script refuses other layouts unless `--external-manifest` is explicitly
selected. Package creation requires Python3 (override `GraphDeploymentPython`
only to select its actual executable). Before packing, the same bounded script
checks the manifest's exact `SourceRevisionId`, complete relative helper roles
and actual packaged helper bytes. It never accepts an external-selection
manifest as a package manifest.

The helper projects deliberately have no NuGet `PackageReference`; they use only
the exact SDK401 reference/compiler files and installed framework packs. Their
isolated SDK restore creates assets without a synthetic package lock, so the
preparation passes `RestoreLockedMode=false` for these two helpers only. A build
guard refuses any added PackageReference until that profile is reviewed and
locked restoration is supplied. Ancestor package imports remain disabled. The
ordinary tool and consumer package restores still use their genuine lockfiles.

The script prints the complete manifest SHA256; it does not issue a capability or
execute any payload. Set `DOTNET_HOST_PATH` to the same absolute host and set
`MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST` and
`MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256` to that exact path/digest. These two
manifest values can be passed through standard VSTest runsettings for the genuine
fixture. The main test build carries the same `SourceRevisionId`. Run the full
ordinary SDK103 suite with these values; its graph fixture creates an isolated
SDK401 consumer root and exercises actual capture and public dispatch. A host
without SDK401/deployment is not a supported full-suite validation environment.

For installation, package this exact deployment with the explicit
GraphDeploymentDirectory pack input. Its default installed location is
`graph/sdk401/deployment.json`. Logger and worker roles relocate with the tool.
The dotnet host, Csc task/definition and complete compiler closure remain exact
absolute path/length/SHA256 pins. This is intentionally an installation profile:
moving or updating the SDK/host requires a fresh selection; there is no runtime
SDK discovery, new-SDK fallback or automatic timestamp/digest renewal.

For standalone selection of an already installed package against a different admitted host path,
keep that package unchanged. Under the existing resource owner, observe the
actual installed helper directories and exact SDK401 task, then use the shipped
script to create a **fresh external** selection:

```text
python3 -I <installed-package>/scripts/create-graph-deployment.py --external-manifest \
  --host <actual-absolute-dotnet-host> \
  --logger-dir <installed-package>/tools/net10.0/any/graph/sdk401/logger \
  --worker-dir <installed-package>/tools/net10.0/any/graph/sdk401/worker \
  --task <same-host-root>/sdk/10.0.401/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll \
  --source-revision <actual-installed-tool-commit> --output <fresh-absolute-deployment.json>
```

Use the actual NuGet tool-store package root, not its command shim directory.
Check the printed SHA256 against the complete resulting file, then select its
exact path/digest through the two explicit manifest environment variables above
and set DOTNET_HOST_PATH to the same host. These values are installation-owned
inputs, never untrusted caller overrides. The generator refuses a helper/SDK
Roslyn byte mismatch. In that case rebuild the same official source under the
admitted exact401 profile and install its newly validated package; do not bless
another SDK or alter the copied pins. The runtime still checks source/SDK stamps,
all complete bytes and original ownership at every use. A missing deployment or
explicit host yields GRAPH_DEPLOYMENT_UNAVAILABLE; mismatched identity/bytes or
host selection yields GRAPH_DEPLOYMENT_STALE, both INCOMPLETE.

When the default packaged manifest exists, the runtime also binds every selected
logger/worker row to its fixed `graph/sdk401/{logger,worker}` installation role
and the packaged length/SHA256 tuple, then rereads the actual images. An external
selection may rebind only the host and SDK pins; copied or changed helpers are
refused even when their source/SDK metadata stamps are correct. Source builds
without a packaged manifest retain the explicit trusted source-CI deployment
route above. A caller's manifest digest or helper stamp does not issue code or
execution authority.

**Forge servicing recovery:** Forge installed profiles refuse both manifest
override environment keys by presence, including empty values. The standalone
external-selection example is therefore unavailable under Forge. After host or
SDK servicing changes any pinned path/bytes, retain the exact
GRAPH_DEPLOYMENT_UNAVAILABLE/GRAPH_DEPLOYMENT_STALE failure and perform a normal
fresh `prepare SOURCE WORKSPACE RECEIPT graph-sdk401` in new external locations.
Install that newly validated package and rebind/requalify the actual Forge
profile and its package/host/SDK pins through the existing approval contract.
Re-preparation is the only supported Forge recovery path. Do not edit an old
receipt, renew timestamps/digests, add an override, silently select a new SDK, or
automatically rerun a consumed plan. Successful re-preparation and fresh behavior
must be observed; documentation and a source-stamp match are not recovery proof.

CI's separate Linux `installed-graph` lane uses existing `prepare ... graph-sdk401`
and the existing installed-tool test helpers. Its one installed-v2 method must
observe real enumeration/execution and the deliberate survivor from a weak suite
using both packaged and freshly regenerated selection, plus a precise wrong-host
refusal and a foreign correctly stamped helper refusal. Reports and logs are uploaded even on failure. To run that method locally,
set MUTATE4CSHARP_INSTALLED_GRAPH_FEED and MUTATE4CSHARP_INSTALLED_GRAPH_HOST to
the actual prepared feed/host; optionally choose an external certification
directory. The complete test environment needs this profile as well as the
earlier source graph deployment. Source changes alone are not a passed run.
Relocation to a genuinely different installed host path, Windows installation,
both platform runs and the complete Forge graph remain acceptance work until
their actual results are retained.

The installed lane keeps its existing **20-minute** job limit and both positive
comparisons. Its 1100-second test ceiling and each 480-second check ceiling are
upper limits, not proof that SDK setup, SDK103 build, graph preparation,
installation, both positives and refusals fit together. Exact current-source
hosted step durations and terminal artifact/closure results are still required
before claiming lane fit. No timeout increase or omitted comparison is approved;
any later per-check reduction must follow those actual measurements. A job
cancellation does not become a passed installed profile or a replacement receipt.

All native calls reuse the existing ProcessTree owner. The enclosing original
deadline shrinks across capture and worker; no new per-worker interval, parallel
runner, compiler server or retry is introduced. Existing opaque synchronous API
and native cleanup qualifications remain. Resource admission still requires the
existing 2 CPU / 4 GiB / 20 minute limits. The metadata manifest, helper source
stamps and unit tests cannot certify native execution, package currentness,
quality or production capability. New source32 regressions require genuine
positive-first RED/GREEN runs after distinct source review.

The original graph interval reserves ten percent, capped at 60 seconds, for first
cleanup inside its unchanged whole deadline. Parent, logger and worker retain the
same absolute work/whole endpoints and earliest terminal cleanup. Work expiry
refuses new input/process work while the remaining original cleanup can release
known owners. Expired whole deadlines, unknown settlement and failed first-close
remain refusals; there is no deadline extension or disposition retry.
Linux graph calls pass that same original through target and cold setsid-probe
admission. Existing process, output and reaping waits shrink to its work or
disposition endpoint; terminal cleanup cannot grant another process birth.
Unsettled graph calls retain their strict-wrapper status directory as evidence.
These cooperative checks retain the existing Linux containment limitations and
do not issue native ownership or certify a completed execution.

Explicit compiler source encodings are limited to UTF8. The same source profile is
checked at the original compiler callback and the semantic worker. Default BOM
detection still supports UTF8 and UTF16 sources that the snapshot writer can
preserve. Other explicit code pages can shift character offsets even when the
original text matches at the wrong token, so those contexts return INCOMPLETE.
UTF32 BOMs are refused at both source boundaries because their decoded offsets
cannot be safely written by the existing UTF8/UTF16 snapshot writer.
Source bytes must also satisfy that writer's throwing decoder: BOM-less and
UTF8-BOM payloads must be valid UTF8, and UTF16-BOM payloads must be valid UTF16.
Malformed sequences are refused before semantic enumeration; parent admission
uses the same strict decoder rather than replacement text or an assumed compiler
fallback. This does not assert a particular compiler's legacy fallback behavior.

The original logger rejects ordinary compiler sources outside the owned source
and package roots, and PE/PDB outputs outside the owned source root, before any
source/output read. The worker independently re-derives both output locations
from its actual parsed Csc options (including the default PDB path) and requires
them to match the complete compiled snapshot before reading those outputs. A
valid PE/PDB byte proof at a different location cannot substitute for that join.

The graph resource profile distinguishes controlled semantic buffers from I/O.
All parent graph readers share one cumulative512MiB semantic/buffer admission.
Complete hash-only deployment images use one64KiB buffer per input owner and are
reread before and after EVERY actual compiler context. The same original has a
finite8GiB controlled-I/O allowance; no image digest cache, skipped recheck or
renewed reader credit is used. Before each existing compiler/logger invocation,
128MiB is reserved for its complete controlled input/output I/O; the semantic
worker reserves640MiB. Unknown/failed/unused reservations are never refunded.
Exact restrictive grants bind the actual request/argv and source-bound return.
The child counters describe pre-delivery observations, not a self-closed receipt.

Each child has its own explicitly separate512MiB controlled semantic/buffer
credit. The parent512MiB is not claimed to cover child allocations. This tracks
controlled byte buffers, not every Roslyn/MSBuild/CLR/native allocation: actual
prebirth2CPU/4GiB-per-runtime admission, complete native closure, elapsed limits
and quantitative full graph fit remain mandatory. Larger graphs can exhaust
these finite limits and must return INCOMPLETE; none of the bounds establishes
that the complete Forge graph fits before its actual census and genuine run.
