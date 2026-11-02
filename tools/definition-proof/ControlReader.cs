using System.Text.Json;

// Read the control declaration before selecting a product binding; no product code is loaded here.
Console.Write(JsonSerializer.Serialize(Control.Declare(), new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
}));
