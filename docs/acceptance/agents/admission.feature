@business @agents
Feature: Operators distinguish agent admission and availability

  @AC-AG-01
  Scenario: Connection alone does not permit an agent to run builds
    Given a new agent is connected but not authorized
    When work is waiting
    Then the agent is visible to the operator but does not receive that work
    And the operator can distinguish connection, authorization, enablement and current activity

  @AC-AG-02
  Scenario: Disabling an agent lets its current build finish
    Given an authorized agent is running a build
    When an operator disables that agent for new work
    Then its current build may finish
    And ordinary queued work is not assigned automatically while the agent remains disabled

  @AC-AG-03
  Scenario: Explain why an agent cannot run a configuration
    Given a configuration requires capabilities that an agent does not provide
    When an operator inspects that configuration and agent
    Then the missing requirements are explained
    And the build is not sent to that incompatible agent

  @AC-AG-06
  Scenario: Explicit diagnostic assignment remains possible on a disabled agent
    Given a connected, authorized and idle disabled agent is suitable for a diagnostic build
    When an authorized operator explicitly assigns that build to the agent
    Then the diagnostic build can run without enabling ordinary automatic assignment
    And the agent remains disabled for ordinary queued work

  @AC-AG-07
  Scenario: Authorize a registered agent
    Given an operator can inspect a connected unauthorized agent
    When an authorized administrator approves it
    Then it may receive compatible work according to its remaining availability and policy
    And approval alone does not make an incompatible or busy agent suitable

  @AC-AG-08
  Scenario: Remove agent authorization
    Given an agent is authorized for builds
    When an authorized administrator removes its authorization
    Then it does not receive new work while unauthorized
    And its current work and resulting availability are shown truthfully

  @AC-AG-09
  Scenario: Restrict an agent pool to its projects
    Given an agent belongs to a pool available to selected projects
    When work from an allowed project and an unrelated project is considered
    Then only work permitted by the pool's project assignments can use it
    And ordinary agent capability requirements still apply

  @AC-AG-10
  Scenario: Change reported and operator-supplied properties independently
    Given an agent reports machine capabilities and an operator supplies custom properties
    When the agent reconnects or the operator edits its properties
    Then reported facts and deliberate operator values remain distinguishable
    And compatibility is calculated from the accepted effective properties
