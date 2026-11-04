using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.AspNetCore.DataProtection;
using Vivarium.Contracts.V1;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Private per-run recovery material. This is not a project secret store.</summary>
internal sealed class RuntimeBuildSecrets(IDataProtectionProvider protection)
{
    internal static void Validate(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.Count > 64 || environment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != environment.Count
            || environment.Any(p => p.Key.Length > 128 || !Regex.IsMatch(p.Key, "\\A[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant)
                || string.IsNullOrEmpty(p.Value) || p.Value.Contains('\0') || Encoding.UTF8.GetByteCount(p.Value) > 4096)
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(environment)) > 16384)
            throw new InvalidDataException("Run secret environment exceeds its documented bounds.");
    }

    internal byte[] ProtectEnvironment(long buildId, IReadOnlyDictionary<string, string> environment)
        => environment.Count == 0 ? [] : Protect(buildId, "environment", JsonSerializer.SerializeToUtf8Bytes(environment));

    internal Dictionary<string, string> ReadEnvironment(long buildId, byte[] envelope)
    {
        if (envelope.Length == 0) return [];
        var plaintext = Unprotect(buildId, "environment", envelope);
        try
        {
            var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                ?? throw new InvalidDataException("Run secret environment is unavailable.");
            Validate(environment);
            return environment;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal byte[] ProtectAssignment(long buildId, BuildAssignment assignment)
        => Protect(buildId, "assignment", assignment.ToByteArray());

    internal BuildAssignment ReadAssignment(long buildId, byte[] envelope, int version = 1)
    {
        // Before runtime secrets were accepted, retained assignments used plain protobuf. They
        // contain no registered secret material. Never accept a plain assignment with masks.
        if (version == 0)
        {
            var legacy = BuildAssignment.Parser.ParseFrom(envelope);
            if (legacy.OutputSecrets.Count != 0) throw new InvalidDataException("Unprotected secret assignment is invalid.");
            return legacy;
        }
        if (version != 1) throw new InvalidDataException("Run recovery version is unsupported.");
        var plaintext = Unprotect(buildId, "assignment", envelope);
        try { return BuildAssignment.Parser.ParseFrom(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Protect(long buildId, string purpose, byte[] plaintext)
    {
        try
        {
            var ciphertext = Protector(buildId, purpose).Protect(plaintext);
            var envelope = new byte[7 + ciphertext.Length];
            "VIVSEC1"u8.CopyTo(envelope); ciphertext.CopyTo(envelope, 7);
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Unprotect(long buildId, string purpose, byte[] envelope)
    {
        if (!envelope.AsSpan().StartsWith("VIVSEC1"u8)) throw new InvalidDataException("Run recovery envelope is invalid.");
        try { return Protector(buildId, purpose).Unprotect(envelope.AsSpan(7).ToArray()); }
        catch (CryptographicException) { throw new InvalidDataException("Run recovery material cannot be decrypted."); }
    }

    private IDataProtector Protector(long buildId, string purpose) => protection.CreateProtector(
        "Vivarium.RuntimeBuildSecrets.v1", purpose, buildId.ToString(CultureInfo.InvariantCulture));
}
