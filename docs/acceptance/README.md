# System Acceptance Criteria

Status: **31 specified business criteria; all currently specified-unbound for the replacement**.
These are requirements, not a test run or a claim that existing/new runtime behavior passed them.

The English Gherkin files here are the canonical highest-level acceptance text. They use product
language and stable AC-* tags. Internal design, wire formats and fixture/process choices belong to
the linked technical contracts and [testing model](../design/testing-model.md), not these scenarios.

| Feature | Business scope |
|---|---|
| [Builds](builds.feature) | Run/queue/cancel, source/settings meaning, logs, tests/triage/statistics, dependencies, retention and discovery |
| [Agents](agents.feature) | Registration/admission, independent availability, compatibility, restart truth and upgrades |
| [Access](access.feature) | Ordinary identity, project/group roles and current access withdrawal |
| [Automation](automation.feature) | Existing automation and extension-neutral base behavior |
| [Migration](migration.feature) | Retained history, explicit repeated import choices and coexistence |
| [Operations](operations.feature) | Local working installation and recovery of retained work |

The [tracked map](../../.workspace/workstreams/coherent-refactor/acceptance-map.tsv) connects every AC
to TCM invariants, existing TC requirements and S* candidate checks. The original 120 requirements
remain in force; mapping is not coverage or a scope reduction. Test-specific expected values come
from independent fixtures/reference contracts rather than product-generated snapshots.

Implementation must consume these exact feature files through the existing Automation/Reqnroll
consumer; do not fork them into a second maintained feature set. Only a real bound case with its
required components, meaningful assertions and recorded outcome can become accepted. Later criteria
remain openly unbound while earlier milestones prove their delivered subset. Parsing, an empty/
skipped run or placeholder step never passes the requirement.

Each milestone carries affected AC/TCM/wire proof plus the small cumulative implemented base suite
and relevant extension-neutrality checks. Existing actors/workflows and the oracle remain stable
through PostgreSQL/Chiral/UI changes. New functionality is an explicit TeamCity superset, not a new
meaning for an existing Build/Agent/role or an obligation to rewrite old clients.
