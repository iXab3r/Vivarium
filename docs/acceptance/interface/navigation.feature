@business @interface
Feature: People navigate the familiar build and agent workflows

  @AC-UI-01
  Scenario: Follow the build workflow through the panel
    Given a developer has access to a project and an operator has access to its agents
    When they use the panel to configure, start, inspect and stop work
    Then they can find the project's configurations, queue, build history, tests, output and files
    And the operator can inspect agent state, requirements and assigned work in the corresponding agent views

  @AC-UI-02
  Scenario: Open and share a resource link
    Given a person has a link to a project, configuration, agent, build or test history
    When the person opens it or returns after sign-in
    Then the intended resource and view can be opened if the person has access
    And the link itself does not grant access or disclose protected content

  @AC-UI-03
  Scenario: Recover a panel view after a connection interruption
    Given a person is watching a changing queue, agent or running build
    When the viewing connection is interrupted and later recovers
    Then the person can distinguish stale or disconnected information from current state
    And returning to the view does not start, cancel or repeat work implicitly
