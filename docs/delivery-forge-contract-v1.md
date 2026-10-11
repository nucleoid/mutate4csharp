# Mutate4CSharp producer mapping for Delivery Forge v1

This document freezes the producer boundary for Delivery Forge #7. That issue owns its adapter,
receipt schema, reviewed policy, baselines and exemption registry. This repository supplies real
installed-tool capability evidence and never approves a baseline or exemption for the consumer.

The accepted producer uses evaluation report schema `2`, sidecar envelope `2`, and local candidate
gate receipt `mutate4csharp-agent-gate-v4`. A preview receipt/report cannot authorize a functional gate.
The installed executable must identify the complete source commit in `--version`; the consumer
independently pins its package SHA-256, complete installed-payload SHA-256 and source commit.

An append-only consumer receipt must record these fields from the same invocation:

| Field | Required binding |
| --- | --- |
| capability | Tool/version, report/receipt schema versions, framework, runner, collector, host/process-control profile and limitations |
| implementation | Full approved task-start commit, current HEAD, HEAD tree, captured snapshot and source-state digest |
| invocation | Exact executable identity and argv, selection/base, configured scope, configuration bytes/hash and resource limits |
| baseline | Exact suite identities, expected/accounted members, baseline status, coverage identities and collection provenance |
| result | Process exit, outcome, every reason/incomplete condition, complete unit IDs/dispositions and reconciled counts |
| receipt | Consumer-owned gate/policy IDs, invocation timestamps, exit state and append-only receipt identity |
| artifacts | Immutable report bytes, SHA-256/length, package/payload hashes and report-bound discovery/proven hashes where applicable |
| source-state | Source bytes, Git index, status and tree before/after; only documented default-state sidecars may differ |
| provenance | Reviewed source/head, CI links, package/install identity, fixture marker and parent-owned acceptance reference |

Normalize only after validating those bindings. Complete nonzero all-killed evidence maps to PASS;
survivors or verified uncovered work maps to FAIL; valid inconclusive tool evidence maps to INCOMPLETE;
known zero effective work maps to NOT_APPLICABLE under the recorded policy. Policy-authorized N/A
exit zero retains NOT_APPLICABLE. Exit zero alone never means PASS.

Missing, stale, forged, truncated or malformed artifacts; report/exit disagreement; changed source,
consumer HEAD, package or payload; unsupported producer format; or adapter/publication failure maps
to ERROR. A previous report or proven record cannot rescue a failed current invocation. Proven-state
inspection is not cache reuse: a consumer must bind the current successful invocation and current
PASS report hash. The gate rejects an internal proof-eligibility contradiction.

All certification fixtures are small repository-owned examples, not production acceptance or approved
quality baselines. The supported Bash orchestration profile is Linux with GNU filesystem utilities,
isolated Python imports and the committed SDK selection. Windows build/real-process tests do not
certify the Linux Bash adapter on Windows. Consult the supported matrix for collector, Unicode,
coverage-projection and process-control limits. No public package feed publication is part of this mapping.

The Linux package job retains `mutate4csharp-installed-certification-v1` observations and SHA-256
sidefiles for each installed fixture invocation. These sanitized observations carry the source/tool,
package/payload, consumer base/head/tree, argv, source inventory, policy, suite accounting, unit ledger,
outcome/reason codes and original report hash/length. Their `fixture: true` marker is mandatory:
they are test evidence, not Forge receipts or approved production quality baselines. Original private
process logs are not published. A Forge adapter still validates the complete original report and current
invocation, then creates its own append-only receipt under Delivery Forge #7's policy.

This field mapping follows [Delivery Forge's pinned quality-gate contract](https://github.com/nucleoid/delivery-forge/blob/6304c90f83df3cdb918dff03f6358dcf4a939d16/docs/quality-gates.md).
The fixture's `sourceInventoryDigest` hashes UTF-8 newline-joined, sorted relative-file/hash entries;
index/status digests hash the captured Git output. These observation digests are explicitly separate
from the Bash guard's framed source-state digest. Safe hash bindings retain captured input, dependency,
plan and evaluation identities without publishing private diagnostics. The consumer still owns gate and
policy IDs, timestamps, registry identity and its append-only receipt; fixture IDs grant no policy authority.
