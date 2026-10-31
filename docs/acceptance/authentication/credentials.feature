@business @authentication
Feature: Automation credentials follow the owner and their lifecycle

  @AC-KEY-01
  Scenario: Issue and use an automation credential
    Given a user may create an automation credential
    When the user creates it using a supported client
    Then the credential can perform only its allowed actions within the user's current access
    And its secret is not available for later redisplay

  @AC-KEY-02
  Scenario: Revoke a credential already in use
    Given an automation credential can currently inspect and start builds
    When its owner or an authorized administrator revokes it
    Then the unchanged credential no longer permits those actions
    And other valid credentials are not silently revoked

  @AC-KEY-03
  Scenario: An expired credential cannot continue
    Given an automation credential has reached its agreed expiry
    When a client attempts to use it
    Then protected access is denied without granting a replacement credential implicitly

  @AC-KEY-04
  Scenario: Role removal affects the same credential
    Given an automation client can act in a project
    When an administrator removes the owner's required project grant
    Then the unchanged credential no longer permits that project action
    And access to unrelated projects is still governed by the owner's remaining grants

  @AC-KEY-05
  Scenario: Protect credentials during use
    Given a valid credential is used to request a build or download
    When the request completes or fails
    Then ordinary build output, navigation links and diagnostics do not disclose the credential
