using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Vivarium.Controller.Security;

/// <summary>
/// Bearer tokens (D4). Scopes are minimal in Phase 0: the admin token and per-agent tokens.
/// Enroll tokens only gate appearing in the unauthorized list; authorization is the real gate.
/// </summary>
public sealed class TokenStore
{
    private readonly ConcurrentDictionary<string, string> agentTokens = new();  // token -> agentId
    private readonly ConcurrentDictionary<string, byte> enrollTokens = new();

    public string AdminToken { get; }

    public TokenStore(string dataDir)
    {
        var path = Path.Combine(dataDir, "admin.token");
        if (File.Exists(path))
        {
            AdminToken = File.ReadAllText(path).Trim();
        }
        else
        {
            AdminToken = NewToken();
            File.WriteAllText(path, AdminToken);
        }
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public string CreateEnrollToken()
    {
        var token = NewToken();
        enrollTokens[token] = 0;
        return token;
    }

    public bool ConsumeEnrollToken(string token) => enrollTokens.TryRemove(token, out _);

    public string IssueAgentToken(string agentId)
    {
        var token = NewToken();
        agentTokens[token] = agentId;
        return token;
    }

    public bool TryGetAgentByToken(string token, out string agentId)
    {
        if (agentTokens.TryGetValue(token, out var id))
        {
            agentId = id;
            return true;
        }

        agentId = string.Empty;
        return false;
    }

    public bool IsValidBearer(string token) => token == AdminToken || agentTokens.ContainsKey(token);
}
