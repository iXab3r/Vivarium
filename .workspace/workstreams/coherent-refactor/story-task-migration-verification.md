# Active task-ownership verification — 2026-11-01

Source HEAD: `1f0421357334be4330e7440b08b23fc12f56a598`, branch
`bugfix/teamcity-behavior-parity`. Lead: Story Delivery; co-held Reconciliation, REST, Docs and
Test Steward. This record verifies planning ownership and text preservation, not runtime behavior,
complete semantic variant discovery or independent milestone acceptance.

## Task creation, text and graph readback

- All 18 existing slices have 54 Open implementation/REST-contract/REST-acceptance tasks, #110–#163.
  Fifty-one tasks were created in this increment; the original first P0 packet was expanded.
  Every implementation is a native child of its primary actor-goal story, and every contract/
  acceptance child shares the implementation's milestone. The existing nine milestones remain.
- All 25 story pages have actual contribution links. The 61 primary/related rows preserve one
  shared subsystem contribution rather than creating an engine per story/surface. The packet
  register, subsystem owners/slices, API ledger and milestone/contribution rollups agree.
- All 54 actual GitHub bodies match the generated task text after declared newline normalization.
  Actual raw and normalized body hashes are retained in `task-packet-bodies.tsv`.
- All 1,497 source-block occurrences from 66 original bodies are present in their destinations.
  The per-source block ordinals are exhaustive. Exact duplicate prose is referenced within the
  same destination; code fences/headings retain their structure. This proves text routing, not
  that a historical status or source-derived expectation is currently correct.
- Sixty-two source cards have the legacy-source label and destination banner; their exact original
  bodies remain inside the current bodies. All eight retained milestone gates, 25 story pages and
  nine evidence-helper headers likewise retain their original bodies verbatim. No source text is
  truncated, and no issue/comment is closed/deleted. Titles, original milestone assignments and
  assignees remain unchanged; labels change only by the declared legacy-source addition.
- All twelve original comments retain exact bodies/IDs/URLs/authors/creation/edit timestamps.
  Their readable retained copy includes original body hashes and historical-execution caveats.
- The final native graph has 129 edges and 33 roots: the 25 goals plus eight milestone gates.
  All new packet links resolve. Nine retained evidence helpers intentionally change parents,
  with before/after records; all other prior edges remain. Reparenting used the documented
  `replace_parent` option of the [GitHub sub-issue API](https://docs.github.com/en/rest/issues/sub-issues).
  No native blocked-by dependency is added, and no issue-closure cycle is inferred from a use link.

## Ownership and unchanged case semantics

The same 16 logical subsystems, 18 slices, 22 early integration contributions, 449 documented API
headings, 456 CLI fixture candidates, 40 literal native HTTP registrations and 20 Agent alternatives
remain. Every heading keeps its source ID and logical primary subsystem; only task ownership moves.
Legacy-owner fields retain original provenance alongside explicit current owner fields. No owner
assignment, source-text routing or Open/label count proves delivered behavior or API completeness.

Both known-case tables preserve all fifteen case IDs, API/owner relations, prerequisites, asserted
effects, independent reference statuses and replay/product test bindings byte-for-byte as field
values. Only `contract_task` and `acceptance_task` change. Request/oracle fixture JSON, product and
harness code, raw receipts, captured bytes, original execution identity and generated `api-status.json`
are unchanged. Source/variant normalization and the global denominator remain UNKNOWN.

## Existing read-only accounting check

The private host still reports SDK 10.0.303. All 183 recorded producer/source bytes and all six
current accounting-source bytes match the immutable receipts. An explicit ignored assessment binds
unchanged historical raw/control evidence to the candidate and explains the task-pointer-only
change. No original receipt or tracked generated status is rewritten.

The existing executable was invoked with that assessment:

```powershell
& artifacts/p0-rest-first/tooling/dotnet/dotnet.exe tools/Vivarium.ApiLedger/bin/Debug/net10.0/Vivarium.ApiLedger.dll . story-task-transition-working-tree@1f0421357334be4330e7440b08b23fc12f56a598 artifacts/story-migration-stage3/accounting-assessment.json
```

Exit 0; PARTIAL_BASELINE_P0_OPEN; 449 discovery headings; fifteen known cases; five product RED;
ten product-prerequisite-blocked; zero GREEN/stale/accounting errors; 18 slices; 22 contributions.
Evidence mode is REUSED, with original raw/control artifacts and execution identity. The command
reads files/Git/results and starts no tests, HTTP probes, Agents or services.

That partial checker validates its supported ownership/case/task/contribution/result joins. New
story/full-text/readback/graph checks are bounded document checks performed separately; this does
not claim implementation of the missing full story/source/variant/later-gap checker. Its old
issue-number messages remain historical source references, resolved through the current index.
An exact postcommit transfer/readback is recorded separately under the ignored evidence directory;
working-tree success alone is not a new commit/main or runtime verdict.

## Local checks and retained evidence

JSON/TOML/TSV shapes, stable IDs, source/story/case/task references, packet ancestry/milestones,
source/body/comment hashes, original-body containment, graph reachability/cycles, local links,
metadata consistency and Git whitespace/diff checks pass. Native role-adapter metadata and all
product/harness sources are unchanged. The narrow case-table comparison verifies task-pointer-only
changes rather than weakening expected behavior to accommodate a new graph.

Raw before/after GitHub issue/comment/native-edge snapshots, generated task bodies, complete source-
block routing, applied body-update identities and accounting assessment/report are retained under
`artifacts/story-migration-stage3/`. That directory names this third planning checkpoint, not execution
of the user's Stage 3 cleanup. Tracked registers retain the useful lineage and hashes.

Root `dotnet build` / `dotnet test` and product/reference/runtime probes were not run: no code,
request/oracle or runtime binding changed. There is no new executable proof script, runtime GREEN,
independent final aggregate/Security PASS, debt clearance, main merge or milestone acceptance.
The 62 labelled original cards remain Open pending separate administrative cleanup and final
delta/lineage reconciliation. No complete downloaded attachment/timeline archive or permanent-
deletion clearance is claimed; original discussions and external links remain reachable on GitHub.
