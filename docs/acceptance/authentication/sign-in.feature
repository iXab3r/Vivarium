@business @authentication
Feature: People sign in without acquiring unintended authority

  @AC-ACC-01
  Scenario: Ordinary sign-in does not make the first visitor an administrator
    Given an installation has no ordinary user grants
    When a person signs in through a trusted provider for the first time
    Then the person obtains an ordinary account without implicit administration
    And an installation operator can grant that account ordinary administrative access

  @AC-AUTH-01
  Scenario: Repeated sign-in returns to the same account
    Given a person has an ordinary account linked to a trusted sign-in provider
    When the same person signs in again through that provider
    Then the same account and its current grants are used
    And another account is not created by a repeated or concurrent sign-in

  @AC-AUTH-02
  Scenario: An email address is not proof of account ownership
    Given two different provider identities report the same email address
    When their owners sign in
    Then the identities are not silently merged or given each other's grants

  @AC-AUTH-03
  Scenario: Reject an untrusted or incomplete sign-in
    Given a person has not completed a valid sign-in through a trusted provider
    When that person attempts to finish signing in
    Then no authenticated access is granted
    And the failure does not disclose credentials or grant administrative rights

  @AC-AUTH-04
  Scenario: Provider failure does not invent a login
    Given the ordinary sign-in provider is unavailable
    When a person attempts a new ordinary sign-in
    Then the person sees that sign-in could not complete
    And existing ordinary accounts and grants are not replaced with a shared fallback identity

  @AC-AUTH-05
  Scenario: Sign out locally
    Given a person has signed in and also has a separate automation credential
    When the person signs out of Vivarium
    Then that browser session no longer permits protected actions
    And the automation credential and provider session are not represented as revoked by that local sign-out

  @AC-AUTH-06
  Scenario: Return safely after sign-in
    Given a person opens a protected Vivarium page while signed out
    When the person completes sign-in
    Then the person can return to an allowed local destination
    And a supplied return destination cannot redirect the sign-in to an untrusted site
