# System Acceptance Criteria

Status: **110 specified business criteria in 11 subsystem folders; all specified-unbound for the replacement**.
These are requirements, not a test run or a claim that existing/new runtime behavior passed them.

The English Gherkin files here are the canonical highest-level acceptance text. They use product
language and stable AC-* tags. Internal design, wire formats and fixture/process choices belong to
the linked technical contracts and [testing model](../design/testing-model.md), not these scenarios.

| Feature | Business scope |
|---|---|
| [Installation](installation/server.feature) | Prepare, start, repair settings, restart, stop and reject incompatible stored data |
| [Ordinary authentication](authentication/sign-in.feature) | First/repeated login, distinct identities, failure, logout and safe return navigation |
| [Installation superuser](authentication/installation-account.feature) | Enable, initialize, rotate, sign in, delegate administration, disable, re-enable and recover prerequisites |
| [Automation credentials](authentication/credentials.feature) | Issue, use, expire, revoke, protect and reflect current rights |
| [Access](access/roles.feature) | Scoped/nested grants, role edits, membership/grant removal, account disablement and historical attribution |
| [Configuration](configuration/catalog.feature) | Create, validate, conflict, review/activate, inherit, rename and protect secrets |
| [Build execution](builds/execution.feature) | Run, queue, cancel, source/step failures, retry and shared limits |
| [Dependencies](builds/dependencies.feature) and [triggers](builds/triggers.feature) | Selected upstream outputs and configured automatic events |
| [Agent admission](agents/admission.feature) | Registration, authorization, enablement, requirements, pools and custom/reported properties |
| [Agent recovery](agents/recovery-maintenance.feature) | Disconnect, reconnect, restart, upgrade and failed upgrade |
| [Logs](results/logs.feature) | Live/history output, reconnect, explicit gaps and hostile/large output |
| [Tests](results/tests.feature) | History, attempts, malformed evidence, investigations and mutes |
| [Artifacts/retention](results/artifacts-retention.feature) and [discovery](results/discovery.feature) | Actual bytes, pins, cleanup, isolation, history search and measurements |
| [Interface](interface/navigation.feature) | Familiar build/agent journeys, protected deep links and reconnect without repeated actions |
| [Automation](automation/compatibility.feature) | Unchanged clients and extension-neutral base behavior |
| [Migration](migration/import.feature) | Preview, initial/repeated/interrupted import, coexistence and retirement proof |
| [Local lab](operations/local-lab.feature), [backup](operations/backup-restore.feature), [resources/audit](operations/resources-audit.feature) | Real local installation, restore, saved/active settings, private credentials and attribution |

Folders follow business responsibility, not code projects. All 31 original AC identifiers survive
the move. The [lifecycle review](lifecycle-review.md) separates specified behavior from unresolved
variants: this is an initial broad baseline, not a claim that every TeamCity operation is decomposed.
Missing decomposition is work to do, not permission to drop the original requirements.

The [REST-driven gap review](rest-gap-review.md) compares the 110-AC baseline with all 30 discovered
public TeamCity API resource pages. It records 36 open decomposition findings and proposed business
outcomes. Public documentation discovery is distinct from frozen-version behavior and runtime proof;
these findings do not add duplicate features or mark existing criteria accepted.

[Reference handling and implementation notes](../design/teamcity-rest-recommendations.md) cover all
36 findings. Owner clarification requires adopted operations to follow TeamCity; public behavior is
not an independent design choice. Internal implementation options remain proposals and must meet
that contract. Unknown reference results still need independent evidence; no scope exemption is
adopted. AC-CFG-03 is explicitly a version-checked extension, not a universal TeamCity stale-edit rule.

D38 adds the [early executable REST gate](../design/teamcity-rest-test-first.md): source-backed
technical cases, strict replay controls and actual product RED precede structural replacement.
They support these business criteria rather than duplicate them. None is claimed executed today.

The [tracked map](../../.workspace/workstreams/coherent-refactor/acceptance-map.tsv) connects every AC
to milestones, TCM invariants, existing TC requirements, S* candidate checks and accepted contracts.
Native installation/identity/resource lifecycles cite D34/D35 without inventing unrelated TC links.
The original 120 requirements
remain in force; mapping is not coverage or a scope reduction. Test-specific expected values come
from independent fixtures/reference contracts rather than product-generated snapshots.

Implementation must consume these exact feature files recursively through the existing Automation/Reqnroll
consumer; do not fork them into a second maintained feature set. Only a real bound case with its
required components, meaningful assertions and recorded outcome can become accepted. Later criteria
remain openly unbound while earlier milestones prove their delivered subset. Parsing, an empty/
skipped run or placeholder step never passes the requirement.

Each milestone carries affected AC/TCM/wire proof plus the small cumulative implemented base suite
and relevant extension-neutrality checks. Existing actors/workflows and the oracle remain stable
through PostgreSQL/Chiral/UI changes. New functionality is an explicit TeamCity superset, not a new
meaning for an existing Build/Agent/role or an obligation to rewrite old clients.

For expected business behavior, start here and follow the current focused contracts. Numbered
architecture decisions govern adopted shape; code/tests describe current implementation. Older
examples cannot silently override this baseline. Reconcile conflicting documents in the same change
or record the unresolved decision; see [documentation governance](../design/documentation-governance.md).
