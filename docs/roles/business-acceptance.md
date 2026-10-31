# Business Acceptance — system acceptance function role

## Mission

Own the highest-level system Acceptance Criteria under D37. Write expected behavior in English
business language before implementation decisions. [Acceptance features](../acceptance/README.md)
are canonical; [testing model](../design/testing-model.md) defines their relationship to lower contracts.

## Responsibilities

- Name the actor, relevant situation, action, useful observable outcome and forbidden outcome.
- Use project/configuration/build/agent/test/artifact/access language a product operator understands.
  Keep DB, Chiral, serializers, routes, tables, fences and internal class topology out of business AC.
- Preserve TeamCity base semantics; classify additional Vivarium capability as an explicit extension,
  never an unannounced change to a familiar base concept or permission.
- Define independent examples/expected outcomes and role/denial boundaries; preserve the originally
  reported defect instead of replacing it with an easier implementation check.
- Maintain stable AC IDs and mapping to model/wire/technical checks. Change an expectation only for
  an explicit product decision, not to turn an incomplete implementation green.
- Keep specified/unbound, implemented/executed and accepted states distinct. Parsed Gherkin, generated
  steps, mocked success or a screenshot do not establish system acceptance.

The role owns intent/oracle, not fixture topology, HTTP DTOs, SQL, test-runner selection or domain
implementation. TeamCity/REST owners specify base contracts; Testing Architect selects evidence;
Test Steward implements/executes bindings and reports actual results. A UI action is tested through
UI when that interaction is the claim; business language alone does not require every case to be E2E.

## Review

Before accepting a milestone, check its user-visible outcome, affected existing AC and extension
neutrality, actual evidence and disclosed gaps. Ask whether a new architecture could satisfy the same
words; if not, an implementation decision has probably leaked into the requirement. Co-held roles
are allowed; existing independent review gates remain independent, without another approval workflow.
