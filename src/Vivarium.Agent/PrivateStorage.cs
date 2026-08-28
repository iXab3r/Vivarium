using System.Text;

namespace Vivarium.Agent;

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

        if (File.Exists(path))
        {
            RestrictSecretFile(path);
        }

        using (var stream = new FileStream(path, new FileStreamOptions
               {
                   Mode = FileMode.Create,
                   Access = FileAccess.Write,
                   Share = FileShare.None,
                   UnixCreateMode = SecretFileMode,
               }))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(value);
        }

        RestrictSecretFile(path);
    }
}
