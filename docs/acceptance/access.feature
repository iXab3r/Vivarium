@business @access
Feature: Users receive only their current granted access

  @AC-ACC-01
  Scenario: Ordinary sign-in does not make the first visitor an administrator
    Given an installation has no ordinary user grants
    When a person signs in through a trusted provider for the first time
    Then the person obtains an ordinary account without implicit administration
    And an installation operator can grant that account ordinary administrative access

  @AC-ACC-02
  Scenario: Project and group roles grant access in their intended scope
    Given a person receives build access through a group in one project
    When the person works with that project, its children and an unrelated project
    Then the person's effective access follows the project's and group's inherited roles
    And unrelated resources remain inaccessible without another grant

  @AC-ACC-03
  Scenario: Removing access affects an already signed-in user
    Given a person can inspect and run builds in a project
    When an administrator removes that access
    Then the person cannot continue those actions without another grant
    And signing in again is not required for the removal to take effect

  @AC-ACC-04
  Scenario: Role edits change the rights of their assignees
    Given an administrator manages a role used by project members
    When the administrator changes its permissions or included roles
    Then assignees receive the resulting rights in the assigned scopes
    And a project administrator cannot use that edit to administer an unrelated project
