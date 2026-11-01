using System.Net;
using System.Text;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

// Installed schema + documented explicit-input semantics. No live reference mutation was made.
internal static class ProjectCreationContract
{
    public const string Id = "TCREST-PROJECT-CREATE-READBACK-JSON-001";
    public const string ProjectId = "VivariumP0Project";
    public const string ProjectName = "Vivarium P0 project";
    public const string CreatePath = "/app/rest/projects";
    public const string ReadPath = "/app/rest/projects/id:VivariumP0Project?fields=id%2Cname";
    public const string Body = "{\"id\":\"VivariumP0Project\",\"name\":\"Vivarium P0 project\",\"parentProject\":{\"locator\":\"id:_Root\"}}";
    public const string ReadBody = "{\"id\":\"VivariumP0Project\",\"name\":\"Vivarium P0 project\"}";

    public static async Task ExecuteAsync(HttpClient http)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, CreatePath)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        create.Headers.Accept.ParseAdd("application/json");
        using var created = await http.SendAsync(create);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "installed addProject contract: explicit project creation");
        Assert.That(created.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        }
        catch (JsonException)
        {
            throw new AssertionException("project creation returned malformed JSON");
        }
        using var createdBody = document;
        Assert.That(createdBody.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "no default POST fields are assumed from schema properties without a required list");

        using var read = new HttpRequestMessage(HttpMethod.Get, ReadPath);
        read.Headers.Accept.ParseAdd("application/json");
        using var observed = await http.SendAsync(read);
        await RestResponseAssert.MatchesAsync(observed, new RestExpectation(
            HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(ReadBody)));
    }
}
