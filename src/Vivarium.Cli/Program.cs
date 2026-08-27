// viv — the Vivarium CLI. Phase 0 stub: the ControlPlane service it talks to lands in Phase 1
// (ARCHITECTURE §5); until then this exists so the artifact pipeline has all four binaries.
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
Console.WriteLine($"viv {version} — Vivarium CLI (Phase 0 stub; ControlPlane arrives in Phase 1)");
return 0;

internal sealed partial class Program;
