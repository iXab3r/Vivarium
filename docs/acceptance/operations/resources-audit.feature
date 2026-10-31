@business @operations
Feature: Operators change resources and investigate actions safely

  @AC-OPS-03
  Scenario: Distinguish saved resource settings from active settings
    Given a resource setting requires a restart to take effect
    When an operator saves a valid replacement
    Then the operator can distinguish the saved setting from the active one
    And the replacement is used after the documented restart

  @AC-OPS-04
  Scenario: Replace a private resource credential
    Given an operator may manage a required service credential
    When the operator replaces it through the supported configuration experience
    Then the new value can be used without redisplaying the stored secret
    And ordinary users cannot retrieve it from configuration views or diagnostics

  @AC-OPS-05
  Scenario: Investigate an administrative change
    Given an authorized actor changes access, build settings or agent authorization
    When an administrator reviews the action history
    Then the actor, target, outcome and relevant accepted settings revision can be established
    And the record does not reveal passwords or protected credential values

  @AC-OPS-06
  Scenario: Recover the installation's access and private dependencies
    Given an operator has a complete supported backup including private installation material
    When the operator restores it with the documented procedure
    Then accounts, grants and still-valid credentials work according to their validity rules
    And missing required backup material is reported rather than silently replaced with unrelated identity

  @AC-OPS-07
  Scenario: Reject an incomplete or corrupt restore
    Given a proposed backup is missing required retained content or private installation material
    When an operator validates or attempts to restore it
    Then the missing or invalid material is identified
    And the installation does not declare recovery complete or silently fabricate that material

  @AC-OPS-08
  Scenario: Uninstall software while retaining the installation's data
    Given an operator chooses the supported uninstall procedure that retains installation data
    When the operator removes the software
    Then the retained data and private dependencies remain available for a supported reinstall
    And unrelated applications and resources are not removed

  @AC-OPS-09
  Scenario: Erase installation data only through an explicit destructive procedure
    Given an operator intends to permanently remove an installation's owned data
    When the operator performs the documented destructive procedure for the selected resources
    Then its scope and irreversible consequences are explicit
    And unrelated or shared resources outside that selected scope are not erased
