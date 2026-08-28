using System.Runtime.Versioning;
using System.Text;

namespace Vivarium.Controller.Security;

internal static class PrivateStorage
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SecretFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, DirectoryMode);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    public static void RestrictSecretFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, SecretFileMode);
        }
    }

    public static void WriteSecretText(string path, string value)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, value);
            return;
        }

        RestrictExistingFile(path);
        using (var stream = OpenSecretFile(path))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(value);
        }

        RestrictSecretFile(path);
    }

    public static void WriteSecretBytes(string path, byte[] value)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, value);
            return;
        }

        RestrictExistingFile(path);
        using (var stream = OpenSecretFile(path))
        {
            stream.Write(value);
        }

        RestrictSecretFile(path);
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream OpenSecretFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Create,
        Access = FileAccess.Write,
        Share = FileShare.None,
        UnixCreateMode = SecretFileMode,
    });

    private static void RestrictExistingFile(string path)
    {
        if (File.Exists(path))
        {
            RestrictSecretFile(path);
        }
    }
}
