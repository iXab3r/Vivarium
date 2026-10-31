@business @builds
Feature: Developers start, wait for and stop builds

  @AC-BLD-01
  Scenario: Run a configuration with the selected source and parameters
    Given a developer may run a build configuration
    When the developer starts it with a chosen source version and input values
    Then the build uses those selections
    And the developer can see its outcome and retrieve its produced files

  @AC-BLD-02
  Scenario: Work waits for a suitable available agent
    Given one requested build has no suitable available agent
    And another requested build has a suitable available agent
    When the system considers the waiting work
    Then the first build remains waiting with an understandable reason
    And the second build can run

  @AC-BLD-03
  Scenario: Cancel work before it starts
    Given a developer has a build waiting to start
    When the developer cancels that build
    Then it is recorded as cancelled
    And its work does not start later

  @AC-BLD-04
  Scenario: Stop a running build truthfully
    Given a developer has a running build
    When the developer requests that it stop
    Then the developer can distinguish a stop request from completed cancellation
    And the build is not presented as successfully stopped while its work is still running

  @AC-RUN-01
  Scenario: Report a source preparation failure
    Given a build requires an unavailable or inaccessible selected source
    When the build attempts to prepare its work
    Then the source preparation failure and its reason are visible
    And no different source version is silently substituted

  @AC-RUN-02
  Scenario: Apply step conditions to the actual result
    Given a configuration has ordered steps and conditions for running them
    When an earlier step fails
    Then later steps follow their configured conditions
    And the final outcome preserves the relevant failures and executed-step results

  @AC-RUN-03
  Scenario: Retry as another attributable run
    Given a completed build can be run again
    When a developer requests another run with chosen inputs
    Then another build is recorded with its actual inputs and result
    And the earlier build's history is not rewritten

  @AC-RUN-04
  Scenario: Apply shared resource limits
    Given two builds require the same exclusively held resource
    When both become otherwise eligible
    Then their use respects the configured limit
    And cancellation or completion makes the released capacity available again
