using System.Text.Json;

namespace Vivarium.Cli;

internal sealed record ClientConfiguration(string Url, string Fingerprint, string Token);

internal sealed record EndpointSettings(string Url, string Fingerprint, string Token);

internal interface IClientConfigurationStore
{
    Task<ClientConfiguration?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ClientConfiguration configuration, CancellationToken cancellationToken);
}

internal sealed class UserClientConfigurationStore(string? path = null) : IClientConfigurationStore
{
    public string Path { get; } = path ?? GetDefaultPath();

    public async Task<ClientConfiguration?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        await using var stream = new FileStream(
            Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        try
        {
            return await JsonSerializer.DeserializeAsync<ClientConfiguration>(
                stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"saved client configuration '{Path}' is invalid", exception);
        }
    }

    public async Task SaveAsync(ClientConfiguration configuration, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        TryRestrictDirectory(directory);

        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                TryRestrictFile(temporary);
                await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            TryRestrictFile(temporary);
            File.Move(temporary, Path, overwrite: true);
            TryRestrictFile(Path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string GetDefaultPath()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return System.IO.Path.Combine(root, "vivarium", "config.json");
    }

    private static void TryRestrictDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: the user's config directory may be on a filesystem without Unix modes.
        }
    }

    private static void TryRestrictFile(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: the user's config directory may be on a filesystem without Unix modes.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal static class EndpointSettingsResolver
{
    public static EndpointSettings Resolve(
        string? flagUrl,
        string? flagToken,
        string? flagFingerprint,
        Func<string, string?> environment,
        ClientConfiguration? saved)
    {
        var url = First(flagUrl, environment("VIVARIUM_URL"), saved?.Url)
            ?? throw new InvalidOperationException(
                "controller URL is required (--url, VIVARIUM_URL, or 'viv-cli login')");
        var token = First(flagToken, environment("VIVARIUM_TOKEN"), saved?.Token)
            ?? throw new InvalidOperationException(
                "controller token is required (--token, VIVARIUM_TOKEN, or 'viv-cli login')");
        var fingerprint = First(
                flagFingerprint,
                environment("VIVARIUM_CERT_FINGERPRINT"),
                saved?.Fingerprint)
            ?? throw new InvalidOperationException(
                "controller certificate fingerprint is required (--fingerprint, " +
                "VIVARIUM_CERT_FINGERPRINT, or 'viv-cli login')");

        return new EndpointSettings(
            PinnedTls.NormalizeControllerUrl(url),
            PinnedTls.NormalizeFingerprint(fingerprint),
            token);
    }

    private static string? First(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
