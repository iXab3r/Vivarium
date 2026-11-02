# Actor-goal stories — Stage 1 catalogue

Latest ownership: [18 active story-led task packets](story-task-migration.md), with implementation,
REST contract and REST acceptance links for every existing slice. All 25 story pages now link their
actual contributions; old source cards retain discussions and current-destination banners. This
does not change the target availability or turn intent links into runtime acceptance.

Current planning checkpoint: 2026-11-01, based on `9f5f36859f6a90309b117168db343abf14ae44df`.
Lead: Story Delivery Lead; co-held Reconciliation, REST, Business Acceptance and Docs perspectives.

The owner chose three stages: establish stories, prepare the API inventory and allocate dependencies;
then create implementation tasks and transfer useful existing discussion; finally remove redundant
work after checking that the discussion and decisions survive. This catalogue completes the first
planning catalogue and records the remaining inventory work. The
[source-union continuation and first Stage 2 packet](story-transition-stage2.md) supplied additional
source/scenario joins; [the current task migration](story-task-migration.md) expands and activates
all 18 packets. [Administrative cleanup](story-source-cleanup.md) closes 62 superseded sources with
their original bodies and discussions retained; 100 active issues remain Open.

Stories drive priority and useful outcomes. Every implementation contribution must trace upward to
an actor goal; API discovery measures missing behavior. A story does not waive reference semantics
or decide that an unused operation is out of scope. D37–D40 and the
[delivery practice](../../../docs/design/api-ledger-delivery.md#actor-goal-stories-and-traceability)
continue to govern evidence, subsystem ownership and milestone closure.

## Current result and navigation

- **25 actor goals:** 24 new GitHub stories, #86–#109, plus retained full-chain #78 as US-013.
  Existing #79 UI, #80 direct compatible REST and #81 unchanged CLI remain acceptance tracks of
  that same product outcome. No old issue body, title, comment, state, parent or milestone is
  intentionally changed by this stage; new stories are proposed and unscheduled.
- All **110 canonical AC** have at least one story link. These are intent links; all remain
  specified-unbound. Target replacement availability is **NOT_AVAILABLE** for every story.
- The **449 documented REST headings** each have one primary story/scenario intent in
  [story-links.tsv](story-links.tsv), retaining existing subsystem ownership and any known case IDs.
  Related stories are uses, not duplicate implementations. Gap links remain gaps.
- [API inventory preparation](api-inventory-preparation.md) records the installed schema, source
  identities, ambiguities, source-union work and the freeze handoff. The normalized operation and
  variant denominators remain **UNKNOWN**; no compatibility percentage or API-complete claim.
- [Dependencies](story-dependencies.tsv) distinguish used portions, contributions and needed ready
  evidence. [Preserved issue crosswalk](legacy-crosswalk.tsv) accounts for all 84 old issues.
  [Gaps](story-gaps.tsv) retain 36 business decomposition findings and 42 further source/mapping
  gaps; these records do not mean there are only 78 unknown variants.

The former 84-issue graph is the preserved source graph. The verified expanded GitHub snapshot has
108 open issues, nine milestones and 24 additional unscheduled roots at Stage 1; the
[continuation](story-transition-stage2.md) records subsequent task additions. Issue counts are not progress.
[stories.tsv](stories.tsv) is the compact catalogue; GitHub remains authoritative for live task state.

## Priority and dependencies

Full T3Personal Prepare → Windows → Release build/package and publication in a test repository remain
the first product goal; built-application launch checks are excluded. Work across UI, direct compatible REST and unchanged CLI at the same breadth
levels; completing a surface first is not a prerequisite for starting the others. The already
available [#84 reference pack](t3personal-reference-pack.md) supplies bounded metadata/manifest facts.
The independent [#85 receipt](t3personal-server-inputs.md) now supplies server-resolved source/settings
and selected producer Build IDs. Per-file consumption and historical settings-mode proof remain gaps.
#32 model, #34 C#/Git proof, #111 actual wire/effect evidence and #112 exact case/source allocation
supply their bounded gates.
Remaining P0 acceptance/review/debt/main gates still precede new-stack R0 Host + Agent and cheap
LocalSmoke; those precede the actual full-chain PILOT result. These stages do not deliver themselves
through this catalogue.

Every story has a dependency-basis row. `uses-ready-portion` names only the needed identity, contract,
service or evidence; `contributes` names integration work; `uses-reference` can already be available
while its issue is Open. `needs-ready-evidence` blocks only the stated freeze/proof stage when that
output is absent. None is a reciprocal issue-closure edge. The register retains legacy provenance
beside current execution owners from the active packet/slice registers. No native blocked-by edges
are added.

## Stories

Each card states an actor outcome, observable examples, applicable surfaces and the cheapest intended
evidence. Examples do not freeze status codes, defaults, timing or product policy. Current-grant,
denial, empty/error, cancellation and recovery variants apply where relevant; exact independent
oracles remain due before implementation. The linked AC-derived TCM set is a planning candidate,
not the frozen per-case set. AC-AUTO-01/TCM-08 also apply to compatible client projections and
TCM-10 to affected extension-neutrality cases; surface obligations are not discharged by a short list.

### US-001 — Operate a usable installation

[#86](https://github.com/iXab3r/Vivarium/issues/86) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Installation operator prepares, starts, repairs and deliberately stops or removes their installation, with actionable health and attributable changes.

**Observable scenarios:** Given prepared dependencies, When start, Then the same installation becomes ready; Given invalid settings/incompatible data, When start, Then useful diagnosis and no unsafe activation. Given saved restart-required change, When inspect/restart, Then saved/active states are distinct then converge. Given active work, When stop, Then work/ownership remains truthful. When retaining uninstall or explicit erasure, Then selected owned scope only.

**Surfaces:** UI installation/settings/health/audit; native lifecycle resources; compatible server/diagnostic declarations need exact mapping; local CLI only where supported.

**Future evidence / what to inspect:** Installation/settings/health/audit entry; `installation/server.feature`, `operations/resources-audit.feature`, `operations/local-lab.feature`. Actual Host+PG+Storage lifecycle and real local Build/readback once; controlled failed start/version/settings cases and audit query. No full platform matrix for each settings variant.

**Existing acceptance:** AC-INS-01, AC-INS-02, AC-INS-03, AC-INS-04, AC-INS-05, AC-INS-06, AC-OPS-01, AC-OPS-03, AC-OPS-04, AC-OPS-05, AC-OPS-08, AC-OPS-09. AC-derived model obligations: TCM-02, TCM-03, TCM-04, TCM-07, TCM-09, TCM-10.

**Canonical features:** [server.feature](../../../docs/acceptance/installation/server.feature), [local-lab.feature](../../../docs/acceptance/operations/local-lab.feature), [resources-audit.feature](../../../docs/acceptance/operations/resources-audit.feature).

**Discovery:** 32 primary documented-heading intent links; 0 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#14](https://github.com/iXab3r/Vivarium/issues/14), [#30](https://github.com/iXab3r/Vivarium/issues/30), [#37](https://github.com/iXab3r/Vivarium/issues/37), [#38](https://github.com/iXab3r/Vivarium/issues/38), [#43](https://github.com/iXab3r/Vivarium/issues/43), [#44](https://github.com/iXab3r/Vivarium/issues/44). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-002 — Bootstrap, delegate and recover administration

[#87](https://github.com/iXab3r/Vivarium/issues/87) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Installation operator uses the reserved installation account to establish named administration and later disables or recovers that access deliberately.

**Observable scenarios:** Given enabled installation access, When successful start/login, Then fresh committed private password grants normal administration; When delegate, Then named ordinary admin works without sharing it. When restart/disable/re-enable, Then old password and disabled authority cannot persist. Given credential initialization/account-store failure, Then diagnostic, no fallback identity/store; provider lookalike cannot link to reserved identity.

**Surfaces:** Normal UI/framework login and delegation; native identity lifecycle; compatible authentication forms/settings require exact mapping; CLI consumes ordinary credentials, no invented bootstrap command.

**Future evidence / what to inspect:** Normal login, private startup disclosure and user grant view; `authentication/installation-account.feature`. One actual normal login/delegation lifecycle plus component failures/current-session revocation and secrecy checks.

**Existing acceptance:** AC-SU-01, AC-SU-02, AC-SU-03, AC-SU-04, AC-SU-05, AC-SU-06, AC-SU-07, AC-SU-08, AC-SU-09. AC-derived model obligations: TCM-07, TCM-10.

**Canonical features:** [installation-account.feature](../../../docs/acceptance/authentication/installation-account.feature).

**Discovery:** 0 primary documented-heading intent links; 0 related uses. No exclusive documented heading is expected to describe this whole lifecycle; supplemental sources and gaps remain required. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#16](https://github.com/iXab3r/Vivarium/issues/16), [#17](https://github.com/iXab3r/Vivarium/issues/17), [#37](https://github.com/iXab3r/Vivarium/issues/37), [#45](https://github.com/iXab3r/Vivarium/issues/45), [#46](https://github.com/iXab3r/Vivarium/issues/46). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-003 — Sign in as myself and return to my work

[#88](https://github.com/iXab3r/Vivarium/issues/88) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Person signs in through trusted OIDC, manages supported profile preferences/avatar and signs out without losing identity or receiving accidental grants.

**Observable scenarios:** Given first/repeated trusted sign-in, When complete, Then same issuer/subject account and current rights, no first-user promotion. Given same email/different subject or failed/untrusted provider, Then no merged account or invented login. Given protected deep link, When login/logout/reconnect, Then allowed local destination and session semantics; profile change cannot edit another person without rights.

**Surfaces:** UI sign-in/profile/deep-link/logout; native OIDC/cookie lifecycle; compatible sessions/profile/avatar; CLI only its supported credential/profile interactions.

**Future evidence / what to inspect:** Login, protected resource link, profile; `authentication/sign-in.feature`, `interface/navigation.feature`. A few actual browser flows; controlled OIDC failures and identity collisions at component boundary; exact compatible session/profile cases.

**Existing acceptance:** AC-ACC-01, AC-AUTH-01, AC-AUTH-02, AC-AUTH-03, AC-AUTH-04, AC-AUTH-05, AC-AUTH-06, AC-UI-02, AC-UI-03. AC-derived model obligations: TCM-01, TCM-02, TCM-03, TCM-06, TCM-07, TCM-10.

**Canonical features:** [sign-in.feature](../../../docs/acceptance/authentication/sign-in.feature), [navigation.feature](../../../docs/acceptance/interface/navigation.feature).

**Discovery:** 15 primary documented-heading intent links; 19 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#16](https://github.com/iXab3r/Vivarium/issues/16), [#17](https://github.com/iXab3r/Vivarium/issues/17), [#37](https://github.com/iXab3r/Vivarium/issues/37), [#45](https://github.com/iXab3r/Vivarium/issues/45), [#46](https://github.com/iXab3r/Vivarium/issues/46). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-004 — Give and remove the right access

[#89](https://github.com/iXab3r/Vivarium/issues/89) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Access administrator manages users, nested groups, editable roles/includes and project/global grants while preserving historical attribution.

**Observable scenarios:** Given nested/group/project grants, When assign/change, Then intended current rights only. When remove membership/grant, edit role, disable/delete/re-enable account/group/role, Then exact supported current effect including existing session, stable historical attribution. Cross-project, excessive delegation, invalid cycles and last-admin variants require independent reference outcomes.

**Surfaces:** UI access management and native/compatible IAM; existing CLI actions only after pinned capability verification.

**Future evidence / what to inspect:** Users/groups/roles/effective-rights view; `access/roles.feature`. Real shared IAM/persistence authorization checks with same cookie/token before/after; small UI create/edit/revoke journeys and exact replace/delete wire cases.

**Existing acceptance:** AC-ACC-02, AC-ACC-03, AC-ACC-04, AC-ACC-05, AC-ACC-06, AC-ACC-07, AC-ACC-08, AC-ACC-09, AC-ACC-10, AC-ACC-11, AC-ACC-12, AC-ACC-13, AC-ACC-14. AC-derived model obligations: TCM-07, TCM-08.

**Canonical features:** [roles.feature](../../../docs/acceptance/access/roles.feature).

**Discovery:** 40 primary documented-heading intent links; 12 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#16](https://github.com/iXab3r/Vivarium/issues/16), [#17](https://github.com/iXab3r/Vivarium/issues/17), [#45](https://github.com/iXab3r/Vivarium/issues/45), [#46](https://github.com/iXab3r/Vivarium/issues/46). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-005 — Run automation with a controllable credential

[#90](https://github.com/iXab3r/Vivarium/issues/90) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Automation owner issues named credentials, uses supported client credential forms and revokes them without sharing a human session.

**Observable scenarios:** Given allowed scope, When issue/use, Then intended actions and safe metadata. When expire/revoke/remove grant/force supported logout, Then same credential loses authority as reference requires; local logout does not pretend to revoke unrelated PAT/provider sessions. Build-scoped credentials retrieve only permitted inputs; Basic/guest/superuser forms need exact mappings, not blanket bearer substitution.

**Surfaces:** UI My keys where supported; stock OIDC/native credentials and exact compatible auth forms; unchanged client credential use.

**Future evidence / what to inspect:** My keys and client profile setup; `authentication/credentials.feature`. Real issue/use/revoke/current-rights tests through HTTP, least-privileged byte read and client smoke; browser only for promised key-management action.

**Existing acceptance:** AC-AUTH-05, AC-KEY-01, AC-KEY-02, AC-KEY-03, AC-KEY-04, AC-KEY-05, AC-SU-08. AC-derived model obligations: TCM-07, TCM-08, TCM-10.

**Canonical features:** [credentials.feature](../../../docs/acceptance/authentication/credentials.feature), [installation-account.feature](../../../docs/acceptance/authentication/installation-account.feature), [sign-in.feature](../../../docs/acceptance/authentication/sign-in.feature).

**Discovery:** 3 primary documented-heading intent links; 13 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#17](https://github.com/iXab3r/Vivarium/issues/17), [#37](https://github.com/iXab3r/Vivarium/issues/37), [#38](https://github.com/iXab3r/Vivarium/issues/38), [#45](https://github.com/iXab3r/Vivarium/issues/45), [#46](https://github.com/iXab3r/Vivarium/issues/46). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-006 — Maintain a usable project and build definition

[#91](https://github.com/iXab3r/Vivarium/issues/91) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Project maintainer authors/reviews C# settings, organizes projects/configurations/templates and activates exactly the intended settings without rewriting past runs.

**Observable scenarios:** Given valid owned/inherited settings, When create/edit/copy/move/rename/activate, Then intended effective plan and stable identity/provenance. Given invalid expression/unsupported edit/conflict, Then independent reference/native extension outcome and last-good activation. When reset/detach/replace/reorder/remove, Then exact inherited/history/dependency consequences, no hidden alternate authority.

**Surfaces:** UI/editor plus C#/Git review/activation, native and compatible catalog mutations; supported CLI reads/mutations only, no invented general code editor.

**Future evidence / what to inspect:** Project/configuration/settings review and effective settings; `configuration/catalog.feature`. Actual C# source diff → evaluator → Git activation → readback/recovery; pure inheritance cases plus bounded public mutation tests and UI edits.

**Existing acceptance:** AC-BLD-09, AC-CFG-01, AC-CFG-02, AC-CFG-03, AC-CFG-04, AC-CFG-05, AC-CFG-06, AC-CFG-07, AC-CFG-08, AC-CFG-09. AC-derived model obligations: TCM-01, TCM-02, TCM-04, TCM-05, TCM-06, TCM-08, TCM-10.

**Canonical features:** [catalog.feature](../../../docs/acceptance/configuration/catalog.feature).

**Discovery:** 85 primary documented-heading intent links; 85 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#18](https://github.com/iXab3r/Vivarium/issues/18), [#32](https://github.com/iXab3r/Vivarium/issues/32), [#34](https://github.com/iXab3r/Vivarium/issues/34), [#47](https://github.com/iXab3r/Vivarium/issues/47), [#48](https://github.com/iXab3r/Vivarium/issues/48), [#78](https://github.com/iXab3r/Vivarium/issues/78). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-007 — Build and explain the intended source

[#92](https://github.com/iXab3r/Vivarium/issues/92) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Developer defines/reuses source roots and identifies pending/included changes, branches and exact checked-out sources.

**Observable scenarios:** Given source/settings selections, When inspect/run, Then resolved source/settings remain distinguishable and actual checkout obeys rules/submodules. Given inaccessible root/bad revision/source preparation failure, Then attributable failed preparation, no invented successful step. Forced checks/hooks/root-instance repair and deletion follow reference effects; exact-build source differs from latest source.

**Surfaces:** UI source/changes/provenance; native/compatible source metadata and exact source bytes; supported CLI reads, no invented revision flag.

**Future evidence / what to inspect:** Sources/changes and Build provenance; `builds/execution.feature`, catalog AC. Actual Agent checkout with immutable file/revision oracle; source-control boundary tests for branch/check/credentials; exact file bytes and changes queries.

**Existing acceptance:** AC-BLD-01, AC-BLD-09, AC-CFG-01, AC-CFG-07, AC-RUN-01. AC-derived model obligations: TCM-01, TCM-02, TCM-04, TCM-05, TCM-08.

**Canonical features:** [execution.feature](../../../docs/acceptance/builds/execution.feature), [catalog.feature](../../../docs/acceptance/configuration/catalog.feature).

**Discovery:** 59 primary documented-heading intent links; 3 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#13](https://github.com/iXab3r/Vivarium/issues/13), [#18](https://github.com/iXab3r/Vivarium/issues/18), [#19](https://github.com/iXab3r/Vivarium/issues/19), [#20](https://github.com/iXab3r/Vivarium/issues/20), [#59](https://github.com/iXab3r/Vivarium/issues/59), [#60](https://github.com/iXab3r/Vivarium/issues/60), [#84](https://github.com/iXab3r/Vivarium/issues/84), [#85](https://github.com/iXab3r/Vivarium/issues/85). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-008 — Admit trusted build machines

[#93](https://github.com/iXab3r/Vivarium/issues/93) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Fleet administrator registers/authorizes machines, distinguishes independent status axes and deliberately retires them.

**Observable scenarios:** Given connected unauthorized Agent, When inspect/approve, Then visible but no ordinary work before approval, then remaining policy applies. When revoke/retire, Then no new unauthorized work and current work/history truthful. Re-enrollment/reconnect cannot impersonate another Agent; idle does not automatically mean reference-deletable inactive.

**Surfaces:** UI Agents/admission and native/compatible admission reads/mutations; supported CLI views/actions only.

**Future evidence / what to inspect:** Agents list/detail/admission; `agents/admission.feature`, `agents/recovery-maintenance.feature`. Real separate Agent enrollment and admit/run denial smoke; component identity/revoke/delete checks with independent status expectations.

**Existing acceptance:** AC-AG-01, AC-AG-07, AC-AG-08, AC-AG-12, AC-AG-14. AC-derived model obligations: TCM-02, TCM-03, TCM-04, TCM-07.

**Canonical features:** [admission.feature](../../../docs/acceptance/agents/admission.feature), [recovery-maintenance.feature](../../../docs/acceptance/agents/recovery-maintenance.feature).

**Discovery:** 5 primary documented-heading intent links; 10 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#14](https://github.com/iXab3r/Vivarium/issues/14), [#37](https://github.com/iXab3r/Vivarium/issues/37), [#56](https://github.com/iXab3r/Vivarium/issues/56), [#61](https://github.com/iXab3r/Vivarium/issues/61), [#62](https://github.com/iXab3r/Vivarium/issues/62). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-009 — Match work to allowed capacity

[#94](https://github.com/iXab3r/Vivarium/issues/94) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Project/fleet operator sets requirements, reported/custom properties, pool/project associations and Agent run policies, and understands refusal reasons.

**Observable scenarios:** Given capability match plus allowed pool/policy, When inspect/queue, Then eligible capacity/reasons correct. Given matching capability but forbidden project/configuration or unavailable axis, Then no automatic assignment; When change custom property/reconnect/move pool, Then reported facts and intended values distinct and effective compatibility recalculated.

**Surfaces:** UI compatible-Agent reasons/pools/properties; native/compatible policy/requirements; supported CLI views/actions only.

**Future evidence / what to inspect:** Configuration compatible Agents and Agent pools/properties; `agents/admission.feature`. Pure requirement/policy examples plus real scheduling/access/component intersections; UI explanation and exact pool replace/move cases.

**Existing acceptance:** AC-AG-02, AC-AG-03, AC-AG-09, AC-AG-10, AC-BLD-02. AC-derived model obligations: TCM-03, TCM-04, TCM-05, TCM-07.

**Canonical features:** [admission.feature](../../../docs/acceptance/agents/admission.feature), [execution.feature](../../../docs/acceptance/builds/execution.feature).

**Discovery:** 37 primary documented-heading intent links; 5 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#20](https://github.com/iXab3r/Vivarium/issues/20), [#56](https://github.com/iXab3r/Vivarium/issues/56), [#61](https://github.com/iXab3r/Vivarium/issues/61), [#62](https://github.com/iXab3r/Vivarium/issues/62). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-010 — Control a run and the waiting work

[#95](https://github.com/iXab3r/Vivarium/issues/95) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Build operator submits normal/custom work, sees queue/approval/wait reasons and cancels/stops/retries with truthful identities and outcomes.

**Observable scenarios:** Given runnable definition, When submit, Then attributable queue→run→terminal result; Given no capacity/approval, Then exact wait. When cancel queued/running or cancel-and-requeue/retry, Then reference transitions and returned/related/reused IDs; timeout is not silent cancellation. When queue order/pause/resume or selected bulk action, Then exact affected set/current grants; denied stop/approval/disabled-Agent custom targeting cannot bypass policy.

**Surfaces:** UI Run/Queue/Build; direct native/compatible lifecycle; unchanged CLI supported run/wait/cancel/read with actual cross-readbacks.

**Future evidence / what to inspect:** Queue, Run/custom-run, Build; `builds/execution.feature`, Agent admission. Shared real run lifecycle with controlled time/Agent; a few actual entry/stop actions through each promised surface and readback. Independent wire cases settle approval, repeats and bulk effects.

**Existing acceptance:** AC-AG-06, AC-BLD-01, AC-BLD-02, AC-BLD-03, AC-BLD-04, AC-RUN-03, AC-UI-01, AC-UI-03. AC-derived model obligations: TCM-01, TCM-02, TCM-03, TCM-04, TCM-05, TCM-06, TCM-07, TCM-08, TCM-10.

**Canonical features:** [admission.feature](../../../docs/acceptance/agents/admission.feature), [execution.feature](../../../docs/acceptance/builds/execution.feature), [navigation.feature](../../../docs/acceptance/interface/navigation.feature).

**Discovery:** 18 primary documented-heading intent links; 5 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#20](https://github.com/iXab3r/Vivarium/issues/20), [#32](https://github.com/iXab3r/Vivarium/issues/32), [#38](https://github.com/iXab3r/Vivarium/issues/38), [#41](https://github.com/iXab3r/Vivarium/issues/41), [#56](https://github.com/iXab3r/Vivarium/issues/56), [#65](https://github.com/iXab3r/Vivarium/issues/65), [#66](https://github.com/iXab3r/Vivarium/issues/66), [#78](https://github.com/iXab3r/Vivarium/issues/78), [#79](https://github.com/iXab3r/Vivarium/issues/79), [#80](https://github.com/iXab3r/Vivarium/issues/80), [#81](https://github.com/iXab3r/Vivarium/issues/81), [#82](https://github.com/iXab3r/Vivarium/issues/82), [#83](https://github.com/iXab3r/Vivarium/issues/83). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-011 — Recover and maintain machines without losing work

[#96](https://github.com/iXab3r/Vivarium/issues/96) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Fleet operator handles disconnect/restart/upgrade/rollback and keeps build ownership and availability honest.

**Observable scenarios:** Given running work, When connection/server/Agent restarts, Then original accepted run recovered or bounded explicit failure, no duplicate/free-capacity fiction. When drain/restart/upgrade, Then stable Agent identity and exact newer healthy version/process or safe rollback/quarantine. Given failed download/interruption/stale acknowledgement, Then explicit failure and no unrelated-Agent effect.

**Surfaces:** UI/native maintenance and Agent/build state; compatible Agent status where applicable; existing CLI restart/upgrade capabilities, remaining UI/CLI actions explicit gaps.

**Future evidence / what to inspect:** Agent maintenance operation/status and associated Build; `agents/recovery-maintenance.feature`. Existing D30/D31 evidence reused by impact decision; real child-process success/rollback plus controlled failures and fencing/restart tests.

**Existing acceptance:** AC-AG-04, AC-AG-05, AC-AG-11, AC-AG-12, AC-AG-13, AC-BLD-04. AC-derived model obligations: TCM-02, TCM-03, TCM-04.

**Canonical features:** [recovery-maintenance.feature](../../../docs/acceptance/agents/recovery-maintenance.feature), [execution.feature](../../../docs/acceptance/builds/execution.feature).

**Discovery:** 0 primary documented-heading intent links; 0 related uses. No exclusive documented heading is expected to describe this whole lifecycle; supplemental sources and gaps remain required. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#11](https://github.com/iXab3r/Vivarium/issues/11), [#14](https://github.com/iXab3r/Vivarium/issues/14), [#44](https://github.com/iXab3r/Vivarium/issues/44), [#56](https://github.com/iXab3r/Vivarium/issues/56), [#61](https://github.com/iXab3r/Vivarium/issues/61), [#62](https://github.com/iXab3r/Vivarium/issues/62). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-012 — Execute the configured workload faithfully

[#97](https://github.com/iXab3r/Vivarium/issues/97) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Build author gets actual ordered runners/features/conditions, resolved parameters, shared limits and produced outputs from their chosen environments.

**Observable scenarios:** Given configured steps, When run, Then actual process/container/script effects and output values with reference exit/condition semantics. Given source/runner failure, skipped/always step, timeout/cancel/lock contention, Then correct continuation/outcome/resource release; secrets masked and ordinary defaults unchanged. External reporters can affect only their intended run under the frozen contract.

**Surfaces:** UI runner configuration and Build output; native/compatible runner/parameter/producers; CLI starts/reads supported workloads, no separate engine.

**Future evidence / what to inspect:** Build steps/output parameters and configuration runner editor; `builds/execution.feature`. Pure parameter/condition logic, real process/OS/container evidence for observed runners, controlled limit/time tests; no thousands of full real builds.

**Existing acceptance:** AC-BLD-01, AC-CFG-05, AC-CFG-07, AC-RUN-01, AC-RUN-02, AC-RUN-04. AC-derived model obligations: TCM-01, TCM-02, TCM-04, TCM-05, TCM-08.

**Canonical features:** [execution.feature](../../../docs/acceptance/builds/execution.feature), [catalog.feature](../../../docs/acceptance/configuration/catalog.feature).

**Discovery:** 43 primary documented-heading intent links; 14 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#19](https://github.com/iXab3r/Vivarium/issues/19), [#20](https://github.com/iXab3r/Vivarium/issues/20), [#22](https://github.com/iXab3r/Vivarium/issues/22), [#23](https://github.com/iXab3r/Vivarium/issues/23), [#24](https://github.com/iXab3r/Vivarium/issues/24), [#67](https://github.com/iXab3r/Vivarium/issues/67), [#68](https://github.com/iXab3r/Vivarium/issues/68). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-013 — Build and publish the T3Personal chain

[#78](https://github.com/iXab3r/Vivarium/issues/78) · **retained-priority-story · NOT_AVAILABLE / specified-unbound**

Build operator obtains coherent Prepare→Windows→Release build/package outputs and publication in a test repository through their chosen channel. Use frequent small-chain checks and one actual T3 build/package, without launching the built application.

**Observable scenarios:** Given frozen adapted recipe/owned Windows and Linux/Docker Agents/test-repository credential, When enter terminal Release through UI, direct REST or unchanged CLI, Then actual three configurations, reference reuse/IDs, selected artifact bytes/common version, real build/package commands and test publication. Given upstream failure/cancel/restart/revocation, Then dependency/result/publication effects remain truthful. No built-T3 launch or interactive desktop is required. Off-mode is an intermediate checkpoint only; fresh installation excludes migration and TeamCity cutover.

**Surfaces:** Preserve #79 UI, #80 direct compatible /app/rest and #81 unchanged CLI as full-chain breadth projections; shared native services/readbacks. C#/Git prepares the three configurations; UI inspects/runs/cancels builds and reads logs/artifacts. UI configuration creation/editing is a separate story.

**Future evidence / what to inspect:** Today [autonomous delivery plan](t3personal-pilot.md#autonomous-delivery-plan--ready-for-p0-execution), [pack #84](t3personal-reference-pack.md) and [server-input receipt #85](t3personal-server-inputs.md); replacement unavailable. Later independent small-chain entries through all three surfaces and one actual T3 CLI build/package/test-publication with UI/REST readbacks. #85 supplies reference server-selected identities, not actual fixture byte/platform/publication proof. All seven owner scope choices are confirmed; the future agent executes P0 proofs and merges milestones only after all due gates and independent review.

**Existing acceptance:** AC-ART-01, AC-ART-03, AC-BLD-01, AC-BLD-02, AC-BLD-03, AC-BLD-04, AC-BLD-05, AC-BLD-07, AC-BLD-13, AC-CFG-07, AC-MIG-04, AC-UI-01, AC-UI-02. AC-derived model obligations: TCM-01, TCM-02, TCM-03, TCM-04, TCM-05, TCM-06, TCM-07, TCM-08, TCM-09, TCM-10.

**Canonical features:** [dependencies.feature](../../../docs/acceptance/builds/dependencies.feature), [execution.feature](../../../docs/acceptance/builds/execution.feature), [triggers.feature](../../../docs/acceptance/builds/triggers.feature), [catalog.feature](../../../docs/acceptance/configuration/catalog.feature), [navigation.feature](../../../docs/acceptance/interface/navigation.feature), [import.feature](../../../docs/acceptance/migration/import.feature), [artifacts-retention.feature](../../../docs/acceptance/results/artifacts-retention.feature), [logs.feature](../../../docs/acceptance/results/logs.feature).

**Discovery:** 15 primary documented-heading intent links; 0 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#21](https://github.com/iXab3r/Vivarium/issues/21), [#69](https://github.com/iXab3r/Vivarium/issues/69), [#70](https://github.com/iXab3r/Vivarium/issues/70), [#78](https://github.com/iXab3r/Vivarium/issues/78), [#79](https://github.com/iXab3r/Vivarium/issues/79), [#80](https://github.com/iXab3r/Vivarium/issues/80), [#81](https://github.com/iXab3r/Vivarium/issues/81), [#82](https://github.com/iXab3r/Vivarium/issues/82), [#83](https://github.com/iXab3r/Vivarium/issues/83). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-014 — Start the right work automatically

[#98](https://github.com/iXab3r/Vivarium/issues/98) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Build/release maintainer configures source/finish/schedule events, counters/locks and publisher ownership through restart and coexistence. Trigger repetition/optimization and counter behavior follow the reference; duplicate migration publishers are prevented by the configured ownership contract, not a universal exactly-once event guarantee.

**Observable scenarios:** Given matching event/branch/time rule, When event occurs, Then intended run/reference queue optimization and number effects; excluded/quiet-period/unrelated event does not spur unintended work. Given retry/restart/overlap/old and new servers, Then one publisher owner, no lost required event or duplicate side effect.

**Surfaces:** UI configured rules/counter/origin; native/compatible configuration and real event producer effects; CLI views/actions only where supported, no invented trigger CLI.

**Future evidence / what to inspect:** Trigger rules, queue origin/counter and coexistence ownership; `builds/triggers.feature`, migration criterion. Controlled clocks/VCS/finish actors exercising real services; few actual used trigger/publication proofs.

**Existing acceptance:** AC-BLD-13, AC-MIG-04, AC-RUN-04. AC-derived model obligations: TCM-02, TCM-04, TCM-05, TCM-09, TCM-10.

**Canonical features:** [execution.feature](../../../docs/acceptance/builds/execution.feature), [triggers.feature](../../../docs/acceptance/builds/triggers.feature), [import.feature](../../../docs/acceptance/migration/import.feature).

**Discovery:** 8 primary documented-heading intent links; 0 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#21](https://github.com/iXab3r/Vivarium/issues/21), [#23](https://github.com/iXab3r/Vivarium/issues/23), [#30](https://github.com/iXab3r/Vivarium/issues/30), [#49](https://github.com/iXab3r/Vivarium/issues/49), [#50](https://github.com/iXab3r/Vivarium/issues/50), [#58](https://github.com/iXab3r/Vivarium/issues/58). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-015 — Inspect reliable live and historical output

[#99](https://github.com/iXab3r/Vivarium/issues/99) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Developer follows output while work runs and later diagnoses it without rerunning or losing the ordering/gaps.

**Observable scenarios:** Given emitting work, When tail/reconnect/open history/download, Then ordered durable output and explicit missing ranges. Given hostile/large output or secret text, Then bounded inspectable safe output; service messages cause intended owner effects without forged arbitrary state or double parsing. Revoked/cross-project reads denied.

**Surfaces:** UI live/history output; native stream and compatible/non-REST log reads; existing CLI log/download interactions after exact capability freeze.

**Future evidence / what to inspect:** Build Log and download; `results/logs.feature`. Real stream/store/restart/readback, parser unit cases and bounded hostile-output checks; one UI reconnect and stock-client/non-REST download proof.

**Existing acceptance:** AC-BLD-05, AC-LOG-01, AC-LOG-02. AC-derived model obligations: TCM-06, TCM-08.

**Canonical features:** [logs.feature](../../../docs/acceptance/results/logs.feature).

**Discovery:** 1 primary documented-heading intent links; 5 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#24](https://github.com/iXab3r/Vivarium/issues/24), [#38](https://github.com/iXab3r/Vivarium/issues/38), [#51](https://github.com/iXab3r/Vivarium/issues/51), [#52](https://github.com/iXab3r/Vivarium/issues/52). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-016 — Follow one test across builds

[#100](https://github.com/iXab3r/Vivarium/issues/100) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Developer sees stable tests, individual attempts, durations/details/attachments and first-failed/fixed/flaky history across branches/configurations.

**Observable scenarios:** Given actual reports/producer evidence, When inspect test/occurrences, Then exact attempts and original provenance; Given missing/malformed/partial evidence, Then distinct incomplete reporting, never fake passing suite. Running/ignored/muted outcomes remain distinct; long/parameterized identities stay stable under frozen reference semantics.

**Surfaces:** UI tests/history/attempts; native/compatible queries and real report producers; supported CLI queries only.

**Future evidence / what to inspect:** Build Tests and Test history; `results/tests.feature`. Independent tiny TRX/JUnit/service-message producers, real ingestion/persistence/query and selected UI drilldown; actual Compile report baseline partial R2.

**Existing acceptance:** AC-BLD-06, AC-TEST-01, AC-TEST-02. AC-derived model obligations: TCM-06, TCM-08.

**Canonical features:** [tests.feature](../../../docs/acceptance/results/tests.feature).

**Discovery:** 10 primary documented-heading intent links; 5 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#20](https://github.com/iXab3r/Vivarium/issues/20), [#25](https://github.com/iXab3r/Vivarium/issues/25), [#71](https://github.com/iXab3r/Vivarium/issues/71), [#72](https://github.com/iXab3r/Vivarium/issues/72). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-017 — Resolve or mute a known failure without erasing it

[#101](https://github.com/iXab3r/Vivarium/issues/101) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Developer/triager assigns investigations and scoped mutes for test failures/build problems and ends them deliberately or on the specified expiry.

**Observable scenarios:** Given failing occurrence/problem, When investigate/reassign/mute, Then intended scope/responsibility and preserved original outcome. When resolve/unmute/expire or selected bulk mutation, Then exact current effect/history; denied cross-project or partial bulk result cannot silently affect hidden targets.

**Surfaces:** UI investigation/mute actions; native/compatible triage; supported CLI operations only.

**Future evidence / what to inspect:** Tests/problems investigations/mutes; `results/tests.feature`. Real scoped triage current-rights component cases with controlled expiry; minimal UI assign/end flow and exact bulk contract.

**Existing acceptance:** AC-BLD-10, AC-TEST-03. AC-derived model obligations: TCM-06, TCM-07, TCM-08.

**Canonical features:** [tests.feature](../../../docs/acceptance/results/tests.feature).

**Discovery:** 13 primary documented-heading intent links; 0 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#25](https://github.com/iXab3r/Vivarium/issues/25), [#71](https://github.com/iXab3r/Vivarium/issues/71), [#72](https://github.com/iXab3r/Vivarium/issues/72). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-018 — Retrieve and retain the right bytes

[#102](https://github.com/iXab3r/Vivarium/issues/102) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Build consumer/release owner browses exact-build files/archives, downloads original outputs and protects required retained data through cleanup.

**Observable scenarios:** Given selected run and file, When browse/download, Then exact source/output bytes with hash and path semantics. When pin/unpin/cleanup, Then actual reference retention effects and explain absent data; denied project/range/path/expired access does not leak bytes. Explicit deletion has its own frozen consequences, not an assumed universal pin veto.

**Surfaces:** UI artifact browser/retention; native/compatible/non-REST bytes/archive/range; supported CLI downloads, actual output-path semantics.

**Future evidence / what to inspect:** Build Artifacts, archive download and retention; `results/artifacts-retention.feature`. Real storage HTTP bytes/ranges/archive/ACL checks, independent manifests, cleanup reference/pin cases; one UI/download flow.

**Existing acceptance:** AC-ART-01, AC-ART-02, AC-ART-03, AC-BLD-08. AC-derived model obligations: TCM-05, TCM-06, TCM-08.

**Canonical features:** [artifacts-retention.feature](../../../docs/acceptance/results/artifacts-retention.feature).

**Discovery:** 13 primary documented-heading intent links; 5 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#26](https://github.com/iXab3r/Vivarium/issues/26), [#38](https://github.com/iXab3r/Vivarium/issues/38), [#44](https://github.com/iXab3r/Vivarium/issues/44), [#73](https://github.com/iXab3r/Vivarium/issues/73), [#74](https://github.com/iXab3r/Vivarium/issues/74). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-019 — Find and explain retained work

[#103](https://github.com/iXab3r/Vivarium/issues/103) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Developer/operator finds every matching visible Build, compares measurements, annotates it and distinguishes administrative displayed status from original evidence.

**Observable scenarios:** Given paginated historical/imported/renamed work, When search/filter/follow links, Then complete permitted matches with no hidden-project leakage. When tag/comment/number/pin/status/bulk change, Then reference effects plus actor/reason; original run/test evidence stays inspectable. Missing measurement differs from zero and produced/resolved values differ from defaults.

**Surfaces:** UI history/search/measurements/annotations; native/compatible query/mutation projections; supported CLI views/annotations only.

**Future evidence / what to inspect:** History/search/statistics and Build metadata; `results/discovery.feature`. Independent paginated fixture with known visible set, real query/projection/current grants; exact measurement/metadata/effects cases and one navigation journey.

**Existing acceptance:** AC-BLD-09, AC-BLD-11, AC-BLD-12, AC-OPS-05, AC-UI-01, AC-UI-02. AC-derived model obligations: TCM-01, TCM-02, TCM-03, TCM-04, TCM-05, TCM-06, TCM-07, TCM-10.

**Canonical features:** [catalog.feature](../../../docs/acceptance/configuration/catalog.feature), [navigation.feature](../../../docs/acceptance/interface/navigation.feature), [resources-audit.feature](../../../docs/acceptance/operations/resources-audit.feature), [discovery.feature](../../../docs/acceptance/results/discovery.feature).

**Discovery:** 27 primary documented-heading intent links; 10 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#20](https://github.com/iXab3r/Vivarium/issues/20), [#25](https://github.com/iXab3r/Vivarium/issues/25), [#26](https://github.com/iXab3r/Vivarium/issues/26), [#65](https://github.com/iXab3r/Vivarium/issues/65), [#66](https://github.com/iXab3r/Vivarium/issues/66), [#71](https://github.com/iXab3r/Vivarium/issues/71), [#72](https://github.com/iXab3r/Vivarium/issues/72), [#73](https://github.com/iXab3r/Vivarium/issues/73), [#74](https://github.com/iXab3r/Vivarium/issues/74). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-020 — Publish and know what is deployed

[#104](https://github.com/iXab3r/Vivarium/issues/104) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Release operator publishes configured status/labels/releases and can identify the actual Build deployed to each environment with retained history.

**Observable scenarios:** Given authorized configured integration/credential, When publish/deploy/report deployment, Then actual effect and mapped build/environment history, not property-bag success. Given failure/retry/concurrent publisher or deleted instance, Then reference result/history and no duplicate unintended effect. T3 publication is early due; broad deployment dashboards are separate later scope.

**Surfaces:** UI publication/deployment readback where promised; native/compatible configuration/dashboard history and real producer integration; CLI supported views/start/read, not an invented publisher command.

**Future evidence / what to inspect:** Publication result, deployment dashboard/environment history; nearest existing build/dependency AC and REST gap record. Real producer boundary/effect receipt plus controlled retry/error/current-grant tests; external execution authorization remains separate from planning.

**Existing acceptance:** AC-AUTO-01, AC-BLD-07, AC-BLD-13, AC-CFG-01, AC-CFG-07, AC-MIG-04. AC-derived model obligations: TCM-01, TCM-02, TCM-04, TCM-05, TCM-08, TCM-09, TCM-10.

**Canonical features:** [compatibility.feature](../../../docs/acceptance/automation/compatibility.feature), [dependencies.feature](../../../docs/acceptance/builds/dependencies.feature), [triggers.feature](../../../docs/acceptance/builds/triggers.feature), [catalog.feature](../../../docs/acceptance/configuration/catalog.feature), [import.feature](../../../docs/acceptance/migration/import.feature).

**Discovery:** 13 primary documented-heading intent links; 0 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#21](https://github.com/iXab3r/Vivarium/issues/21), [#22](https://github.com/iXab3r/Vivarium/issues/22), [#23](https://github.com/iXab3r/Vivarium/issues/23), [#58](https://github.com/iXab3r/Vivarium/issues/58), [#75](https://github.com/iXab3r/Vivarium/issues/75), [#76](https://github.com/iXab3r/Vivarium/issues/76). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-021 — Bring complete retained history into Vivarium

[#105](https://github.com/iXab3r/Vivarium/issues/105) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Migration operator previews and imports the retained project/build/change/test/problem/actor/deployment graph and original files so it remains useful independently of TeamCity.

**Observable scenarios:** Given authoritative bounded inventory, When preview/import, Then scoped additions/conflicts/blockers and stable mappings/links/bytes; no automatic trigger/grant/Agent enrollment/publication. Given interruption/source change/missing bytes/deleted parent, Then resumable truthful partial state and explicit gap, no fabricated completion. Source cleanup cannot delete imported history.

**Surfaces:** UI/native import preview/progress and retained readbacks; source compatible reads are import inputs; future local CLI only when actually provided.

**Future evidence / what to inspect:** Import preview/progress, imported Build/test/history/file/deployment links; `migration/import.feature`. Controlled paginated TeamCity boundary exercising real import and explicit mappings, actual bounded retained graph/byte proof and source-disconnected reads. Full retained universe has no accepted denominator yet.

**Existing acceptance:** AC-ART-01, AC-ART-03, AC-MIG-01, AC-MIG-03, AC-MIG-05, AC-MIG-06. AC-derived model obligations: TCM-01, TCM-05, TCM-06, TCM-07, TCM-08, TCM-09.

**Canonical features:** [import.feature](../../../docs/acceptance/migration/import.feature), [artifacts-retention.feature](../../../docs/acceptance/results/artifacts-retention.feature).

**Discovery:** 0 primary documented-heading intent links; 0 related uses. No exclusive documented heading is expected to describe this whole lifecycle; supplemental sources and gaps remain required. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#13](https://github.com/iXab3r/Vivarium/issues/13), [#29](https://github.com/iXab3r/Vivarium/issues/29), [#32](https://github.com/iXab3r/Vivarium/issues/32), [#54](https://github.com/iXab3r/Vivarium/issues/54), [#55](https://github.com/iXab3r/Vivarium/issues/55). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-022 — Coexist, reconcile differences and retire TeamCity safely

[#106](https://github.com/iXab3r/Vivarium/issues/106) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Migration/release operator repeats one-way diff import, chooses whole-entity conflicts and proves final delta/standalone use before shutdown.

**Observable scenarios:** Given changed/new/unchanged source entities and local edits, When diff, Then add/skip or explicit whole-Vivarium/whole-TeamCity choice, no field merge/duplicates. Given parallel servers, Then single active trigger/publisher ownership. When final delta/rehearse all families, Then retained history/bytes and intended execution proven before retirement; blockers prevent false readiness.

**Surfaces:** UI/native diff decisions/coexistence/retirement receipts; source compatible reads as inputs; no invented CLI cutover command.

**Future evidence / what to inspect:** Diff choice/receipt, coexistence ownership, per-family acceptance and retirement report; `migration/import.feature`. Deterministic repeated/interrupted conflict cases plus actual bounded/full due migration, byte and family evidence with independent reconciliation.

**Existing acceptance:** AC-MIG-01, AC-MIG-02, AC-MIG-03, AC-MIG-04, AC-MIG-07, AC-MIG-08. AC-derived model obligations: TCM-01, TCM-05, TCM-06, TCM-07, TCM-09, TCM-10.

**Canonical features:** [import.feature](../../../docs/acceptance/migration/import.feature).

**Discovery:** 0 primary documented-heading intent links; 0 related uses. No exclusive documented heading is expected to describe this whole lifecycle; supplemental sources and gaps remain required. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#13](https://github.com/iXab3r/Vivarium/issues/13), [#23](https://github.com/iXab3r/Vivarium/issues/23), [#29](https://github.com/iXab3r/Vivarium/issues/29), [#30](https://github.com/iXab3r/Vivarium/issues/30), [#31](https://github.com/iXab3r/Vivarium/issues/31), [#54](https://github.com/iXab3r/Vivarium/issues/54), [#55](https://github.com/iXab3r/Vivarium/issues/55), [#58](https://github.com/iXab3r/Vivarium/issues/58). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-023 — Restore a complete installation and trust the result

[#107](https://github.com/iXab3r/Vivarium/issues/107) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Installation operator requests/follows a backup and restores work, configuration, identities, keys and original bytes in a fresh installation.

**Observable scenarios:** Given complete consistent backup, When restore, Then retained graph/files/accounts/current-valid credentials and private dependencies work. Given missing/corrupt/incompatible material or failed/single-flight backup, Then exact useful status and no fabricated successful restore or fallback identity.

**Surfaces:** UI/native backup/status/restore and restored journeys; compatible backup mappings remain reference gap; local restore/CLI actions only where actually provided.

**Future evidence / what to inspect:** Backup operation/status and fresh restored installation; `operations/backup-restore.feature`, resource AC. Real fresh restore once with known graph/byte/identity checks; component validation/failure/job lifecycle cases. Backup source format mapping remains reference work.

**Existing acceptance:** AC-INS-06, AC-OPS-02, AC-OPS-06, AC-OPS-07. AC-derived model obligations: TCM-04, TCM-06, TCM-07, TCM-09, TCM-10.

**Canonical features:** [server.feature](../../../docs/acceptance/installation/server.feature), [backup-restore.feature](../../../docs/acceptance/operations/backup-restore.feature), [resources-audit.feature](../../../docs/acceptance/operations/resources-audit.feature).

**Discovery:** 2 primary documented-heading intent links; 0 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#14](https://github.com/iXab3r/Vivarium/issues/14), [#29](https://github.com/iXab3r/Vivarium/issues/29), [#30](https://github.com/iXab3r/Vivarium/issues/30), [#54](https://github.com/iXab3r/Vivarium/issues/54), [#55](https://github.com/iXab3r/Vivarium/issues/55). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-024 — Inspect and safely act on my fleet independently of builds

[#108](https://github.com/iXab3r/Vivarium/issues/108) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Fleet operator inspects permitted host/process/network/software facts and performs only explicitly supported remote operations without changing build semantics.

**Observable scenarios:** Given permitted capability, When read/refresh, Then bounded fresh/stale/partial/unsupported/policy-disabled/offline states and redacted data. Given stale session or denied scope, Then no stale publication/leak. Given supported mutating operation, When execute/cancel, Then attributable bounded outcome and no overlapping capacity-one Build; provider-only actions stay provider-owned.

**Surfaces:** AgentExplorer UI/native fleet observations and explicitly supported operations; Agent protocol is separate; no fabricated compatible fleet endpoint or CLI capability.

**Future evidence / what to inspect:** AgentExplorer Agent facts and explicit operation views; `automation/compatibility.feature`, focused AgentExplorer design. Real shared Agent identity/lease/access component checks and selected actual platform probes/operations. Static host facts currently exist; dynamic inventory/remote command UI is not thereby available.

**Existing acceptance:** AC-AG-01, AC-AG-10, AC-AG-11, AC-AG-12, AC-AUTO-02, AC-OPS-05. AC-derived model obligations: TCM-02, TCM-03, TCM-04, TCM-07, TCM-08, TCM-10.

**Canonical features:** [admission.feature](../../../docs/acceptance/agents/admission.feature), [recovery-maintenance.feature](../../../docs/acceptance/agents/recovery-maintenance.feature), [compatibility.feature](../../../docs/acceptance/automation/compatibility.feature), [resources-audit.feature](../../../docs/acceptance/operations/resources-audit.feature).

**Discovery:** 0 primary documented-heading intent links; 0 related uses. No exclusive documented heading is expected to describe this whole lifecycle; supplemental sources and gaps remain required. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#11](https://github.com/iXab3r/Vivarium/issues/11), [#17](https://github.com/iXab3r/Vivarium/issues/17), [#56](https://github.com/iXab3r/Vivarium/issues/56), [#61](https://github.com/iXab3r/Vivarium/issues/61), [#62](https://github.com/iXab3r/Vivarium/issues/62). Original decisions/comments remain at those sources; this is not a completed transfer.

### US-025 — Obtain and release required build capacity

[#109](https://github.com/iXab3r/Vivarium/issues/109) · **stage1-proposed-unscheduled · NOT_AVAILABLE / specified-unbound**

Capacity operator starts/stops referenced cloud/provider capacity and distinguishes machine type, requested instance and connected Agent.

**Observable scenarios:** Given required mapped provider/profile and rights, When start, Then real capacity state→Agent linkage and project eligibility. Given failure/quota/unsupported capability, Then truthful provider failure/unavailability; When normal/forced stop during work, Then exact reference effects and no false safely idle machine or unrelated resource damage.

**Surfaces:** UI/native/compatible capacity/profile/instance views/actions where mapped; actual provider/Agent effects; supported CLI actions only.

**Future evidence / what to inspect:** Capacity/profile/instance and Agent readiness; current nearest Agent AC plus REST finding. Controlled provider boundary with real coordination state and exact wire/effect cases; minimal actual provider proof only for required source mapping. No speculative hypervisor framework.

**Existing acceptance:** AC-AG-01, AC-AG-09, AC-AG-12, AC-AUTO-01, AC-BLD-02. AC-derived model obligations: TCM-02, TCM-03, TCM-04, TCM-07, TCM-08.

**Canonical features:** [admission.feature](../../../docs/acceptance/agents/admission.feature), [recovery-maintenance.feature](../../../docs/acceptance/agents/recovery-maintenance.feature), [compatibility.feature](../../../docs/acceptance/automation/compatibility.feature), [execution.feature](../../../docs/acceptance/builds/execution.feature).

**Discovery:** 10 primary documented-heading intent links; 5 related uses. Dependencies are in [the register](story-dependencies.tsv); variants/cases and source gaps stay with their existing owners.

**Retained context:** [#56](https://github.com/iXab3r/Vivarium/issues/56), [#57](https://github.com/iXab3r/Vivarium/issues/57), [#63](https://github.com/iXab3r/Vivarium/issues/63), [#64](https://github.com/iXab3r/Vivarium/issues/64). Original decisions/comments remain at those sources; this is not a completed transfer.

## Stage 2 handoff and Stage 3 cleanup

Use story scenarios to select coherent subsystem contributions, retaining the single primary owner
and shared services. Create implementation tasks that name the story, AC/TCM, exact source variants,
contract/implementation/REST-acceptance joins, due milestone, evidence boundary and observable result.
Do not create one backend implementation per surface or a task for each discovered route.

For each old issue, transfer the useful decisions, rationale, rejected alternatives that still
explain the decision, open questions, constraints, dependencies and evidence limits into the new task.
Use stable issue/comment anchors and a source-to-destination readback. A link alone is not a transfer;
`legacy-crosswalk.tsv` distinguishes NOT_TRANSFERRED from explicit partial transfers in
[task-transfer.tsv](task-transfer.tsv); neither means complete migration. Capture changes/comments arriving after the
baseline before cleanup. Existing local snapshots are partial preservation evidence, not a complete
archive of timelines, attachments, external evidence and later edits.

Only after that reconciliation can redundant tasks be removed from the active graph. Preserve the
original conversations through supersession/backlinks or a verified complete archive. Permanent
deletion needs zero unexplained losses, including attachments and post-snapshot changes. No old task
is closed or deleted here; cleanup closure must never imply implementation acceptance.

## Verification boundary

The [Stage 1 verification record](story-stage1-verification.md) records the actual local joins,
GitHub readbacks and checks. Historical product evidence stays at its original revisions: fifteen
known conformance cases, five RED, ten blocked, zero GREEN. This stage creates no product code,
runtime proof, frozen variant, AC binding, generated API status, independent final review or milestone
acceptance. No build, test, production/reference mutation, publisher or service lifecycle is run.
