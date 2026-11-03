using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Antiforgery;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.TeamCity;

/// <summary>Compatible representation and credential boundary; authority remains in application services.</summary>
internal static class TeamCityProtocol
{
    internal static string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'+0000'", CultureInfo.InvariantCulture);

    internal static async Task<IResult> ExecuteAsync(HttpContext http,
        Func<ManagementRequestContext, Task<IResult>> action, bool mutation = false, string? deniedMessage = null)
    {
        var user = await http.RequestServices.GetRequiredService<RuntimeIdentity>().CurrentAsync(http);
        if (user is null)
            return Results.Text("Could not authenticate with provided token\n", "text/plain;charset=UTF-8", statusCode: 401);
        try
        {
            var bearer = http.Request.Headers.ContainsKey("Authorization");
            if (mutation && !bearer) await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            return await action(http.RequestServices.GetRequiredService<RuntimeRequestContext>()
                .ForUser(user.Id, bearer ? "openid-bearer" : "identity-cookie", "teamcity"));
        }
        catch (AccessPermissionException)
        {
            return Error(http, 403, deniedMessage ?? "Current permissions do not allow this action");
        }
        catch (AntiforgeryValidationException) { return Error(http, 400, "Request verification failed"); }
        catch (JsonException) { return Error(http, 400, "Invalid JSON request"); }
        catch (BadHttpRequestException) { return Error(http, 400, "Invalid request"); }
        catch (TeamCityPermissionException error) { return Error(http, 403, error.Message); }
        catch (CatalogBusyException) { return Error(http, 409, "The catalogue is busy"); }
        catch (ConfigurationRepositoryException) { return Error(http, 503, "The configuration repository is unavailable"); }
        catch (RestApiException error) { return Error(http, error.Status, error.Message); }
    }

    internal static IResult Error(HttpContext http, int status, string message)
    {
        var text = status switch { 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
        var statusText = $"Responding with error, status code: {status} ({text}).";
        var additional = status switch
        {
            403 => "jetbrains.buildServer.serverSide.auth.AccessDeniedException: " + message + ".",
            400 => "jetbrains.buildServer.server.rest.errors.BadRequestException: " + message + ".",
            _ => message,
        };
        if (JsonRequested(http))
            return Results.Json(new { errors = new[] { new { statusText, additionalMessage = additional, stackTrace = (string?)null, message } } }, statusCode: status);
        var xml = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement("errors", new XElement("error",
            new XElement("message", message), new XElement("additionalMessage", additional), new XElement("statusText", statusText))));
        return new XmlResult(XmlBytes(xml), status);
    }

    internal static IResult Entity(HttpContext http, string name, JsonObject value, bool applyFields = true)
    {
        if (applyFields && http.Request.Query.TryGetValue("fields", out var fields))
        {
            if (fields.Count != 1 || string.IsNullOrWhiteSpace(fields[0]) || fields[0]!.Length > 4096)
                throw InvalidFields();
            var position = 0;
            var selection = ParseFields(fields[0]!, ref position, 0);
            if (position != fields[0]!.Length) throw InvalidFields();
            value = Select(value, selection);
        }
        if (JsonRequested(http)) return Results.Json(value);
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), Xml(name, value));
        return Results.Bytes(XmlBytes(document), "application/xml");
    }

    private static byte[] XmlBytes(XDocument document)
    {
        using var bytes = new MemoryStream();
        bytes.Write(Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"));
        using (var writer = XmlWriter.Create(bytes, new XmlWriterSettings { Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true, Indent = true, IndentChars = "    ", NewLineChars = "\n", CloseOutput = false })) document.Save(writer);
        bytes.WriteByte((byte)'\n');
        return bytes.ToArray();
    }

    private sealed class XmlResult(byte[] body, int status) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = status; http.Response.ContentType = "application/xml";
            http.Response.ContentLength = body.Length;
            await http.Response.Body.WriteAsync(body, http.RequestAborted);
        }
    }

    private static bool JsonRequested(HttpContext http) => http.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
    private static RestApiException InvalidFields() => new(400, "invalid_fields", "Invalid fields", "Invalid fields selection");

    private sealed record Field(string Name, List<Field>? Children);
    private static List<Field> ParseFields(string text, ref int position, int depth)
    {
        if (depth > 16) throw InvalidFields();
        var fields = new List<Field>();
        while (position < text.Length)
        {
            var start = position;
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] is '_' or '*')) position++;
            if (position == start) throw InvalidFields();
            var name = text[start..position];
            List<Field>? children = null;
            if (position < text.Length && text[position] == '(')
            {
                position++; children = ParseFields(text, ref position, depth + 1);
                if (position >= text.Length || text[position++] != ')') throw InvalidFields();
            }
            fields.Add(new(name, children));
            if (position == text.Length || text[position] == ')') break;
            if (text[position++] != ',' || position == text.Length) throw InvalidFields();
        }
        return fields;
    }

    private static JsonObject Select(JsonObject value, List<Field> fields)
    {
        var result = fields.Any(f => f.Name == "*") ? (JsonObject)value.DeepClone() : new JsonObject();
        foreach (var field in fields)
        {
            if (!value.TryGetPropertyValue(field.Name, out var item)) continue;
            result[field.Name] = field.Children is null ? item?.DeepClone() : item switch
            {
                JsonObject child => Select(child, field.Children),
                JsonArray array => new JsonArray(array.Select(i => i is JsonObject child ? Select(child, field.Children) : i?.DeepClone()).ToArray()),
                _ => item?.DeepClone(),
            };
        }
        return result;
    }

    private static XElement Xml(string name, JsonObject value)
    {
        var element = new XElement(name);
        foreach (var property in value)
        {
            switch (property.Value)
            {
                case JsonObject child: element.Add(Xml(property.Key, child)); break;
                case JsonArray children:
                    foreach (var child in children.OfType<JsonObject>()) element.Add(Xml(property.Key, child));
                    break;
                case JsonValue scalar:
                    element.SetAttributeValue(property.Key, scalar.ToJsonString().StartsWith('"') ? scalar.GetValue<string>() : scalar.ToJsonString());
                    break;
            }
        }
        return element;
    }
}
