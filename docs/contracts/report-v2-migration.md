# Functional report v2 and gate receipt v4

The functional gate requires evaluation report schema `2` and receipt format
`mutate4csharp-agent-gate-v4`. Reports from preview schema `1` and receipts from earlier formats
remain historical observations and cannot certify a functional gate. The original v1 schema is
retained for reading historical artifacts; the strict writer emits only v2.

Every conclusive suite now includes structured `accounting` with the run and snapshot identities,
the configured execution and full expected-member array, the full accounted-member array, exact raw
coverage digest/length, canonical coverage identity, path-map version and fresh-collection flag.
The configured suite execution identity is recomputed from that configuration. Run/snapshot identities
and every member must agree. These fields are authoritative; bounded free-form diagnostics are
descriptive and cannot replace or truncate accounting. Incomplete suites may omit this proof.

The outcome is authoritative only when schema, process exit, source/package/payload identities and
ledger agree. Policy-authorized N/A may return exit zero while retaining `NOT_APPLICABLE`; it is never
normalized to PASS. Valid red/empty/omitted/error/unstable evidence remains INCOMPLETE. Contradictory,
stale, missing or malformed orchestration remains ERROR. Rollback rejects the newer receipt/report
rather than interpreting it with preview semantics. Cache reuse remains disabled.
