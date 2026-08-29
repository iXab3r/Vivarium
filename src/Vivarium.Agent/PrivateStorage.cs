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
        if (File.Exists(path))
        {
            RestrictSecretFile(path);
        }
        DurableFile.ReplaceText(path, value, SecretFileMode);
        RestrictSecretFile(path);
    }
}
