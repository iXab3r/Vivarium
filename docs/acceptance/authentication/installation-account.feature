@business @authentication
Feature: An operator administers installation access deliberately

  @AC-SU-01
  Scenario: Start with installation access enabled
    Given an operator has enabled installation access
    When the prepared server starts successfully
    Then the operator can obtain a newly generated installation-account password from the intended private startup log
    And the account signs in through the normal sign-in experience with full installation administration

  @AC-SU-02
  Scenario: Replace the installation password on startup
    Given an installation-account password was issued at an earlier startup
    When the server starts successfully again with installation access enabled
    Then a fresh password is issued and the previous password cannot be used for a new sign-in
    And the new password is disclosed only after it has been saved successfully

  @AC-SU-03
  Scenario: Failed password initialization does not accept an older password
    Given an installation has an earlier installation-account password
    When the server cannot safely initialize the new startup password
    Then installation-account sign-in does not silently fall back to the earlier password
    And the operator receives a useful diagnostic without public disclosure of credentials

  @AC-SU-04
  Scenario: Disable installation access
    Given the installation account has an active signed-in session
    When an operator disables installation access and applies the setting as documented
    Then neither that session nor a new sign-in can exercise installation-account authority

  @AC-SU-05
  Scenario: Re-enable installation access
    Given an operator has disabled installation access
    When the operator re-enables it and restarts as required
    Then a fresh password is needed for a new installation-account sign-in
    And the earlier password is not restored

  @AC-SU-06
  Scenario: Keep the installation identity reserved
    Given an ordinary provider reports a name resembling the installation account
    When that person signs in
    Then the person does not acquire or link to the reserved installation identity
    And ordinary provider claims cannot confer installation-account authority

  @AC-SU-07
  Scenario: Delegate normal administration
    Given an ordinary person has signed in without administrative grants
    When the installation operator grants that account the standard system-administrator role
    Then that person can administer through their own ordinary account
    And the grant is attributable without sharing the installation password

  @AC-SU-08
  Scenario: Keep the startup password private
    Given an installation password has been issued
    When another user inspects ordinary status, support output, build logs and audit history
    Then the password is absent from those surfaces
    And only the intended private startup disclosure makes the password available to the operator

  @AC-SU-09
  Scenario: Unavailable account storage is not another authentication path
    Given the installation cannot use its account data
    When an operator attempts installation-account sign-in
    Then the installation does not grant administration through a separate fallback credential store
    And the operator can diagnose and repair the resource configuration locally
