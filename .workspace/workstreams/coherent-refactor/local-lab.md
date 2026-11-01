# Local server + Agent lab — first new-stack farm milestone

Status: target contract under D36, **not delivered or launched by this planning task**.
The existing legacy Host/Agent entry points and SessionLoopTests already demonstrate a protocol
loop, but use SQLite/custom authentication and are not the new reproducible PG/Chiral environment.
Pinned SDK 10.0.303 has been acquired and verified in private tooling without changing the pin.
The separate TeamCity reference sandbox is available; it does not deliver this Vivarium R0 lab.

D38 now places executable reference-backed REST tests and the first real conformance RED baseline
in P0, before this new-stack farm milestone. Prepare the exact test toolchain there; do not make the
early corpus wait for the full PG/Chiral/Agent lab. P0 HTTP/replay evidence is not a claim that R0's
separate-Agent execution or operator deployment experience already exists.

## When it becomes usable

| Gate | What the owner can actually do | What it does not yet claim |
|---|---|---|
| R0.1 | Prepare/start local PG, Host and a real separate Agent; open current-style UI, inspect nodes/health, enroll/authorize the Agent, attach debuggers and restart/reset/stop | Full catalogue authoring, source checkout, all roles/clients or full build parity |
| R0.2 | Use unchanged teamcity-cli with a local profile to run/watch/cancel seeded LocalSmoke, inspect the same run through native UI/API and download a verified artifact | Entire CLI route surface or migrated real build families |
| R0.3 (R0 exit) | Repeat the above after Host/Agent disconnect/restart, preserve stable identity/ownership/results, prove clean reset/teardown and protected output | All platform fault cases, upgrade rollout or complete durable searchable logs |
| R1 | Complete ordinary identity/token lifecycle and exact TeamCity project/group roles beyond R0's required ordinary-user credential and current-grant subset | Source-based full Compile still belongs to R2 |
| R2 | Configure and run a genuine source-based C# Compile with exact Git revisions/requirements and durable provenance | Chains/publishers/history migration still have their later gates |

The local Host/Agent lab must not wait until R2 or R5. It is usable at R0.1; the first compatible
execution journey is R0.2. Each milestone may merge to main after its own acceptance. No calendar
estimate is implied before the P0 contract/binding work and initial preparation are measured.
R0.1/R0.2 are internal checkpoints usable on the milestone branch before whole-R0 acceptance;
they do not waive root regressions, existing-feature disposition or review required to merge R0.

## Composition and lifecycle

Use the existing EyeAuras.Automation consumer approach, not a new orchestration engine:

- One real PostgreSQL instance with pinned image/version and an owned isolated database.
- One native ASP.NET Host process with co-hosted Chiral nodes and real local Storage backend.
- One real native Agent process with its own data directory/stable identity and normal reverse gRPC.
- One local TLS CA/certificate/trust setup suitable for unchanged CLI HTTP validation; Agent uses
  its normal pin. No disabled TLS validation, credential bypass or special lab-only queue API.
- Bundled TypeScript/Workbench UI preserving TeamCity/current Vivarium composition. No frontend
  runtime service, forced Depot outline layout or separate browser authority.
- Stock Identity/OpenIddict installation login plus the minimal ordinary external-OIDC/My keys/current-grant
  contribution in R0; R1 completes identity, roles/groups, compatible PAT wire and account lifecycle.
  The installation user is excluded from My keys issuance and code redemption in the frozen Depot
  source. Its password/cookie cannot supply the unchanged CLI bearer.

The [R0 journey manifest](r0-journey-manifest.md) is the concrete, still **UNFROZEN** planning input
owned by #36, consumed by #43/#14/#37/#38 and verified by #42/#44. It names request shapes,
ordinary/admin/restricted principals, activated seeds, required states, effects and reference gaps.
It adds no fixture language, product behavior or executed case. #32/#33 establish unknown TeamCity
facts; #34 proves the seed's source representation before R0 implementation.

Preparation pins SDK/packages/browser/client inputs and builds the real programs/fixture payload.
start/status/attach/run/reset/stop use the same product fixture composition as acceptance; exact
supported commands/help and executable examples are delivered with R0, not invented as working now.
Use owned task-local processes/data and bounded logs. Stop/reset never targets a live TeamCity/Depot
service or another checkout. Windows native processes support ordinary IDE attachment; closed Linux
acceptance proves its separate no-egress/ownership boundary. Do not label native loopback as network-none.

## R0.1 proof

Start a fresh lab from documented prepared inputs. Host passes real PG/schema/storage readiness,
the expected Chiral nodes are observed and the UI loads. Agent appears with normal pending status,
operator authorization succeeds through the normal authorized management path, and the subsequent
credential/session handshake makes it eligible. Show independent connected/authorized/enabled/idle
facts and a real heartbeat. A health endpoint alone does not prove the Agent loop.

Restart the Agent with the same directory and retain its stable identity; unknown/unavailable status
is displayed truthfully. Debugger attachment does not alter domain rules. Reset removes only owned
lab state; stop leaves no owned child process/container behind. Commands and safe status receipts make
the same environment usable by a human and by Automation.

Before #38, prove ordinary OIDC sign-in with no implicit grants, My keys finite bearer issuance,
installation-user token denial, and normal installation login granting the ordinary account
TeamCity SYSTEM_ADMIN through the shared authorized Access path. The same pre-grant bearer must
gain current rights. Prepare a second ordinary principal with only the required project grant for
positive own-project reads and denied cross-project artifact/Agent-control checks. #14 integrates
this SS-IAM contribution from #16/#17 without depending on complete R1 acceptance.

## R0.2 proof

Seed one Git-owned LocalSmoke configuration using P0's final approved definition representation and
the shared catalogue activation path. Use the eventual process runner/queue/storage services; no
in-memory sample catalogue, separate fake Agent, direct test-only dispatch or throwaway runner engine.
Full evaluator/editor/inheritance and product-source checkout expand in R2.

Run an actual prepared native fixture executable. It emits recognizable stdout and smoke.txt and
supports deterministic nonzero/long-running modes for negative/cancel checks. Through a separate
normal CLI profile: authenticate, discover Agent/configuration, enqueue/watch completion, inspect
outcome, list/download/hash smoke.txt through the client's actual artifact `/files/smoke.txt` path.
Execute the nonzero mode and prove terminal FAILURE plus unchanged CLI nonzero exit. Separately
cancel queued work and prove it never starts; cancel running long work and prove actual stop,
terminal state and capacity release, with reference-defined raced-completion handling. Native
UI/API observe the same run IDs/state and retained raw output. Verify denied access using the
restricted principal and known protected bytes, with positive controls. No real publisher/trigger
or production credential is involved.

## R0.3 proof and replacement discipline

Task ownership: #14 owns the Host/Agent recovery integration; #15 supplies owned fixtures, #38 the
unchanged-client journey and #40 the canonical bindings. #44 explicitly verifies R0.3 REST/native
consistency and restart/cleanup evidence. R0.3 is an existing R0 exit obligation, not an unowned
future milestone or a requirement to create another runner/epic.

Focused real PG/protocol tests cover concurrent admission, queue wait, exact ownership/session fences,
cancel intent and duplicate/stale result rejection. A small actual process journey covers disconnect/
reconnect and Host restart while work is owned; it must preserve a truthful terminal outcome and avoid
duplicate execution/result acceptance. Virtual time proves ordering in cheap tests; real processes
prove actual transport/restart boundaries. Declare unsupported native containment faults explicitly.

Capture exact revision/tool/input fingerprints, actual run/Agent identity, byte hashes, state checks
and cleanup outcome without raw credentials. R0 exit includes root build/test plus affected real
PG/protocol/Gherkin/unchanged-client cases and milestone review. No SDK/package downloads during the
prepared acceptance run. Replaced foundation code is removed where its named invariants/data/consumers
have a proven replacement; a temporary adapter has one authority, owner and removal milestone.
