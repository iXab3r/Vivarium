@business @agents
Feature: Agents retain truthful identity and work through maintenance

  @AC-AG-04
  Scenario: A server restart does not invent another accepted run
    Given an agent owns an accepted build
    When the server restarts and the agent reconnects
    Then the operator can still identify that same build and agent
    And uncertainty about the work is visible instead of a fabricated successful result
    And the agent is not presented as safely available for conflicting work until ownership is resolved

  @AC-AG-05
  Scenario: Upgrade an agent while preserving its identity and work
    Given an operator requests an agent upgrade while it is building
    When the supported upgrade completes
    Then the current build has finished before the agent is replaced
    And the operator can verify the returned agent version and stable identity

  @AC-AG-11
  Scenario: Disconnect during a build
    Given an agent is running an accepted build
    When its connection is lost
    Then users can distinguish lost contact from a confirmed terminal build result
    And the same agent's capacity is not offered for conflicting work while ownership is unresolved

  @AC-AG-12
  Scenario: Reconnect a registered agent
    Given an agent has an existing registered identity
    When it reconnects with its valid retained registration
    Then the operator sees the same agent and its history rather than another machine
    And a second machine cannot silently take over that identity

  @AC-AG-13
  Scenario: Fail an agent upgrade safely
    Given an agent has a working installed version
    When an attempted upgrade cannot start a healthy replacement
    Then the upgrade failure and the version actually running are visible
    And the operator can follow the supported recovery path without losing the agent's history

  @AC-AG-14
  Scenario: Retire an idle registered agent
    Given an idle registered agent can be removed by an authorized administrator
    When the administrator completes its supported removal
    Then the retired registration is no longer offered for new work
    And its old credential cannot silently restore authority to that retired registration
    And retained build history does not become the history of a different machine
