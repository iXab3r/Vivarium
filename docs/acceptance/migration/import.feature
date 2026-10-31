@business @migration
Feature: Operators retain their history while moving between systems

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

  @AC-MIG-05
  Scenario: Preview migration without activating it
    Given an operator has selected the source projects and retained history
    When the operator previews an import
    Then supported entities, differences, missing prerequisites and unsupported items are identified
    And preview alone changes neither server's configurations or automatic work

  @AC-MIG-06
  Scenario: Resume an interrupted import
    Given an import was interrupted after transferring some retained builds and files
    When an operator retries the supported import
    Then already accepted objects are reconciled without duplication
    And incomplete or unverifiable content is not declared successfully imported

  @AC-MIG-07
  Scenario: Bring in new history while coexisting
    Given a previous import completed and TeamCity has since produced new builds
    When an operator applies a later explicit import
    Then the new history is added alongside the existing imported history
    And native Vivarium builds retain their own identity and provenance

  @AC-MIG-08
  Scenario: Prove that the old server may be retired
    Given the declared configuration families and retained history have been migrated
    When the operator rehearses the cutover with the old server unavailable
    Then required build workflows and retained history remain usable
    And any unresolved migration gap prevents declaring the retirement complete
