@business @builds
Feature: Downstream work consumes selected upstream results

  @AC-BLD-07
  Scenario: Downstream work uses the selected upstream results
    Given a release requires successful compile and test builds
    When the release chain runs
    Then downstream work uses the selected upstream outputs
    And a failed required upstream build prevents publication
