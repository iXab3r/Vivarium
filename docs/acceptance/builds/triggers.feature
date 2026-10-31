@business @builds
Feature: Configured events start the intended work

  @AC-BLD-13
  Scenario Outline: Start work from a configured automatic rule
    Given a configuration has an automatic start rule for a <matching event>
    When that event occurs
    Then the configuration starts one build according to the rule
    And unrelated events do not start that build

    Examples:
      | matching event                  |
      | source change on a chosen branch |
      | required upstream completion     |
      | scheduled time                   |
