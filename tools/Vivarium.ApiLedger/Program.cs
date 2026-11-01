using System.Text.Json;
using Vivarium.ApiLedger;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: Vivarium.ApiLedger <repository-root> <candidate-id> [assessment-file] (offline; reads evidence only)");
    return 2;
}
try
{
    var report = Ledger.Read(args[0], args[1], assessmentPath: args.Length == 3 ? args[2] : Ledger.Workstream + "api-evidence.json");
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    return report.Errors.Count == 0 ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or System.Xml.XmlException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
