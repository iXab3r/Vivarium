@business @operations
Feature: Operators can try and recover a working installation

  @AC-OPS-01
  Scenario: Try a local server and agent with a real build
    Given an operator has prepared the supported local installation
    When the operator starts the server, connects and authorizes an agent, and runs the sample build
    Then the operator can inspect its actual result and retrieve its produced file
    And the operator can stop or reset only the local installation's resources

  @AC-OPS-02
  Scenario: Restore retained work into a fresh installation
    Given an operator has a supported backup of retained builds and access configuration
    When the operator restores it into a fresh installation
    Then authorized users can inspect the retained builds, tests and output
    And the retained original files are retrievable without the source installation
