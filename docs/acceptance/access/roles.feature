@business @access
Feature: Users receive only their current granted access

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

  @AC-ACC-05
  Scenario: Create and assign a custom role
    Given an administrator may manage roles and project grants
    When the administrator creates a role and assigns it in a project
    Then its intended members receive exactly those rights in that scope
    And unrelated project access is not added

  @AC-ACC-06
  Scenario: Remove group membership
    Given a person receives a project role only through group membership
    When an authorized administrator removes that membership
    Then the person's next protected action uses the resulting current access
    And the old signed-in session does not preserve the removed grant

  @AC-ACC-07
  Scenario: Change a nested group's grants
    Given users inherit access through nested groups
    When an authorized administrator changes an included group's role assignment
    Then affected users receive the resulting effective access
    And independently granted rights remain effective

  @AC-ACC-08
  Scenario: Remove a role assignment
    Given a user has several independent grants
    When an administrator removes one role assignment
    Then only rights no longer provided by any remaining grant disappear

  @AC-ACC-09
  Scenario: Disable an ordinary account
    Given a person has a signed-in session and an automation credential
    When an authorized administrator disables the account
    Then neither can continue protected actions as that account
    And a new provider sign-in does not silently re-enable or replace it

  @AC-ACC-10
  Scenario: Preserve attribution after access changes
    Given a user has started builds and performed administrative actions
    When the user's name or access later changes
    Then retained build and audit history still identify the original actor
    And the history does not give that actor new current access

  @AC-ACC-11
  Scenario: Re-enable an ordinary account deliberately
    Given an administrator has disabled an ordinary account
    When an authorized administrator re-enables it
    Then its owner can sign in to that same account under the current sign-in policy
    And access follows the account's current grants rather than a newly invented administrator grant

  @AC-ACC-12
  Scenario: Remove an ordinary account through the supported account lifecycle
    Given an administrator may remove an ordinary account
    When the administrator completes its supported removal
    Then that removed account cannot continue protected actions
    And retained historical actions remain attributable without granting the former account current access

  @AC-ACC-13
  Scenario: Remove a custom role and its grants
    Given an administrator may remove a custom role and address its assignments
    When the administrator completes the supported role removal
    Then users no longer receive rights from that removed role
    And independently granted rights remain governed by their remaining assignments

  @AC-ACC-14
  Scenario: Remove a group from access management
    Given an administrator may remove a group and address its memberships and assignments
    When the administrator completes the supported group removal
    Then users no longer receive rights through that removed group
    And retained historical actions are not reassigned to another user
