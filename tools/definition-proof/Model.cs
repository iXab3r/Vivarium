namespace DefinitionProof;

// P0 representability candidate; not the public SDK or a second product catalog.
public sealed record Binding(string Repository, string Path, string Branch, string TrustReference, string CredentialReference);
public sealed record Project(string Id, string Parent, string Name, string Description, Binding? Binding);
public sealed record Parameter(string Name, string? Literal, string? Expression);
public sealed record Step(string Id, string Name, string Runner, Parameter[] Properties);
public sealed record Requirement(string Name, string Value);
public sealed record Snapshot(string Configuration, string Failure, string Cancel);
public sealed record Artifact(string Configuration, string Selector, string Rules, bool CleanDestination);
public sealed record Trigger(string Type, string BranchFilter, int QuietSeconds);
public sealed record Source(string RootId, string Url, string Branch, string CheckoutRules);
public sealed record Configuration(string Id, string Name, Parameter[] Parameters, Step[] Steps,
    Requirement[] Requirements, Snapshot[] Snapshots, Artifact[] Artifacts, int TimeoutMinutes, Trigger[] Triggers);
public sealed record Settings(string ProjectId, Source? Source, Parameter[] Parameters, Configuration[] Configurations);
public sealed record ResolvedParameter(string Name, string Value, bool Inherited);
