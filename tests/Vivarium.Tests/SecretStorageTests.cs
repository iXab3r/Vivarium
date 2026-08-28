using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
public class SecretStorageTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(Path.GetTempPath(), "vivarium-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(rootDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [TestCase("")]
    [TestCase("ABCDEF")]
    [TestCase("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task Controller_rejects_a_malformed_persisted_token(string persistedToken)
    {
        var dataDir = Path.Combine(rootDir, "controller");
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "admin.token"), persistedToken);

        await using var database = new VivariumDatabase(dataDir);
        var exception = Assert.Throws<InvalidDataException>(() => new TokenStore(dataDir, database));

        Assert.That(exception!.Message, Does.Contain("exactly 48 hexadecimal characters"));
    }
}
