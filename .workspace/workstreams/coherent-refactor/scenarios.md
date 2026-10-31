# Technical scenario and evidence catalogue — S* candidates

D37 clarification: this S* table is the **technical evidence/candidate-check catalogue**, not the
highest-level business AC. Canonical business language is in docs/acceptance/*.feature and linked
by acceptance-map.tsv. Preserve these S* IDs and useful assertions; do not rewrite business AC to
match PostgreSQL/Chiral/HTTP/fencing details here. Gherkin does not force every supporting check
through a browser or process. No current table row or feature-file parse is an executed pass.

Write executable English `.feature` files as each approved mechanism is implemented; this table is
the planning catalogue, **not 38 implemented/passing scenarios**. Each case is independently selected
and owns a fresh fixture where isolation is needed. Scenario outlines cover named variants, not a
Cartesian product of every OS, role, runner and fault. `requirement-routing.tsv` assigns every existing
TC requirement to its primary milestone and candidate scenarios; that is planning coverage only.

| ID | Milestone | User/contract example and failure boundary | Lowest decisive evidence |
|---|---|---|---|
| S01 | R1 | A new ordinary user signs in via trusted OIDC, obtains one local issuer+sub identity and no implicit privileges; repeated login is stable | Real issuer + Host/PG; UI sign-in |
| S02 | R1 | An operator uses the approved installation/recovery login and grants an ordinary user Admin online; a restart follows the selected credential/session policy | Identity UI + restart, private disclosure checks |
| S03 | R1 | An admin removes access or disables a user; the unchanged browser cookie and unchanged bearer token immediately lose the affected rights | Actual framework credentials + real resource policies |
| S04 | R1 | A user issues/lists/revokes a token; metadata never exposes it again; logout and token revocation have explicitly different effects | UI + unchanged client token; CSRF/return-origin negatives |
| S05 | R1/R2 | Viewer cannot enqueue/edit; project developer cannot administer fleet or sibling project; service account cannot exceed its grants | Shared native/compatible policy probes + one UI negative |
| S06 | R0 | Operator prepares/starts/attaches/runs/resets/stops one owned local Host + real separate Agent with PG/storage and sees required nodes/status | Same fixture via debug and Automation; actual process identity and cleanup |
| S07 | R0 | Two settings editors race; stale CAS fails, secrets remain masked, valid Save applies on restart and keys/settings survive | Workbench UI + real restart/storage |
| S08 | R2 | A pinned trusted C# catalog revision yields an immutable effective plan; invalid, timed-out or oversized evaluation keeps last good active state | Pure validation plus real separate evaluator |
| S09 | R2 | Operator changes a supported parameter/requirement through UI or compatible REST; Git diff/revision explains the effective result and concurrent change conflicts | Actual Git + shared mutations, approved RF-05 representation |
| S10 | R2 | Developer selects exact source/settings revisions and submodules; later HEAD cannot change an enqueued run; private credentials stay private | Local real Git origin + real Agent |
| S11 | R2/R3 | Agent facts and custom parameters explain exists/equals/not-equals/contains and all admitted operators; connected/authorized/enabled/busy remain independent | Matcher logic + API/UI incompatibility reasons |
| S12 | R2 | Incompatible FIFO head does not block another eligible build; two dispatchers cannot claim one Agent/run | Deterministic scheduling + real PG race |
| S13 | R2 | Restart/disconnect/duplicate assignment/result/stale session cannot double-run or overwrite terminal state; ambiguous capacity quarantines | Scripted Agent + virtual time + PG restart; real Agent smoke |
| S14 | R2 | User cancels queued/running build; reconnect preserves intent; bounded force stop reports truth rather than successful cancellation before process exit | State tests + actual process containment on target OS |
| S15 | R2/R6 | Agent enrollment, unauthorized/disabled behavior, restart/upgrade and previous compatible protocol retain identity and owned work | Real Agent/HTTP/gRPC; supported old binary when available |
| S16 | R3 | Prompted/typed/secret/inherited parameters and shared templates yield ordered steps/output values and stable provenance | Plan tests + run UI + secret redaction |
| S17 | R3 | Compile → Test → Release → Publish freezes upstream runs/revisions, reuse and same-Agent policy; failed/cancelled upstream prevents wrong release | DAG simulation + representative actual chain |
| S18 | R3 | Build receives exact upstream artifacts with include/exclude/rename/archive/extract/clean behavior and verified bytes | Golden mapping + real storage/Agent filesystem |
| S19 | R3 | VCS/finish/schedule events enqueue once with correct branch/quiet/timezone policy despite restart; import does not activate a second trigger | Virtual time + VCS/source-event simulator + PG |
| S20 | R3 | Concurrent release jobs honor shared locks and counter/build-number/output semantics; dry-run cannot publish | PG races + real local publisher fixture |
| S21 | R3 | Each observed .NET/script/C#/container/build feature maps to a supported runner with correct environment/service messages/exit status | Representative real tools per inventoried family; unknown features block |
| S22 | R4 | Developer tails/searches/downloads a long build log; reconnect/restart preserves order or explicit gaps and bounds memory; structured output cannot grant privileges | Segment/parser tests + restart/stream + actual UI |
| S23 | R4 | Operator downloads retained artifacts, pins a release, applies retention and runs GC without breaking live references; interrupted/corrupt upload is rejected | Actual bytes/hash/storage/PG; UI download |
| S24 | R4 | Developer sees TRX and currently used JUnit producers with correct failed/ignored/parameterized identities, durations and output | Golden producer fixtures + actual producer + UI |
| S25 | R4 | Developer opens a test across builds/branches/retries, compares failure details and timing; old builds retain correct source/Agent/config provenance | Real historical projection + independent UI journey |
| S26 | R4 | Investigator mutes/unmutes/assigns/resolves a problem without hiding original failures or granting a viewer mutation rights | Domain/API tests + ordinary user UI |
| S27 | R4 | Developer finds old builds by branch/status/date/number/tag/pin/Agent, follows chain/test/log deep links and reads statistics | API deterministic paging + actual browser journey |
| S28 | R4/R6 | Operator restores PG/blobs/node settings/Identity keys to a fresh owned stack; retained build/test/log/artifact links and allowed credentials work | Encrypted backup + actual fresh restore, no source available |
| S29 | R5 | Unchanged teamcity-cli lists/queues/cancels/edits/downloads using supported JSON/XML/text, locators/fields/paging and IDs beyond JS safe integer range | Frozen CLI binary/source version against real facade |
| S30 | R5 | Actual other clients and malformed/denied/stale requests get compatible status/body/headers and shared authorization/audit/idempotency behavior | Route/variant conformance plus real clients |
| S31 | R6 | Initial import preserves all retained history/tests/logs/artifacts; interrupted/replayed transfer resumes without duplicates, byte drift or secret leakage | TeamCity simulator + full authoritative private capture rehearsal |
| S32 | R6 | Cumulative diff adds new, skips unchanged and previews changed entities; keep/replace choices are whole-entity, source cleanup never deletes imported history | Simulator deltas + captured data, stable lineage/hash reconciliation |
| S33 | R6 | TeamCity/Vivarium coexist on isolated workspaces and one trigger/publisher owner; final delta/switch/family rollback have an explicit write boundary | Real local family/client rehearsal + operator runbook |
| S34 | R6 | Candidate runs all accepted families and UI journeys after legacy stores/routes are removed; no hidden dependency on TeamCity survives | Full declared suite + offline source-independent historical reads |
| S35 | R1/R5 | An administrator edits a built-in/custom role and its includes, assigns scoped roles through nested groups, and delegates only within their project; unchanged cookie/token reflects the resulting inherited rights | Cheap inheritance tests + real PG policies + actual Access UI and unchanged TeamCity CLI; shared-pool and denied sibling mutations |
| S36 | R0.1 | Operator starts the prepared local Host/PG/Storage and a real Agent, enrolls/authorizes it normally, observes heartbeat and stable identity, attaches debugger and stops/resets only owned resources | Actual processes/PG/TLS/framework technical identity; no fake Agent or test-only management bypass |
| S37 | R0.2/R0.3 | Unchanged CLI queues/watches/cancels seeded LocalSmoke; native/compatible views share the run, raw output/artifact bytes are inspectable and bounded restart/reconnect preserves truthful ownership/outcome | Real CLI profile, Host/PG/Storage/Agent and executable; byte hashes plus restart and cleanup receipts |
| S38 | R2/R5 | Explicit suitable-agent diagnostic assignment can run on a connected/authorized disabled Agent while normal automatic queue assignment remains blocked and its disabled state is retained | Frozen targeted-run/permission wire contract, cheap admission tests plus actual Agent/API case; documentation input is not an executed reference pass |
| S39 | R0/R1/R6 | Prepared versus invalid resource startup; installation-password create/reset persistence/disclosure ordering, enabled/disabled/re-enabled sessions, reserved external identity, failed initialization, ordinary session/key continuity and incomplete restore | Actual Identity/PG/Chiral lifecycle composition, controlled resource failures and independent sink scan; real Host where startup is the claim |
| S40 | R1/R2/R4/R5/R6 | Reference-backed remove/re-enable lifecycles for accounts/groups/roles/catalog/agent; destructive consequences, retained attribution, revoked authority, restore failure and owned uninstall/erasure | Frozen compatible request/effect cases plus actual owning service/PG/Git boundaries; erasure only in disposable owned fixtures; unresolved profile variants cannot pass |
| S41 | R0/R1/R2/R4/R5 | Real panel journeys for initial login, grant, configure/run/watch/cancel, history/tests/output/files and agent views; protected direct links and interrupted live view | Existing Automation browser composition against actual Host/public API and real Agent for execution claims; no parallel feature copies or forced Depot layout |


Illustrative technical examples (canonical business scenarios live in `docs/acceptance/`; D34 owns the chosen installation-account model):

```gherkin
@access @current-credentials
Feature: Current project access
  Scenario: A removed developer cannot enqueue with an existing token
    Given an ordinary developer with access to project "Sample"
    And that developer has a valid Vivarium token
    When an administrator removes that project access through the Access page
    Then the unchanged token cannot enqueue a build in project "Sample"
    And the denial does not disclose sibling project configuration
```

```gherkin
@history @diff-import
Feature: Retained TeamCity history
  Scenario: Source cleanup does not remove imported build output
    Given a retained TeamCity build was imported with verified logs and artifacts
    When TeamCity cleanup removes the source build
    And I preview and apply a later diff-import
    Then the imported build, test history, logs and artifact bytes remain available in Vivarium
```

Evidence receipt: exact candidate commit, scenario/TC IDs, fixture/client/tool versions, prepared-input
fingerprint, expected vs actual outcome, output hashes where relevant, isolation and cleanup result.
Missing prerequisites are blocked/failed evidence, not skipped proof. Production data names and raw
credentials remain private; committed reports state safe counts and classifications only.
