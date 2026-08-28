namespace Vivarium.Cli;

internal interface ICliConsole
{
    bool IsInteractive { get; }
    void WriteLine(string value);
    void WriteError(string value);
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
    Task<string?> ReadSecretAsync(CancellationToken cancellationToken);
}

internal sealed class SystemCliConsole : ICliConsole
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public void WriteLine(string value) => Console.Out.WriteLine(value);

    public void WriteError(string value) => Console.Error.WriteLine(value);

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        await Console.In.ReadLineAsync(cancellationToken);

    public async Task<string?> ReadSecretAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            return await Console.In.ReadLineAsync(cancellationToken);
        }

        var value = new List<char>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return new string([.. value]);
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Count > 0)
                {
                    value.RemoveAt(value.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Add(key.KeyChar);
            }
        }
    }
}
