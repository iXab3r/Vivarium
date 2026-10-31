@business @operations
Feature: Operators can try a working installation

  @AC-OPS-01
  Scenario: Try a local server and agent with a real build
    Given an operator has prepared the supported local installation
    When the operator starts the server, connects and authorizes an agent, and runs the sample build
    Then the operator can inspect its actual result and retrieve its produced file
    And the operator can stop or reset only the local installation's resources
