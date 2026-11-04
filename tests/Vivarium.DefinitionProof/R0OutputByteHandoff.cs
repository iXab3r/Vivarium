namespace Vivarium.DefinitionProof;

/// <summary>Pause one real output PUT after Storage accepts it and before the Host can publish its PG reference.</summary>
internal sealed class R0OutputByteHandoff
{
    private int armed;
    private readonly TaskCompletionSource stored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Stored => stored.Task;
    internal void Arm() => Interlocked.Exchange(ref armed, 1);
    internal void Release() => release.TrySetResult();
    internal HttpMessageHandler Handler() => new HandoffHandler(this);

    private sealed class HandoffHandler(R0OutputByteHandoff owner)
        : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            // The case arms only after both process prefixes are committed; its next PUT is
            // the gated tail. Storage URLs deliberately do not disclose application object keys.
            if (request.Method != HttpMethod.Put || Interlocked.CompareExchange(ref owner.armed, 0, 1) != 1) return response;
            try
            {
                response.EnsureSuccessStatusCode(); owner.stored.TrySetResult();
                await owner.release.Task.WaitAsync(cancellationToken); return response;
            }
            catch { response.Dispose(); throw; }
        }
    }
}
