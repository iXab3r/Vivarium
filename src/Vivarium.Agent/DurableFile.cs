using System.Runtime.InteropServices;
using System.Text;

namespace Vivarium.Agent;

internal static class DurableFile
{
    public static void ReplaceText(string path, string value, UnixFileMode? unixMode = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("durable file has no parent directory");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(
                   temporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   16 * 1024,
                   FileOptions.WriteThrough))
        {
            if (unixMode is not null && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, unixMode.Value);
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
        using (var committed = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.Read,
                   1,
                   FileOptions.WriteThrough))
        {
            committed.Flush(flushToDisk: true);
        }

        FlushDirectory(directory);
    }

    private static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var descriptor = Open(directory, 0);
        if (descriptor < 0)
        {
            throw new IOException("could not open durable file directory", Marshal.GetLastPInvokeError());
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException("could not flush durable file directory", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
