@business @migration
Feature: Operators retain their build history while moving between systems

  @AC-MIG-01
  Scenario: Imported history remains useful without the old server
    Given retained builds were imported with their tests, output and produced files
    When the old TeamCity server is unavailable
    Then authorized users can inspect that history and retrieve the retained original files in Vivarium

  @AC-MIG-02
  Scenario: Repeated import uses an explicit whole-entity choice
    Given a previously imported configuration now differs between TeamCity and Vivarium
    When an operator previews another import and chooses which complete version to keep
    Then that choice is applied and recorded
    And unchanged entries do not create duplicate configurations or builds

  @AC-MIG-03
  Scenario: Old-server cleanup does not delete imported history
    Given a retained build was imported into Vivarium
    When TeamCity cleanup removes it and an operator later imports new changes
    Then its imported history, output, test results and retained files remain available

  @AC-MIG-04
  Scenario: Coexistence does not silently enable another publisher
    Given TeamCity and Vivarium coexist with an explicit owner for automatic runs and publication
    When an operator imports configuration changes
    Then the import does not silently activate another trigger or publisher
    And the operator can see which system owns that work
