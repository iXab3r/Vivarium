# Usable-agent-server evidence

Status: Waves 0-5 gates and the first D30 per-Agent central-upgrade core are complete; deployment
installers/fleet orchestration and final release/freeze gates remain.

## Authoritative context inspected

- `AGENTS.md`
- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `docs/walkthrough.md`
- `docs/DEVELOPMENT.md`
- `docs/design/teamcity.md`
- `docs/design/agent-explorer.md`
- `docs/design/rest-api.md`
- `docs/design/ui.md`
- `docs/design/agent-api-sdk.md`
- `docs/design/platform.md`
- Current controller, Agent, bootstrap, contract, transitional panel, and test layout

## Current facts that shape the plan

- Durable Agent identity/status, FIFO scheduling, build ownership/cancellation, matrix submission,
  results/artifacts, and the management kernel already exist.
- Public REST/OpenAPI, typed capabilities/static facts, the first Agent desired mutation, immutable
  package resources, and specialized durable Agent-upgrade operations now exist. React/Workbench,
  enrollment bundles/installers, fleet rollout, complete identity/RBAC administration, dynamic
  inventory, and broader mutations do not.
- D30 now governs an authenticated post-authorization manifest, digest identity, durable maintenance
  drain/operation, exact health confirmation, and one-shot LKG recovery. Initial installer-byte trust
  and the final cross-platform failure gates remain open.
- Architecture D30 explicitly authorized the change-controlled bootstrap implementation before it was
  modified; it is not yet declared frozen.
- The UI design explicitly rejects implementing new screens in both Blazor and React.
- Bootstrap and Agent run under the same installation principal/data directory today, so the protected
  existing Agent credential is the simplest authenticated package-read credential; a second secret would
  add lifecycle state without a local isolation boundary.

## Planning validation

- Every worker stream in `phases.md` has a lower estimate of at least 8 hours.
- The wave order now matches the accepted Phase-1 sequence in `docs/ROADMAP.md`.
- The final milestone has explicit runnable evidence and a two-Agent correctness gate.
- Initial mutable release policy is intentionally absent, preserving Git as the only desired
  configuration authority while its release-policy schema remains unimplemented.
- The stamped ZIP path authenticates bytes before execution; the unsafe pipe-to-shell shape remains
  excluded.

## Specialist planning reviews

- UI Expert: confirmed the four-layer shell, canonical route grammar, dedicated Agents collection,
  immutable-ID Agent routes, Agent-local tabs, responsive pane behavior, and D25 refinement path.
- Agent API/SDK Expert: confirmed existing multi-Agent/session foundations; identified the missing durable
  upgrade operation/shared lease and digest-based activation; recommended reusing the existing protected
  Agent credential for package reads.
- Reconciliation Lead: found and corrected the conflict between the earlier deployment-first workstream
  and the authoritative roadmap; supplied disjoint Wave-1 ownership and integration gates.

## Wave-0 implementation evidence

- Migration v3 adds principal-scoped matrix idempotency and the append-only replacement guard while
  preserving the immutable v1/v2 definitions and checksums.
- Application command authorization now sits below ControlPlane/panel adapters; gRPC mutation denials
  are target-aware and correlated.
- Fresh enrollment, replacement enrollment, and rejected proofs are bounded, redacted, and audited;
  audit failure rolls back the associated mutation. Dedicated D28 reclaim proof remains pending.
- Blob PUT and artifact reads have request-level outcome/correlation audit, retry/no-change semantics,
  and digested invalid targets without logging raw route input.
- Combined focused Wave-0 gate: 42 passed; enrollment boundary: 10 passed.
- Final root build: 0 errors, with two `NU1900` warnings because vulnerability metadata was unreachable.
- Final root tests on macOS 26.0 / `osx-arm64` / .NET SDK 10.0.301: 182 passed, 9 Windows-only
  skips, 0 failed (191 total).
- Inventory was revalidated from `scope.toml`: 44 protocol declarations + 4 Blazor routes + 9 mapped
  host surfaces = 57 data rows; no inventory identity changed.

## Wave-1 implementation evidence

- Shared REST infrastructure supplies cookie/bearer authentication, correlated RFC 9457 Problem
  Details, bounded `limit`, tamper-evident principal/filter/sort-bound cursors with a 15-minute lifetime,
  stable JSON conventions, private conditional ETags, and authenticated `/api/v1/system` discovery.
- `/api/v1/agents` and `/api/v1/agents/{agentId}` expose stable Agent identity, all four runtime status
  axes, freshness, OS/version fields, separate reported/custom/effective parameters, and accurate
  child-build identity with a parent matrix link only when known.
- `/api/v1/audit-events` exposes bounded, filtered append-only audit history to the current legacy
  admin permission only; secret/session/enrollment credential values are not projected.
- `/api/v1/builds`, `/api/v1/builds/{matrixBuildId}`, and `/api/v1/queue` expose stable matrix/child
  identity, FIFO state, deadlines, cancellation, steps, results, artifact manifests, and immutable
  assigned-Agent provenance without blob bytes or storage paths.
- `/openapi/v1.json` is byte-identical across controller restart, publishes only `/api/v1` operations,
  and requires unique operation IDs and domain tags. The OpenAPI document is intentionally anonymous;
  management resources remain protected.
- Focused Wave-1 REST gate: 15 passed across core, Agent/audit, and build/queue suites. Adjacent Agent,
  audit, kernel, and REST regression evidence: 32 passed.
- Root build: 0 errors, with two offline NuGet vulnerability-feed `NU1900` warnings. Root tests:
  197 passed, 9 Windows-only skips, 0 failed (206 total).
- Current legacy limitations are explicit: `/system` is visible to admin/submit management principals,
  Agent/audit visibility is global-admin scoped, and neutral system-read/pool-scoped RBAC waits for D26.

## Wave-2 implementation evidence

- `AgentHub.Session` now has additive protocol-range negotiation, bounded capability IDs, explicit
  negotiated/legacy modes, authoritative credential/connection generations, typed static HostFacts,
  and lower-case package-digest evidence without changing existing field numbers.
- Protocol compatibility is fail-closed before enrollment-token claim. Current Agents negotiate
  build-runner and host-facts support; legacy Agents remain observable and may re-adopt existing work
  but are not reconciled for new assignments.
- Credential generations survive restart, replacement re-enrollment revokes the prior token before the
  replacement receives work, and durable connection generations fence independent reconnects. A failed
  replacement observation leaves the prior live session unauthorized rather than schedulable.
- Cross-platform collectors provide bounded Windows registry build+UBR, Linux `os-release`/`uname`, and
  macOS `sw_vers`/`uname` facts with distinct product/kernel/architecture fields, partial outcomes, and
  structured redacted issues. Native macOS smoke passed; the local run skips Linux-native smoke.
- Migration v4 preserves the v1-v3 bytes/checksums, backfills existing credentials to generation 1,
  and adds generation-fenced static observations plus independently replaceable capabilities.
- Agent list/detail filtering and `/api/v1/agents/{agentId}/facts` expose typed facts, quality/freshness,
  current capability support, strict digest validation, and distinct current-versus-observation
  generation provenance. Legacy Agents project explicit unknown facts.
- Focused protocol/platform/persistence/REST/lifecycle gate: 30 passed, 1 platform-conditional skip.
  Integrated migration/Agent REST/protocol gate: 38 passed.
- Final solution build: 0 errors, with two offline `NU1900` vulnerability-feed warnings. Final root
  tests: 220 passed, 10 platform-only skips, 0 failed (230 total). `git diff --check` passed.

## Wave-3 implementation evidence

- The controller creates or adopts a normal non-bare managed-local repository on `main`. The narrow
  D29 system-Git adapter uses isolated candidate index/object state, complete-tree validation,
  expected-old `update-ref` compare-and-swap, bounded process/output handling, and secret-free commit
  provenance. Human dirty state blocks writes; the expected/result marker permits only proven-safe
  checkout recovery.
- Canonical validation currently accepts only bounded first-version Agent documents at
  `.vivarium/agents/{id}.yaml`: `apiVersion: vivarium.io/v1alpha1`, `kind: Agent`, a lowercase stable
  ID, and explicit boolean `spec.enabled`. Unknown paths, noncanonical content, oversized trees/docs,
  and representative secret material fail closed.
- Migration v5 preserves the immutable v1-v4 ledger and adds configuration revision sets/members,
  materialization-scope active/last-known-good state, durable principal-scoped mutation operations,
  and the Agent desired projection. Append-only migration v6 adds affected-target metadata, exact
  conflict revision/diff evidence, and bounded repository-attempt failures without changing v1-v5.
  Reconciliation validates committed bytes, applies projection, pointers, and audit atomically,
  retains last-known-good on invalid/blocked heads, rejects unsupported Agent-document removal, and
  recovers pending committed work after restart.
- `/api/v1/agents/{id}/settings` GET/PUT is the first desired-state mutation. It exposes desired/applied
  enablement and revision/diagnostics, uses a strong reversible configuration ETag, requires
  `If-Match`, `Idempotency-Key`, and an explicit boolean, and distinguishes precondition, validation,
  conflict, authorization, not-found, and repository-unavailable outcomes. Exact replay returns the
  original semantic result without rolling back newer live state.
- Desired-state activation and scheduler admission now share the per-Agent lifecycle lease. A bounded
  hosted monitor acquires a stable ordered lease set, converges externally authored local Git heads,
  refreshes the live registry from the durable projection, and preserves LKG through invalid, removal,
  or repository-failure attempts. Exact service mutations never project unrelated external changes.
- Focused v6 Git/reconciliation/migration/desired-configuration gate: 49 passed. Combined repository,
  reconciliation, migration, desired-configuration, monitor, and scheduler hardening gate: 62 passed.
  The monitor scenarios passed 4/4 and the scheduler lifecycle-race suite passed 9/9.
- Final solution build for the slice completed with 0 errors; the only messages were two offline
  NuGet vulnerability-feed `NU1900` warnings. Final root tests after Wave-3 hardening and the independent
  Wave-4C TRX/CLI boundary: 269 passed, 10 platform-only skips, 0 failed (279 total).
  `git diff --check` passed.

## Wave-4 implementation evidence

- Migration v7 adds expiring principal/project-owned blob upload plans, immutable build payload and
  artifact references, Agent assignment/artifact staging authority, principal-scoped build mutation
  records, durable build runtime revisions, and retained build events. Cross-object hash knowledge no
  longer grants access: staging upload, Agent payload read/artifact write, and human download each prove
  their owning object and current fence.
- `/api/v1/blob-upload-plans`, staged blob PUT, `POST /api/v1/builds`, idempotent cancellation, and
  `/api/v1/events` provide the complete live build mutation/watch path. SSE resumes from
  `Last-Event-ID`, reports retention gaps explicitly, and treats GET build state as authoritative.
- `viv login`, `viv run`, and `viv cancel` now use the REST client. Run creates one staging plan,
  uploads only required archives, submits with one idempotency identity, and watches through resumable
  SSE; Ctrl+C still detaches locally without cancelling remote work. The gRPC ControlPlane remains
  frozen for compatibility and legacy list/authorize methods, not as the CLI build transport.
- Migration v8 adds build TRX projection state plus durable report, test-definition, and occurrence
  tables. The bounded parser retains adapter/schema and raw artifact provenance, records typed safe
  failures and `NO_REPORT`/`PARTIAL` states, never removes raw evidence, and catches up absent or
  interrupted projections sequentially on restart.
- A real-Kestrel two-Agent acceptance starts two independently enrolled/authorized Agent runners,
  submits two compatible matrix cells, observes both `RUNNING` concurrently on distinct immutable
  Agent IDs, and finishes both successfully. This proves controller/scheduler isolation for the
  two-Agent topology while retaining a one-physical-Agent dogfood target for the later deployment wave.
- Focused gates: management kernel 8/8; ControlPlane compatibility 8/8; CLI/REST 20/20; fencing 5/5;
  panel artifact boundary 4/4; build REST 4/4; migration ledger 22/22; TRX parser/persistence 9/9; and
  session loop including two-Agent concurrency 4/4.
- Final solution build succeeded with 0 errors; the only messages were two offline NuGet
  vulnerability-feed `NU1900` warnings. Full tests: 288 passed, 10 platform-only skips, 0 failed
  (298 total, 2m14s).

## Wave-5 implementation evidence

- Migration v9 adds the singleton administration state, durable setup operation, purpose-bound hashed
  token generations, setup sessions, and request replay records. Bootstrap rotation, single-use claim,
  restart persistence, local access reissue, and pre-commit abandon are covered over real HTTPS.
- The managed-local schema now accepts canonical User and direct built-in RoleBinding documents. One
  atomic multi-document Git mutation prevents the first User and `SYSTEM_ADMIN` binding from tearing.
  Complete-tree validation rejects dangling users, duplicate case-insensitive logins, illegal role
  scopes, noncanonical content, and secret-bearing fields.
- Migration v10 materializes Users and RoleBindings with the exact active revision-set provenance. The
  product permission catalog and five built-in TeamCity-style floors are code-owned; evaluator tests
  prove project and fleet scopes do not cross and Agent Manager lacks high-risk command authority.
- Setup completion records the candidate commit, reconciles that exact revision, then atomically copies
  only the salted password verifier into v11, activates the instance, and revokes setup access. Named
  panel cookies carry a credential generation checked on each application authorization decision.
- Recovery is host-explicit and purpose-separated. The recovery value is accepted only by its claim
  exchange; the resulting bounded `Vivarium-Recovery` Superuser session authenticates normal REST,
  survives restart, and loses access immediately on local revoke. Neither value appears in SQLite,
  audit details, setup responses, or normal authentication schemes.
- Legacy admin/submit credentials keep their previous evaluator scopes and are labeled migration
  adapters; submit receives no fleet/Git authority. Groups, service accounts, PATs, custom roles,
  project ancestry/shared-pool rules, general identity REST, setup/local recovery CLI/UI, lockout/MFA,
  and legacy-adapter removal remain explicit gaps.
- Focused administration/authorization/migration gate: 32 passed. Adjacent REST, panel, managed-Git,
  reconciliation, kernel, and application-authorization regression gate: 43 passed.
- The final panel-scope/setup-response edge-case gate passed 7/7. The final serial solution build
  completed with 0 errors and only the two offline NuGet vulnerability-feed `NU1900` warnings. Full
  root tests: 298 passed, 10 platform-only skips, 0 failed (308 total, 3m19s).
- `git diff --check` passed after implementation, documentation, and evidence reconciliation.

## D30 central-upgrade implementation evidence

- Migration v12 adds immutable package metadata/publication receipts, durable upgrade operations, and
  one fenced maintenance drain per Agent. Exact schema verification and v11→v12 upgrade evidence pass.
- Package publication is bounded, archive/path/symlink validated, digest-addressed, audited, and
  principal-idempotent. Startup bundled-catalog import is idempotent across server restarts.
- Agent bearer credentials can read only the manifest and bytes for that Agent's active operation;
  another Agent receives no package oracle and no credential appears in the manifest URL/body.
- Busy work drains to completion before restart; another enrolled Agent stays schedulable. Controller
  restart preserves operation, package, and drain state.
- AgentHub uses an additive exact-session acceptance/confirmation handshake: the controller completes
  reconciliation, the Agent atomically writes the launcher marker, and only its confirmation commits
  success/releases drain.
- Real macOS/Linux child-process evidence runs bootstrap + seed Agent + candidate Agent through a
  successful authenticated download/activation. A failed executable proves one-shot LKG rollback and
  controller observation of the prior digest; that test found and closed a repeated-bad-manifest loop.
- Public REST and `viv agent package publish`, `viv agent upgrade`, and
  `viv agent upgrade-status` cover the operator path. Fleet/group rollout policy and UI remain pending.
- Focused deployment/CLI gate: 27 passed; descriptor/authorization/deployment regression gate: 8
  passed. Final serial solution gate: 308 passed, 10 platform-only skips, 0 failed (318 total, 3m31s).
  Final solution build had 0 errors and only two offline NuGet vulnerability-feed `NU1900` warnings;
  `git diff --check` passed.

## Planning checks run

- `git diff --check -- docs/ARCHITECTURE.md docs/ROADMAP.md docs/walkthrough.md docs/design/ui.md
  .workspace/workstreams/usable-agent-server` — passed.
- Inventory census counts: 44 protocol declarations + 4 Blazor routes + 9 mapped host surfaces = 57;
  `inventory.tsv` contains 57 data rows.
- `git diff --check` — passed after the integrated Wave-0 code and evidence refresh.
