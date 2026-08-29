using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Vivarium.Cli.Rest;

internal interface IVivariumRestApiClient : IAsyncDisposable
{
    Task ValidateAsync(CancellationToken cancellationToken);

    Task<RestBlobUploadPlan> CreateBlobUploadPlanAsync(
        RestBlobUploadPlanRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task UploadBlobAsync(
        RestBlobUploadPlanItem item,
        string stagingId,
        string filePath,
        CancellationToken cancellationToken);

    Task<RestResourceResponse<RestBuildResource>> SubmitBuildAsync(
        RestBuildSubmissionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<RestResourceResponse<RestBuildResource>> GetBuildAsync(
        string buildId,
        CancellationToken cancellationToken);

    Task<RestResourceResponse<RestBuildResource>> CancelBuildAsync(
        string buildId,
        string reason,
        CancellationToken cancellationToken);

    Task<RestAgentUpgradeOperationResource> CreateAgentUpgradeAsync(
        string agentId,
        RestAgentUpgradeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<RestAgentUpgradeOperationResource> GetAgentUpgradeAsync(
        string operationId,
        CancellationToken cancellationToken);

    Task<RestAgentUpgradeOperationResource> CancelAgentUpgradeAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken);

    IAsyncEnumerable<RestBuildWatchUpdate> WatchBuildAsync(
        string buildId,
        string? lastEventId,
        CancellationToken cancellationToken);
}

internal sealed class VivariumRestApiClient : IVivariumRestApiClient
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string BlobStagingHeader = "X-Vivarium-Blob-Staging-Id";
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private const int MaximumSseLineCharacters = 64 * 1024;
    private const int MaximumSseEventCharacters = 256 * 1024;
    private readonly Uri controller;
    private readonly HttpClient http;

    public VivariumRestApiClient(EndpointSettings settings)
        : this(
            settings,
            PinnedTls.CreateHandler(settings.Fingerprint),
            disposeHandler: true)
    {
    }

    internal VivariumRestApiClient(
        EndpointSettings settings,
        HttpMessageHandler handler,
        bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(handler);
        controller = new Uri(settings.Url.TrimEnd('/') + "/", UriKind.Absolute);
        http = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = controller,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.Token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/system");
        using var response = await SendAsync(request, cancellationToken);
        var system = await ReadJsonAsync<RestSystemResource>(response, cancellationToken);
        if (!string.Equals(system.ApiVersion, "v1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(system.Status))
        {
            throw new InvalidOperationException("controller returned an incompatible REST system resource");
        }
    }

    public async Task<RestBlobUploadPlan> CreateBlobUploadPlanAsync(
        RestBlobUploadPlanRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(idempotencyKey);
        ValidateBlobPlanRequest(request);
        using var message = JsonRequest(HttpMethod.Post, "api/v1/blob-upload-plans", request);
        message.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        using var response = await SendAsync(message, cancellationToken);
        var plan = await ReadJsonAsync<RestBlobUploadPlan>(response, cancellationToken);
        ValidateBlobPlanResponse(request, plan);
        return plan;
    }

    public async Task UploadBlobAsync(
        RestBlobUploadPlanItem item,
        string stagingId,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireBounded(stagingId, 256, nameof(stagingId));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!item.UploadRequired)
        {
            throw new InvalidOperationException("blob upload plan item does not require upload");
        }

        var uploadUri = ResolveControllerUri(item.UploadUrl);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != item.Size)
        {
            throw new InvalidOperationException("payload archive size changed after upload planning");
        }

        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = item.Size;
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri) { Content = content };
        request.Headers.TryAddWithoutValidation(BlobStagingHeader, stagingId);
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task<RestResourceResponse<RestBuildResource>> SubmitBuildAsync(
        RestBuildSubmissionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(idempotencyKey);
        using var message = JsonRequest(HttpMethod.Post, "api/v1/builds", request);
        message.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        using var response = await SendAsync(message, cancellationToken);
        return await ReadBuildResponseAsync(response, cancellationToken);
    }

    public async Task<RestResourceResponse<RestBuildResource>> GetBuildAsync(
        string buildId,
        CancellationToken cancellationToken)
    {
        RequireBounded(buildId, 256, nameof(buildId));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/builds/{Uri.EscapeDataString(buildId)}");
        using var response = await SendAsync(request, cancellationToken);
        return await ReadBuildResponseAsync(response, cancellationToken);
    }

    public async Task<RestResourceResponse<RestBuildResource>> CancelBuildAsync(
        string buildId,
        string reason,
        CancellationToken cancellationToken)
    {
        RequireBounded(buildId, 256, nameof(buildId));
        RequireBounded(reason, 512, nameof(reason));
        using var request = JsonRequest(
            HttpMethod.Put,
            $"api/v1/builds/{Uri.EscapeDataString(buildId)}/cancellation",
            new RestBuildCancellationRequest(reason));
        using var response = await SendAsync(request, cancellationToken);
        return await ReadBuildResponseAsync(response, cancellationToken);
    }

    public async Task<RestAgentUpgradeOperationResource> CreateAgentUpgradeAsync(
        string agentId,
        RestAgentUpgradeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        RequireBounded(agentId, 256, nameof(agentId));
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(idempotencyKey);
        using var message = JsonRequest(
            HttpMethod.Post,
            $"api/v1/agents/{Uri.EscapeDataString(agentId)}/upgrade-operations",
            request);
        message.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        using var response = await SendAsync(message, cancellationToken);
        var operation = await ReadJsonAsync<RestAgentUpgradeOperationResource>(
            response, cancellationToken);
        ValidateAgentUpgrade(operation, agentId);
        return operation;
    }

    public async Task<RestAgentUpgradeOperationResource> GetAgentUpgradeAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        RequireBounded(operationId, 256, nameof(operationId));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/agent-upgrade-operations/{Uri.EscapeDataString(operationId)}");
        using var response = await SendAsync(request, cancellationToken);
        var operation = await ReadJsonAsync<RestAgentUpgradeOperationResource>(
            response, cancellationToken);
        if (!string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("controller returned a different Agent upgrade operation");
        }

        ValidateAgentUpgrade(operation, operation.AgentId);
        return operation;
    }

    public async Task<RestAgentUpgradeOperationResource> CancelAgentUpgradeAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken)
    {
        RequireBounded(operationId, 256, nameof(operationId));
        RequireBounded(reason, 512, nameof(reason));
        using var request = JsonRequest(
            HttpMethod.Put,
            $"api/v1/agent-upgrade-operations/{Uri.EscapeDataString(operationId)}/cancellation",
            new RestAgentUpgradeCancellationRequest(reason));
        using var response = await SendAsync(request, cancellationToken);
        var operation = await ReadJsonAsync<RestAgentUpgradeOperationResource>(
            response, cancellationToken);
        if (!string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("controller returned a different Agent upgrade operation");
        }

        ValidateAgentUpgrade(operation, operation.AgentId);
        return operation;
    }

    public async IAsyncEnumerable<RestBuildWatchUpdate> WatchBuildAsync(
        string buildId,
        string? lastEventId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RequireBounded(buildId, 256, nameof(buildId));
        if (lastEventId is not null)
        {
            RequireBounded(lastEventId, 256, nameof(lastEventId));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/v1/events?topic=build&resourceId=" + Uri.EscapeDataString(buildId));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        using var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("controller build watch did not return an SSE stream");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var text = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4_096,
            leaveOpen: false);
        var lines = new BoundedLineReader(text);
        var eventId = default(string);
        var eventType = default(string);
        var data = new StringBuilder();
        while (await lines.ReadLineAsync(MaximumSseLineCharacters, cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return await MaterializeBuildEventAsync(
                        buildId,
                        data.ToString(),
                        eventId,
                        eventType,
                        cancellationToken);
                }

                eventId = null;
                eventType = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "id":
                    eventId = value;
                    break;
                case "event":
                    eventType = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    if (data.Length > MaximumSseEventCharacters)
                    {
                        throw new InvalidDataException("controller SSE event exceeds the client limit");
                    }
                    break;
            }
        }

        if (data.Length > 0)
        {
            yield return await MaterializeBuildEventAsync(
                buildId,
                data.ToString(),
                eventId,
                eventType,
                cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var problem = await TryReadProblemAsync(response, cancellationToken);
            throw new VivariumRestApiException(
                response.StatusCode,
                SafeProblemCode(problem?.Code, response.StatusCode),
                BoundProblemText(problem?.Detail ?? problem?.Title) ??
                    $"controller REST request failed with HTTP {(int)response.StatusCode}",
                IsBounded(problem?.CorrelationId, 256) ? problem!.CorrelationId : null);
        }
    }

    private async Task<RestBuildWatchUpdate> MaterializeBuildEventAsync(
        string buildId,
        string json,
        string? eventId,
        string? eventType,
        CancellationToken cancellationToken)
    {
        var envelope = ParseEvent(json, eventId, eventType);
        if (!string.Equals(envelope.Resource.Type, "build", StringComparison.Ordinal) ||
            !string.Equals(envelope.Resource.Id, buildId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("controller build event references a different resource");
        }

        var current = await GetBuildAsync(buildId, cancellationToken);
        return new RestBuildWatchUpdate(envelope.Id, current.Resource, current.ETag);
    }

    private async Task<RestResourceResponse<RestBuildResource>> ReadBuildResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var resource = await ReadJsonAsync<RestBuildResource>(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(resource.Id) || string.IsNullOrWhiteSpace(resource.RuntimeRevision))
        {
            throw new InvalidDataException("controller returned an invalid REST build resource");
        }

        return new RestResourceResponse<RestBuildResource>(
            resource,
            response.Headers.ETag?.Tag,
            response.Headers.Location);
    }

    private static HttpRequestMessage JsonRequest<T>(
        HttpMethod method,
        string uri,
        T body) => new(method, uri)
    {
        Content = JsonContent.Create(body, options: JsonOptions),
    };

    private async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedContentAsync(response.Content, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new InvalidDataException("controller returned an empty JSON resource");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("controller returned malformed REST JSON", exception);
        }
    }

    private async Task<RestProblemResponse?> TryReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadBoundedContentAsync(response.Content, cancellationToken);
            return JsonSerializer.Deserialize<RestProblemResponse>(bytes, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumJsonBytes)
        {
            throw new InvalidDataException("controller REST response exceeds the client limit");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaximumJsonBytes)
                {
                    throw new InvalidDataException("controller REST response exceeds the client limit");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static RestEventEnvelope ParseEvent(
        string json,
        string? sseId,
        string? sseType)
    {
        RestEventEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RestEventEnvelope>(json, JsonOptions)
                ?? throw new InvalidDataException("controller returned an empty SSE event");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("controller returned malformed SSE event JSON", exception);
        }

        RequireBounded(envelope.Id, 256, "event id");
        if (sseId is not null && !string.Equals(sseId, envelope.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("controller SSE id conflicts with its event envelope");
        }

        if (!string.IsNullOrWhiteSpace(sseType) &&
            !string.Equals(sseType, envelope.Type, StringComparison.Ordinal))
        {
            throw new InvalidDataException("controller SSE type conflicts with its event envelope");
        }

        return envelope;
    }

    private Uri ResolveControllerUri(string value)
    {
        RequireBounded(value, 2_048, nameof(value));
        var resolved = Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https"
                ? absolute
                : new Uri(controller, value);
        if (!string.Equals(resolved.Scheme, controller.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, controller.Host, StringComparison.OrdinalIgnoreCase) ||
            resolved.Port != controller.Port ||
            !string.IsNullOrEmpty(resolved.UserInfo))
        {
            throw new InvalidDataException("controller returned an off-origin blob upload URL");
        }

        return resolved;
    }

    private static void ValidateBlobPlanRequest(RestBlobUploadPlanRequest request)
    {
        RequireBounded(request.ProjectId, 256, nameof(request.ProjectId));
        if (request.Blobs.Count > 1_024)
        {
            throw new ArgumentException("blob upload plan exceeds the client item limit", nameof(request));
        }

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var blob in request.Blobs)
        {
            if (!IsSha256(blob.Sha256) || blob.Size < 0 || !hashes.Add(blob.Sha256))
            {
                throw new ArgumentException("blob upload plan contains an invalid or duplicate item", nameof(request));
            }
        }
    }

    private static void ValidateAgentUpgrade(
        RestAgentUpgradeOperationResource operation,
        string expectedAgentId)
    {
        if (string.IsNullOrWhiteSpace(operation.OperationId) ||
            !string.Equals(operation.AgentId, expectedAgentId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(operation.Package.PackageId) ||
            !IsSha256(operation.Package.Sha256) ||
            operation.MaintenanceFence <= 0 ||
            operation.Deadline <= operation.CreatedAt ||
            operation.Events.Count > 256 ||
            operation.Events.Any(value => value.Sequence <= 0 ||
                string.IsNullOrWhiteSpace(value.Phase) || string.IsNullOrWhiteSpace(value.Code)))
        {
            throw new InvalidDataException("controller returned an invalid Agent upgrade operation");
        }
    }

    private static void ValidateBlobPlanResponse(
        RestBlobUploadPlanRequest request,
        RestBlobUploadPlan plan)
    {
        RequireBounded(plan.Id, 256, nameof(plan.Id));
        var requested = request.Blobs.ToDictionary(blob => blob.Sha256, StringComparer.Ordinal);
        if (plan.Items.Count != requested.Count)
        {
            throw new InvalidDataException("controller blob upload plan has an unexpected item count");
        }

        foreach (var item in plan.Items)
        {
            if (!requested.TryGetValue(item.Sha256, out var expected) ||
                expected.Size != item.Size ||
                !IsSha256(item.Sha256) ||
                string.IsNullOrWhiteSpace(item.UploadUrl))
            {
                throw new InvalidDataException("controller blob upload plan contains an unknown item");
            }
        }
    }

    private static void ValidateIdempotencyKey(string value)
    {
        RequireBounded(value, 256, nameof(value));
        if (value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '@' or '/' or '-')))
        {
            throw new ArgumentException("REST Idempotency-Key contains unsupported characters", nameof(value));
        }
    }

    private static void RequireBounded(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} must be a bounded non-control string", name);
        }
    }

    private static string SafeProblemCode(string? value, System.Net.HttpStatusCode statusCode)
    {
        if (!IsBounded(value, 128) || value!.Any(character =>
                !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')))
        {
            return $"http_{(int)statusCode}";
        }

        return value!;
    }

    private static string? BoundProblemText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return new string(value.Take(1_024)
            .Select(character => char.IsControl(character) ? '?' : character)
            .ToArray());
    }

    private static bool IsBounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum &&
        !value.Any(char.IsControl);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    private sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] buffer = new char[4_096];
        private int position;
        private int count;
        private bool skipLineFeed;

        public async ValueTask<string?> ReadLineAsync(
            int maximumCharacters,
            CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (position == count)
                {
                    count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                    position = 0;
                    if (count == 0)
                    {
                        return line.Length == 0 ? null : line.ToString();
                    }
                }

                var character = buffer[position++];
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }

                if (character == '\r')
                {
                    skipLineFeed = true;
                    return line.ToString();
                }

                if (character == '\n')
                {
                    return line.ToString();
                }

                line.Append(character);
                if (line.Length > maximumCharacters)
                {
                    throw new InvalidDataException("controller SSE line exceeds the client limit");
                }
            }
        }
    }
}
