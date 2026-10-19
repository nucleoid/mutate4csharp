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

After the owned builds, use the metadata-only script:

```text
python scripts/create-graph-deployment.py --host <absolute-dotnet-host> \
  --logger-dir <complete-logger-output> --worker-dir <complete-worker-output> \
  --task <sdk401/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll> \
  --source-revision <actual-commit> --output <fresh-absolute-deployment.json>
```

The script prints the complete manifest SHA256; it does not issue a capability or
execute any payload. Set `DOTNET_HOST_PATH` to the same absolute host and set
`MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST` and
`MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256` to that exact path/digest. These two
manifest values can be passed through standard VSTest runsettings for the genuine
fixture. The main test build carries the same `SourceRevisionId`. Run the full
ordinary SDK103 suite with these values; its graph fixture creates an isolated
SDK401 consumer root and exercises actual capture and public dispatch. A host
without SDK401/deployment is not a supported full-suite validation environment.

For installation, package this exact deployment with the tool using the explicit
GraphDeploymentDirectory pack input. Its default installed location is
`graph/sdk401/deployment.json`. Absolute selected image paths in an externally
prepared manifest must be regenerated for the actual installation; copying a
manifest to another location does not transfer file or source authority.
Installed packaging and both platform runs remain required acceptance work.

All native calls reuse the existing ProcessTree owner. The enclosing original
deadline shrinks across capture and worker; no new per-worker interval, parallel
runner, compiler server or retry is introduced. Existing opaque synchronous API
and native cleanup qualifications remain. Resource admission still requires the
existing 2 CPU / 4 GiB / 20 minute limits. The metadata manifest, helper source
stamps and unit tests cannot certify native execution, package currentness,
quality or production capability. New source32 regressions require genuine
positive-first RED/GREEN runs after distinct source review.

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
