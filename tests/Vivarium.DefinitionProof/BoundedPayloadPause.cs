namespace Vivarium.DefinitionProof;

/// <summary>A finite external HTTP actor: pause only the second script PUT, forwarding real Storage I/O.</summary>
internal sealed class BoundedPayloadPause : DelegatingHandler
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int uploads;
    internal Task Entered => entered.Task;
    internal Task Completed => completed.Task;
    internal void Release() => released.TrySetResult();
    internal BoundedPayloadPause() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pause = request.Method == HttpMethod.Put && Interlocked.Increment(ref uploads) == 2;
        if (!pause) return await base.SendAsync(request, cancellationToken);
        entered.TrySetResult();
        await released.Task.WaitAsync(cancellationToken);
        try { return await base.SendAsync(request, cancellationToken); }
        finally { completed.TrySetResult(); }
    }
}
