@business @results
Feature: Retained build files survive according to policy

  @AC-BLD-08
  Scenario: Preserve a pinned release through cleanup
    Given an operator has pinned a retained release
    When ordinary build cleanup runs
    Then that release and its retained output, test results and files remain available

  @AC-ART-01
  Scenario: Download the actual published file
    Given a retained build has published a file and a user may read it
    When the user downloads it through a supported client
    Then the returned content matches the retained original file
    And a similarly named file from another build is not substituted

  @AC-ART-02
  Scenario: Explain removed output
    Given a build's unpinned output was removed by an authorized retention policy
    When a developer opens the build or requests that output
    Then the removed content is distinguishable from content that never existed or is temporarily unavailable
    And the system does not fabricate an empty successful download

  @AC-ART-03
  Scenario: Do not expose another project's output
    Given a user cannot read a project
    When that user obtains a reference to one of its retained files
    Then the reference alone does not grant download access
