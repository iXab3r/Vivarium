@business @builds
Feature: Developers run builds and understand their results

  @AC-BLD-01
  Scenario: Run a configuration with the selected source and parameters
    Given a developer may run a build configuration
    When the developer starts it with a chosen source version and input values
    Then the build uses those selections
    And the developer can see its outcome and retrieve its produced files

  @AC-BLD-02
  Scenario: Work waits for a suitable available agent
    Given one requested build has no suitable available agent
    And another requested build has a suitable available agent
    When the system considers the waiting work
    Then the first build remains waiting with an understandable reason
    And the second build can run

  @AC-BLD-03
  Scenario: Cancel work before it starts
    Given a developer has a build waiting to start
    When the developer cancels that build
    Then it is recorded as cancelled
    And its work does not start later

  @AC-BLD-04
  Scenario: Stop a running build truthfully
    Given a developer has a running build
    When the developer requests that it stop
    Then the developer can distinguish a stop request from completed cancellation
    And the build is not presented as successfully stopped while its work is still running

  @AC-BLD-05
  Scenario: Inspect build output during and after execution
    Given a build produces diagnostic output
    When an authorized developer opens the build during execution and after it finishes
    Then the output remains available in order
    And any unavailable portion is clearly identified

  @AC-BLD-06
  Scenario: Follow the same test across builds
    Given the same test has passed, failed and been ignored in different builds
    When a developer opens its history
    Then the developer can compare those outcomes, durations and failure details
    And an ignored or unavailable result is not presented as a passing test

  @AC-BLD-07
  Scenario: Downstream work uses the selected upstream results
    Given a release requires successful compile and test builds
    When the release chain runs
    Then downstream work uses the selected upstream outputs
    And a failed required upstream build prevents publication

  @AC-BLD-08
  Scenario: Preserve a pinned release through cleanup
    Given an operator has pinned a retained release
    When ordinary build cleanup runs
    Then that release and its retained output, test results and files remain available

  @AC-BLD-09
  Scenario: A settings change does not rewrite an earlier run
    Given a build has run with known configuration and source selections
    When an administrator changes the configuration for later builds
    Then the earlier build still shows the selections it actually used
    And later builds use only accepted configuration changes

  @AC-BLD-10
  Scenario: Triage a known failure without hiding its original result
    Given an authorized developer is investigating a recurring failure
    When the developer records an investigation or mutes the known failure
    Then the selected triage decision is visible in its intended scope
    And the original failed result remains inspectable

  @AC-BLD-11
  Scenario: Compare build statistics over time
    Given a configuration has retained builds with reported measurements
    When a developer compares those builds
    Then the developer can inspect their durations and reported measurements
    And missing measurements are distinguishable from zero values

  @AC-BLD-12
  Scenario: Find a retained build by its familiar attributes
    Given a project contains retained builds from different branches and agents
    When a developer searches by build number, branch, outcome, tag, pin or agent
    Then the matching builds can be found and opened directly
    And renaming the configuration does not lose their history

  @AC-BLD-13
  Scenario Outline: Start work from a configured automatic rule
    Given a configuration has an automatic start rule for a <matching event>
    When that event occurs
    Then the configuration starts one build according to the rule
    And unrelated events do not start that build

    Examples:
      | matching event                  |
      | source change on a chosen branch |
      | required upstream completion     |
      | scheduled time                   |
