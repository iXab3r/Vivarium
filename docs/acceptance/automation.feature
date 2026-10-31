@business @automation
Feature: Existing automation remains usable as Vivarium gains capabilities

  @AC-AUTO-01
  Scenario: Existing TeamCity automation works without a client rewrite
    Given an operator has an existing TeamCity automation client
    When the operator points it to Vivarium and supplies Vivarium credentials
    Then the agreed discovery, configuration, run, inspection, cancellation and download workflows work
    And the client does not need to be rewritten for Vivarium

  @AC-AUTO-02
  Scenario: Additional fleet inspection preserves existing build workflows
    Given existing build automation and agent access work for a project
    When an operator enables additional Vivarium fleet inspection
    Then the same build automation and agent eligibility still work
    And the additional inspection requires its own intended access without changing existing grants
