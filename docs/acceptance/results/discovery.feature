@business @results
Feature: Developers find builds and compare their measurements

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
