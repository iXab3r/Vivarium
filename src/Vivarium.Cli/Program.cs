using Vivarium.Cli;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return await VivariumCliApplication.CreateDefault().ExecuteAsync(args, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

internal sealed partial class Program;
