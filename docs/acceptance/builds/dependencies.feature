@business @builds
Feature: Downstream work consumes selected upstream results

  @AC-BLD-07
  Scenario: Downstream work uses the selected upstream results
    Given a release requires successful compile and test builds
    When the release chain runs
    Then downstream work uses the selected upstream outputs
    And a failed required upstream build prevents publication

  @AC-BLD-07 @US-013
  Scenario: Release explicitly consumes successful prior upstream results
    Given successful prepare and test results from a prior release attempt
    When an authorized operator selects those results for another release attempt
    Then release consumes their exact outputs and selected source revision
    And prepare and test do not execute again
    And the selected result identities survive a server restart
