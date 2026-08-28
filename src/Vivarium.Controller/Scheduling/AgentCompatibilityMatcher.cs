namespace Vivarium.Controller.Scheduling;

public sealed record AgentRequirement(string Parameter, string ExpectedValue);

public sealed record AgentCompatibilityResult(
    bool Compatible,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Evaluates the Phase 1 agent selector syntax from vivarium.yaml. Requirements are joined by AND,
/// matching TeamCity's agent-requirement semantics; Phase 1 deliberately supports equality only.
/// </summary>
public static class AgentCompatibilityMatcher
{
    private const string SupportedSyntax =
        "Only equality clauses joined by '&&' are supported in Phase 1.";

    public static AgentCompatibilityResult Match(
        string selector,
        string agentName,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(agentName);
        ArgumentNullException.ThrowIfNull(parameters);

        if (!TryParse(selector, out var requirements, out var parseError))
        {
            return new AgentCompatibilityResult(false, [parseError!]);
        }

        var reasons = new List<string>();
        foreach (var requirement in requirements)
        {
            if (requirement.Parameter == "name")
            {
                if (!string.Equals(agentName, requirement.ExpectedValue, StringComparison.Ordinal))
                {
                    reasons.Add(
                        $"Agent name is '{agentName}', expected '{requirement.ExpectedValue}'.");
                }

                continue;
            }

            if (!parameters.TryGetValue(requirement.Parameter, out var actualValue))
            {
                reasons.Add($"Agent does not report parameter '{requirement.Parameter}'.");
                continue;
            }

            if (!string.Equals(actualValue, requirement.ExpectedValue, StringComparison.Ordinal))
            {
                reasons.Add(
                    $"Parameter '{requirement.Parameter}' is '{actualValue}', expected '{requirement.ExpectedValue}'.");
            }
        }

        return new AgentCompatibilityResult(reasons.Count == 0, reasons);
    }

    public static bool TryParse(
        string selector,
        out IReadOnlyList<AgentRequirement> requirements,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            requirements = [];
            error = null;
            return true;
        }

        var parsed = new List<AgentRequirement>();
        var clauses = selector.Split("&&", StringSplitOptions.None);
        for (var index = 0; index < clauses.Length; index++)
        {
            var clause = clauses[index].Trim();
            var equalsIndex = clause.IndexOf("==", StringComparison.Ordinal);
            if (equalsIndex <= 0 ||
                clause.IndexOf("==", equalsIndex + 2, StringComparison.Ordinal) >= 0)
            {
                return Fail(
                    selector,
                    $"clause {index + 1} must have the form '<parameter> == <value>'",
                    out requirements,
                    out error);
            }

            var parameter = clause[..equalsIndex].Trim();
            var expectedValue = clause[(equalsIndex + 2)..].Trim();
            if (!IsParameterName(parameter))
            {
                return Fail(
                    selector,
                    $"clause {index + 1} has invalid parameter name '{parameter}'",
                    out requirements,
                    out error);
            }

            if (!IsValue(expectedValue))
            {
                return Fail(
                    selector,
                    $"clause {index + 1} has an invalid or empty value",
                    out requirements,
                    out error);
            }

            parsed.Add(new AgentRequirement(parameter, expectedValue));
        }

        requirements = parsed;
        error = null;
        return true;
    }

    private static bool Fail(
        string selector,
        string detail,
        out IReadOnlyList<AgentRequirement> requirements,
        out string? error)
    {
        requirements = [];
        error = $"Invalid agent selector '{selector}': {detail}. {SupportedSyntax}";
        return false;
    }

    private static bool IsParameterName(string value) =>
        value.Length > 0 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsValue(string value) =>
        value.Length > 0 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '@' or '/' or ':' or '+');
}
