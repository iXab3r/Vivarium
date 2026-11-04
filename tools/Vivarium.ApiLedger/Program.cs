using System.Text.Json;
using Vivarium.ApiLedger;

if (args.Length is < 2 or > 4 || args.Length == 4 && args[2] is not ("--r0" or "--pilot"))
{
    Console.Error.WriteLine("Usage: Vivarium.ApiLedger <repository-root> <candidate-id> [assessment-file | --r0 [assessment-file] | --pilot assessment-file] (offline; reads evidence only)");
    return 2;
}
try
{
    if (args.Length >= 3 && args[2] == "--pilot")
    {
        var pilot = Ledger.ReadPilot(args[0], args[1], args.Length == 4 ? args[3] : Ledger.Workstream + "pilot-api-evidence.json");
        Console.WriteLine(JsonSerializer.Serialize(pilot, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return pilot.Gaps.Count == 0 ? 0 : 1;
    }
    if (args.Length >= 3 && args[2] == "--r0")
    {
        var selected = Ledger.ReadR0(args[0], args[1], assessmentPath: args.Length == 4 ? args[3] : Ledger.Workstream + "r0-api-evidence.json");
        Console.WriteLine(JsonSerializer.Serialize(selected, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return selected.Errors.Count == 0 ? 0 : 1;
    }
    var report = Ledger.Read(args[0], args[1], assessmentPath: args.Length == 3 ? args[2] : Ledger.Workstream + "api-evidence.json");
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    return report.Errors.Count == 0 ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or System.Xml.XmlException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
