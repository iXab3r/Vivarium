@business @operations
Feature: Operators recover retained work

  @AC-OPS-02
  Scenario: Restore retained work into a fresh installation
    Given an operator has a supported backup of retained builds and access configuration
    When the operator restores it into a fresh installation
    Then authorized users can inspect the retained builds, tests and output
    And the retained original files are retrievable without the source installation
