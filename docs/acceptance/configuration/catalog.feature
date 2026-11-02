@business @configuration
Feature: Accepted build settings govern future work

  @AC-BLD-09
  Scenario: A settings change does not rewrite an earlier run
    Given a build has run with known configuration and source selections
    When an administrator changes the configuration for later builds
    Then the earlier build still shows the selections it actually used
    And later builds use only accepted configuration changes

  @AC-CFG-01
  Scenario: Create a usable build configuration
    Given an administrator may manage a project
    When the administrator creates a configuration with source, steps, parameters and agent requirements
    Then authorized developers can discover and run the accepted configuration
    And its settings are editable through the supported management clients

  @AC-CFG-02
  Scenario: Reject an invalid configuration without partial activation
    Given a working configuration is available
    When an administrator submits an invalid change
    Then the change is explained and is not activated
    And the previously accepted configuration remains identifiable and usable

  @AC-CFG-03 @extension
  Scenario: Expose a conflicting settings edit
    Given two administrators edit the same version of build settings in a workflow that explicitly checks for intervening changes
    When one accepted edit makes the other's proposal stale
    Then the stale edit is reported as conflicting rather than silently replacing the accepted settings

  @AC-CFG-04
  Scenario: Review and activate a settings revision
    Given an administrator proposes versioned build settings
    When the proposal is reviewed and accepted through the configured workflow
    Then the operator can distinguish proposed, accepted and active settings
    And subsequent builds identify the actual accepted settings they use

  @AC-CFG-05
  Scenario: Inherit and override template parameters
    Given configurations inherit shared steps or parameters from a template
    When an administrator changes the template and an allowed local override
    Then each configuration uses the resulting inherited and overridden values
    And its effective settings can be inspected before a build runs

  @AC-CFG-06
  Scenario: Rename without changing identity
    Given a project and configuration have retained builds and links
    When an administrator changes their display names
    Then the same objects, permissions and retained build history remain identifiable

  @AC-CFG-07
  Scenario: Use a secret without exposing its value
    Given a build configuration refers to a protected credential
    When an authorized build needs that credential
    Then the intended step can use it
    And ordinary settings views and audit records do not reveal its value

  @AC-CFG-08
  Scenario: Remove a build configuration with explicit consequences
    Given an administrator may remove a build configuration
    When the administrator completes its supported removal
    Then the removed configuration is not offered for new runs
    And affected waiting work, dependencies and retained history follow the agreed removal policy
    And the destructive consequences are visible rather than silently treated as a harmless rename

  @AC-CFG-09
  Scenario: Remove a project within the administrator's authority
    Given an administrator may remove a project and its affected contents
    When the administrator completes its supported removal
    Then the removed project is not offered for new work
    And affected child objects and history follow the agreed removal policy
    And an unrelated project is not removed or granted new access
