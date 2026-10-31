# Lifecycle review and remaining decisions

Status: **Initial business baseline, not complete variant coverage or executed acceptance**.
Review by lifecycle prevents a happy-path catalogue from looking complete merely because every
subsystem has a folder. Stable IDs live in the feature files; this table is a review index.

| Subsystem | Enter/use/change | Failure and recovery | Leave/remove/retire |
|---|---|---|---|
| Installation | INS-01/02/04 | INS-03/05/06 | INS-05; uninstall/data erasure procedure still to specify |
| Ordinary identity | ACC-01/11, AUTH-01/02/06 | AUTH-03/04, ACC-09 | AUTH-05, ACC-12; exact account-delete variants below |
| Installation account | SU-01/02/06/07 | SU-03/08/09 | SU-04/05; password replacement is not instant retirement of every old cookie |
| Automation credentials | KEY-01/04/05 | KEY-02/03, OPS-06 | KEY-02/03; exact compatible credential variants belong to P0 |
| Roles and groups | ACC-02/04/05/07 | ACC-03/06/08/09 | ACC-06/08/13/14; exact removal variants below |
| Build settings | CFG-01/04/05/06/07, BLD-09 | CFG-02/03 | CFG-08/09; reference removal-policy details below |
| Builds | BLD-01/02/07/13, RUN-02/03/04 | RUN-01, BLD-03/04, AG-04/11 | BLD-03/04; cleanup belongs to retained results |
| Agents | AG-01/03/07/09/10 | AG-04/05/11/12/13 | AG-02/06/08/14; deletion/re-enrollment variants below |
| Results | BLD-05/06/10/11/12, ART-01 | LOG-01/02, TEST-01/02/03 | BLD-08, ART-02/03; dependency-aware cleanup details below |
| Compatibility | AUTO-01/02 | Same workflows include denials/retries, not only successful reads | Existing clients remain a cumulative gate through every milestone |
| Migration | MIG-01/02/05/07 | MIG-03/06 | MIG-04/08, OPS-02/06; source-independent retirement rehearsal |
| Interface | UI-01/02 | UI-03; protected links use current access | Leaving/reopening views has no implicit build side effect |
| Operations | OPS-01/03/04/05 | OPS-02/06/07 | OPS-08/09; complete owned uninstall/erasure tooling below |

## Variants requiring reference-backed decomposition in P0

These are named requirements to resolve, not invented product restrictions. The owner adds specific
examples and expected outcomes before implementing the affected slice. A milestone cannot claim
that family complete while its required variants remain unresolved.

| Variant | Required clarification and owner | Delivery gate |
|---|---|---|
| Startup prerequisites/degraded readiness | Platform/Identity/Persistence: readiness per resource, provisioning and upgrade/downgrade recovery. Failed account storage never creates an alternative superuser verifier. | P0 allocation; R0 startup; R1 ordinary identity |
| Account lifecycle | Identity/REST: compatible create/edit/disable/re-enable/delete and external bindings, preserving attribution. Depot's deferred deletion is not a Vivarium restriction. First-login/group modes follow the frozen TeamCity profile. | P0 contract; R1/R5 proof |
| Role/group removal and cycles | User Roles/REST: built-in/custom deletion, live assignments, nested cycles, All Users/guest/simple versus per-project modes. No invented immutable admin floor or last-admin protection that changes reference behavior. | P0 contract; R1/R5 proof |
| Destructive catalog changes | TeamCity/Results/REST: project/configuration deletion, affected queue/chains/history, pause versus manual runs and restore. Do not infer retention from the word delete; capture reference behavior and destructive impact. | P0 contract; R2/R4/R5 proof |
| Agent deletion and identity recovery | Agent API/TeamCity/Scheduling: forget/re-enroll, duplicate registration, authorization removal during work and credentials. Separate ordinary scheduling from explicit custom assignment to disabled agents (TCM-GAP-001). | P0 contract; R0/R2/R5 proof |
| Trigger/chain/resource variants | TeamCity: branches, quiet periods, missed schedules, retry/reuse, composite/personal builds, dependencies, priority, counters, locks and publisher failures. Existing TC requirements bind beyond these initial examples. | P0 allocation; R2/R3 proof |
| Source-selection timing | TeamCity/REST: default versus explicitly requested revisions, branch changes while queued, chain synchronization, multiple roots and checkout modes. Exact executed revisions are required; the old resolve-before-queue example is not a universal policy. | P0 contract; R2/R3/R5 proof |
| Tests and retained bytes | Results/Logs: TRX/JUnit/service messages, attempts, identity, investigations/mutes, partial reports, gaps, archive/path downloads, dependency/pin cleanup and unavailable content. | P0 allocation; R4/R5 proof |
| Import reconciliation | Migration/Identity/Git: stable source IDs, identities/roles, unavailable secrets, source changes during export, interrupted bytes, preview authority, whole-entity choices and absent-source objects. No field merge or source-cleanup deletion. | P0 inventory; R6 proof |
| Backup, retirement and erasure | Platform/Persistence/Security: compatible backup boundary including private settings/keys, restore validation, uninstall, deliberate erasure and secret rotation. Database-only or blob-only copies are not complete recovery. | P0 allocation; R6 rehearsal |
| Optional fleet and VM features | AgentExplorer/Providers: observations, remote operations, images and machine lifecycle need their own AC before implementation. These extensions cannot narrow Build/Agent semantics or become hidden prerequisites for TeamCity migration. | Before their feature slice; not evidence of migration parity |

P0 issue [#39](https://github.com/iXab3r/Vivarium/issues/39) owns baseline review and these decisions.
R0 issue [#40](https://github.com/iXab3r/Vivarium/issues/40) owns binding the first delivered slice.
The [documentation audit](../../.workspace/workstreams/coherent-refactor/documentation-audit.tsv)
records reconciled older claims separately from product acceptance.
