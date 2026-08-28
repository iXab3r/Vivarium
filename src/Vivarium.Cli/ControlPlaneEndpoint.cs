using System.Net.Http.Headers;
using Grpc.Core;
using Grpc.Net.Client;
using Vivarium.Contracts.V1;

namespace Vivarium.Cli;

internal interface IControlPlaneEndpoint : IAsyncDisposable
{
    Task ValidateAsync(CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> MissingBlobsAsync(
        IReadOnlyCollection<string> hashes,
        CancellationToken cancellationToken);
    Task UploadBlobAsync(string hash, string path, CancellationToken cancellationToken);
    Task<BuildRef> SubmitBuildAsync(SubmitBuildRequest request, CancellationToken cancellationToken);
    Task<BuildSnapshot> CancelBuildAsync(
        string buildId,
        string reason,
        CancellationToken cancellationToken);
    IAsyncEnumerable<BuildSnapshot> WatchBuildAsync(string buildId, CancellationToken cancellationToken);
}

internal interface IControlPlaneEndpointFactory
{
    IControlPlaneEndpoint Create(EndpointSettings settings);
}

internal sealed class ControlPlaneEndpointFactory : IControlPlaneEndpointFactory
{
    public IControlPlaneEndpoint Create(EndpointSettings settings) => new ControlPlaneEndpoint(settings);
}

internal sealed class ControlPlaneEndpoint : IControlPlaneEndpoint
{
    private readonly string token;
    private readonly Uri blobBaseAddress;
    private readonly GrpcChannel channel;
    private readonly HttpClient http;
    private readonly Vivarium.Contracts.V1.ControlPlane.ControlPlaneClient client;

    public ControlPlaneEndpoint(EndpointSettings settings)
    {
        token = settings.Token;
        blobBaseAddress = new Uri(settings.Url.TrimEnd('/') + "/", UriKind.Absolute);
        channel = GrpcChannel.ForAddress(settings.Url, new GrpcChannelOptions
        {
            HttpHandler = PinnedTls.CreateHandler(settings.Fingerprint),
        });
        client = new Vivarium.Contracts.V1.ControlPlane.ControlPlaneClient(channel);
        http = new HttpClient(PinnedTls.CreateHandler(settings.Fingerprint));
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await client.MissingBlobsAsync(
            new BlobHashes(), Headers(), cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlySet<string>> MissingBlobsAsync(
        IReadOnlyCollection<string> hashes,
        CancellationToken cancellationToken)
    {
        var request = new BlobHashes();
        request.Sha256.Add(hashes);
        var response = await client.MissingBlobsAsync(
            request, Headers(), cancellationToken: cancellationToken);
        return response.Sha256.ToHashSet(StringComparer.Ordinal);
    }

    public async Task UploadBlobAsync(string hash, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri(blobBaseAddress, $"blobs/{hash}"))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"payload upload failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
        }
    }

    public async Task<BuildRef> SubmitBuildAsync(
        SubmitBuildRequest request,
        CancellationToken cancellationToken) =>
        await client.SubmitBuildAsync(
            request, Headers(), cancellationToken: cancellationToken);

    public async Task<BuildSnapshot> CancelBuildAsync(
        string buildId,
        string reason,
        CancellationToken cancellationToken) => await client.CancelBuildAsync(
            new CancelBuildRequest { BuildId = buildId, Reason = reason },
            Headers(),
            cancellationToken: cancellationToken);

    public async IAsyncEnumerable<BuildSnapshot> WatchBuildAsync(
        string buildId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var call = client.WatchBuild(
            new BuildRef { BuildId = buildId }, Headers(), cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        channel.Dispose();
        return ValueTask.CompletedTask;
    }

    private Metadata Headers() => new() { { "authorization", $"Bearer {token}" } };
}
