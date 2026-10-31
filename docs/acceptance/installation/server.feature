@business @installation
Feature: Operators bring an installation into and out of service

  @AC-INS-01
  Scenario: First start explains what is required
    Given an operator has installed the server but has not supplied its required resource settings
    When the operator starts it
    Then the operator can identify the missing settings and the supported way to provide them
    And the installation does not claim to be ready to accept builds

  @AC-INS-02
  Scenario: Start a prepared installation
    Given an operator has supplied valid settings and reachable required services
    When the operator starts the installation
    Then the operator can identify its address and whether it is ready for use
    And no ordinary visitor becomes its administrator by arriving first

  @AC-INS-03
  Scenario: Repair invalid resource settings
    Given an installation cannot start because a required resource setting is invalid
    When an operator repairs that setting using the supported local procedure and starts again
    Then the corrected setting is used and its readiness is visible
    And existing builds and accounts are preserved

  @AC-INS-04
  Scenario: Restart without creating another installation
    Given a working installation has users, registered agents and retained builds
    When an operator restarts it with the same retained data
    Then those users, agent identities and builds still belong to the same installation
    And ordinary valid access remains usable under its normal validity rules

  @AC-INS-05
  Scenario: Stop while work is active
    Given an installation has waiting and running builds
    When an operator performs a supported shutdown and later starts it again
    Then waiting and running work can be accounted for after recovery
    And work with an unknown outcome is not silently marked successful or repeated

  @AC-INS-06
  Scenario: Refuse an incompatible stored version safely
    Given an operator attempts to start a server version that cannot use the installation's stored data
    When the server checks that data
    Then the incompatibility is explained before incompatible changes are made
    And the operator can follow the documented recovery or upgrade procedure
