@business @results
Feature: Build output remains useful during and after a run

  @AC-BLD-05
  Scenario: Inspect build output during and after execution
    Given a build produces diagnostic output
    When an authorized developer opens the build during execution and after it finishes
    Then the output remains available in order
    And any unavailable portion is clearly identified

  @AC-LOG-01
  Scenario: Reconnect to live build output
    Given a developer is reading output from a running build
    When the viewing connection breaks and the developer returns
    Then retained output can be followed without confusing a repeated display with new build activity
    And any missing output is explicitly identified

  @AC-LOG-02
  Scenario: Large or hostile build output remains inspectable
    Given a build produces very large output including text resembling page controls
    When a developer views and searches the retained output
    Then the page remains usable and treats that output as build text
    And the output cannot act as the viewing user's commands
