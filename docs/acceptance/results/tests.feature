@business @results
Feature: Developers follow and investigate tests across builds

  @AC-BLD-06
  Scenario: Follow the same test across builds
    Given the same test has passed, failed and been ignored in different builds
    When a developer opens its history
    Then the developer can compare those outcomes, durations and failure details
    And an ignored or unavailable result is not presented as a passing test

  @AC-BLD-10
  Scenario: Triage a known failure without hiding its original result
    Given an authorized developer is investigating a recurring failure
    When the developer records an investigation or mutes the known failure
    Then the selected triage decision is visible in its intended scope
    And the original failed result remains inspectable

  @AC-TEST-01
  Scenario: Distinguish broken test reporting from a passing suite
    Given a build's expected test report is missing or cannot be read
    When the developer opens its results
    Then unavailable test evidence is clearly identified
    And the absence of readable tests is not presented as proof that all expected tests passed

  @AC-TEST-02
  Scenario: Preserve individual attempts
    Given a test is executed more than once in a build
    When the developer inspects that test's results
    Then the attempts and their actual outcomes remain distinguishable
    And a later passing attempt does not erase an earlier failure

  @AC-TEST-03
  Scenario: End an investigation or mute
    Given a known failure has an investigation or mute decision
    When an authorized developer changes or ends that decision
    Then its current triage state is visible in the intended scope
    And previous occurrences and original outcomes remain inspectable
