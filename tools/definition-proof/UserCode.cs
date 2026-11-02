namespace DefinitionProof;

public static class UserCode
{
    public static string Evaluate(string expression) => expression switch
    {
        "fixture-mode" => string.Join("", new[] { "computed", "-mode" }),
        _ => throw new InvalidOperationException("Unknown computed parameter: " + expression),
    };
}
