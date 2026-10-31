@business @agents
Feature: Operators manage agents without changing build meaning

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

  @AC-AG-06
  Scenario: Explicit diagnostic assignment remains possible on a disabled agent
    Given a connected, authorized and idle disabled agent is suitable for a diagnostic build
    When an authorized operator explicitly assigns that build to the agent
    Then the diagnostic build can run without enabling ordinary automatic assignment
    And the agent remains disabled for ordinary queued work
