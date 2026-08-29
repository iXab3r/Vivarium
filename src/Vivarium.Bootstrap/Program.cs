// Vivarium bootstrap — the change-controlled launcher (ARCHITECTURE D2/D21/D30).
// It owns authenticated acquisition, atomic activation, health-gated rollback, and exactly one child.
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const long maximumPackageSize = 512L * 1024 * 1024;
const long maximumExpandedSize = 2L * 1024 * 1024 * 1024;
const long minimumFreeSpaceReserve = 128L * 1024 * 1024;
const int maximumEntries = 2048;
const int maximumPathLength = 512;
const int maximumSegmentLength = 255;
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = false,
};

var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
var configPath = Path.Combine(baseDir, "bootstrap.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"bootstrap: missing {configPath}");
    return 2;
}

using var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var controllerUrl = configDoc.RootElement.GetProperty("controllerUrl").GetString()!;
var fingerprint = configDoc.RootElement.GetProperty("certFingerprint").GetString()!
    .Replace("SHA256:", "", StringComparison.OrdinalIgnoreCase);
if (!Uri.TryCreate(controllerUrl, UriKind.Absolute, out var controllerUri) ||
    controllerUri.Scheme != Uri.UriSchemeHttps ||
    !string.IsNullOrEmpty(controllerUri.UserInfo) ||
    !string.IsNullOrEmpty(controllerUri.Query) ||
    !string.IsNullOrEmpty(controllerUri.Fragment) ||
    fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
{
    Console.Error.WriteLine("bootstrap: controllerUrl or certificate fingerprint is invalid");
    return 2;
}

var dataDir = Path.Combine(baseDir, "data");
var agentDir = Path.Combine(baseDir, "agent");
var packagesDir = Path.Combine(agentDir, "packages");
var legacyCurrentDir = Path.Combine(agentDir, "current");
var legacyVersionFile = Path.Combine(agentDir, "version");
var statePath = Path.Combine(agentDir, "active.json");
var childPath = Path.Combine(agentDir, "child.json");
var lockPath = Path.Combine(agentDir, "bootstrap.lock");
var healthMarkerPath = Path.Combine(dataDir, "agent-upgrade-health.json");
var promotionMarkerPath = healthMarkerPath + ".promoted";
var leasePath = Path.Combine(dataDir, "bootstrap-lease.json");
var tokenPath = Path.Combine(dataDir, "auth.token");
var executableName = OperatingSystem.IsWindows() ? "vivarium-agent.exe" : "vivarium-agent";
var rid = CurrentRid();
Directory.CreateDirectory(agentDir);
Directory.CreateDirectory(packagesDir);
Directory.CreateDirectory(dataDir);

FileStream singleton;
try
{
    singleton = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    Console.Error.WriteLine("bootstrap: another supervisor already owns this installation");
    return 3;
}

using (singleton)
{
    singleton.SetLength(0);
    var owner = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    singleton.Write(owner);
    singleton.Flush(flushToDisk: true);

    var handler = new SocketsHttpHandler();
    handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
        cert != null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))
            .Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
    using var http = new HttpClient(handler) { BaseAddress = controllerUri };

    var state = LoadOrCreateState();
    CleanupAbandonedTemporaryContent(state);
    var trackedNextLaunchUnixMs = state.NextLaunchUnixMs;
    var nextLaunchWait = Stopwatch.StartNew();
    string? unrecordedLeaseId = null;
    Stopwatch? unrecordedLeaseWait = null;
    Console.WriteLine($"vivarium-bootstrap: controller {controllerUri.GetLeftPart(UriPartial.Authority)}");
    while (true)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (state.NextLaunchUnixMs != trackedNextLaunchUnixMs)
        {
            trackedNextLaunchUnixMs = state.NextLaunchUnixMs;
            nextLaunchWait.Restart();
        }
        if (state.NextLaunchUnixMs > now && nextLaunchWait.Elapsed < TimeSpan.FromMinutes(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(
                5000,
                Math.Min(
                    state.NextLaunchUnixMs - now,
                    Math.Max(1, (TimeSpan.FromMinutes(5) - nextLaunchWait.Elapsed).TotalMilliseconds)))));
            continue;
        }
        if (state.NextLaunchUnixMs > now)
        {
            state = state with { NextLaunchUnixMs = 0 };
            WriteState(state);
        }

        try
        {
            state = await FlushPendingFailureReportAsync(state);
            if (state.PendingFailureReport is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }
            state = await ApplyDirectiveBeforeLaunchAsync(state);
            var executable = ResolveExecutable(state.Active);
            if (executable is null)
            {
                throw new InvalidDataException("no verified Agent package is available");
            }
            state = await SuperviseAgentAsync(state, executable);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"bootstrap: supervision failed ({SafeMessage(exception)})");
            if (exception is UnrecordedLeaseException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }
            if (exception is ChildTerminationException termination)
            {
                state = RecordSupervisorFailure(
                    state, termination.OperationId, "child_termination_failed");
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }
            state = state.Pending is null
                ? RecordLaunchFailure(state)
                : RollBack(
                    state,
                    exception is CandidateLaunchException
                        ? "candidate_launch_failed"
                        : "candidate_supervision_failed");
        }
    }

    BootstrapState LoadOrCreateState()
    {
        BootstrapState loaded;
        if (File.Exists(statePath))
        {
            var stateJson = File.ReadAllText(statePath);
            loaded = JsonSerializer.Deserialize<BootstrapState>(stateJson, jsonOptions)
                ?? throw new InvalidDataException("active package state is empty");
            if (loaded.SchemaVersion == 1 && loaded.Pending is not null)
            {
                using var legacyDocument = JsonDocument.Parse(stateJson);
                var legacyPending = legacyDocument.RootElement.GetProperty("pending");
                var timeoutSeconds = legacyPending.GetProperty("healthTimeoutSeconds").GetInt32();
                loaded = loaded with
                {
                    Pending = loaded.Pending with
                    {
                        DeadlineUnixMs = checked(
                            loaded.Pending.ActivatedUnixMs + timeoutSeconds * 1000L),
                    },
                };
            }
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(packagesDir).Any() ||
                File.Exists(childPath) || File.Exists(healthMarkerPath) ||
                File.Exists(promotionMarkerPath))
            {
                throw new InvalidDataException(
                    "active package state is missing from an initialized installation");
            }
            var version = File.Exists(legacyVersionFile)
                ? File.ReadAllText(legacyVersionFile).Trim()
                : "seed";
            var seedExecutable = Path.Combine(legacyCurrentDir, executableName);
            if (!File.Exists(seedExecutable))
            {
                throw new InvalidDataException("seed Agent executable is missing");
            }
            loaded = new BootstrapState(
                2,
                new PackageSlot(version, rid, ComputeFileSha256(seedExecutable), "current"),
                null,
                null,
                null,
                null,
                0,
                0);
            WriteState(loaded);
            return loaded;
        }

        var legacyMigration = loaded.SchemaVersion == 1;
        if (legacyMigration)
        {
            loaded = loaded with
            {
                SchemaVersion = 2,
                Active = EnsureSlotDigest(loaded.Active),
                Fallback = loaded.Fallback is null ? null : EnsureSlotDigest(loaded.Fallback),
                Pending = loaded.Pending is null ? null : loaded.Pending with
                {
                    Previous = EnsureSlotDigest(loaded.Pending.Previous),
                },
            };
            EnsurePackageReceipt(loaded.Active);
            if (loaded.Fallback is not null)
            {
                EnsurePackageReceipt(loaded.Fallback);
            }
            if (loaded.Pending is not null)
            {
                EnsurePackageReceipt(loaded.Pending.Previous);
            }
            ValidateState(loaded);
            WriteState(loaded);
            return loaded;
        }
        ValidateState(loaded);
        VerifyPersistedSlot(loaded.Active);
        if (loaded.Fallback is not null)
        {
            VerifyPersistedSlot(loaded.Fallback);
        }
        if (loaded.Pending is not null)
        {
            VerifyPersistedSlot(loaded.Pending.Previous);
        }
        return loaded;
    }

    async Task<BootstrapState> ApplyDirectiveBeforeLaunchAsync(BootstrapState current)
    {
        var token = ReadAgentToken();
        if (token is null)
        {
            return current;
        }
        BootstrapManifest? directive;
        try
        {
            directive = await FetchManifestAsync(token);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"bootstrap: directive fetch failed ({SafeMessage(exception)})");
            return current;
        }
        if (directive is null)
        {
            return current;
        }
        if (directive.Action == "rollback" && NeedsLocalRollback(current, directive.OperationId))
        {
            return RollBack(current, "controller_requested_rollback");
        }
        if (directive.Action == "rollback" && ShouldReportUnneededRollback(current, directive))
        {
            return ReportUnneededRollback(current, directive.OperationId);
        }
        if (directive.Action != "activate" ||
            current.Pending?.OperationId == directive.OperationId ||
            current.ReportOperationId == directive.OperationId)
        {
            return current;
        }
        try
        {
            return await StageAndActivateAsync(current, directive, token);
        }
        catch (Exception exception)
        {
            var failed = current with
            {
                Pending = null,
                ReportOperationId = directive.OperationId,
                ReportFailureCode = FailureCode(exception),
            };
            WriteState(failed);
            Console.Error.WriteLine(
                $"bootstrap: package {ShortDigest(directive.Sha256)} staging failed " +
                $"({failed.ReportFailureCode})");
            return failed;
        }
    }

    async Task<BootstrapManifest?> FetchManifestAsync(string token)
    {
        var platform = rid.Split('-', 2);
        var os = platform[0] == "win" ? "windows" : platform[0] == "osx" ? "macos" : "linux";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/bootstrap/manifest?os={Uri.EscapeDataString(os)}&arch={Uri.EscapeDataString(platform[1])}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync();
        var manifest = await JsonSerializer.DeserializeAsync<BootstrapManifest>(content, jsonOptions)
            ?? throw new InvalidDataException("package manifest is empty");
        ValidateManifest(manifest);
        return manifest;
    }

    async Task<BootstrapState> StageAndActivateAsync(
        BootstrapState current,
        BootstrapManifest manifest,
        string token)
    {
        if (current.Pending is not null || current.Active.Sha256 != manifest.PriorSha256)
        {
            throw new UpgradeStageException("upgrade_prior_digest_mismatch");
        }
        Console.WriteLine($"bootstrap: staging Agent {manifest.Version} ({ShortDigest(manifest.Sha256)})");
        var finalDirectory = Path.Combine(packagesDir, manifest.Sha256);
        if (Directory.Exists(finalDirectory) && !VerifyExtractedPackage(finalDirectory, manifest.Sha256))
        {
            Directory.Delete(finalDirectory, recursive: true);
        }
        if (!Directory.Exists(finalDirectory))
        {
            EnsureAvailableSpace(manifest.Size);
            var downloadPath = Path.Combine(agentDir, $"download-{Guid.NewGuid():N}.tmp");
            var stagingDirectory = Path.Combine(packagesDir, $"staging-{Guid.NewGuid():N}");
            try
            {
                await DownloadAsync(manifest, token, downloadPath);
                ExtractVerifiedPackage(downloadPath, stagingDirectory);
                WritePackageReceipt(stagingDirectory, manifest.Sha256);
                try
                {
                    Directory.Move(stagingDirectory, finalDirectory);
                    DurableFile.FlushDirectory(packagesDir);
                }
                catch (IOException) when (Directory.Exists(finalDirectory) &&
                                           VerifyExtractedPackage(finalDirectory, manifest.Sha256))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            finally
            {
                DeleteFile(downloadPath);
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }

        var confirmation = await FetchManifestAsync(token);
        if (confirmation is null || confirmation != manifest ||
            current.Pending is not null || current.Active.Sha256 != confirmation.PriorSha256)
        {
            throw new UpgradeStageException("upgrade_directive_changed_during_staging");
        }
        var slot = new PackageSlot(
            manifest.Version,
            manifest.Rid,
            manifest.Sha256,
            $"packages/{manifest.Sha256}");
        if (ResolveExecutable(slot) is null)
        {
            throw new UpgradeStageException("package_executable_missing");
        }
        DeleteUpgradeMarkers();
        var activated = new BootstrapState(
            2,
            slot,
            current.Active,
            new PendingUpgrade(
                confirmation.OperationId,
                confirmation.Sha256,
                current.Active,
                DateTimeOffset.UtcNow.AddSeconds(confirmation.HealthTimeoutSeconds)
                    .ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null,
                null),
            null,
            null,
            0,
            0);
        WriteState(activated);
        return activated;
    }

    async Task DownloadAsync(BootstrapManifest manifest, string token, string destination)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } declared && declared != manifest.Size)
        {
            throw new UpgradeStageException("package_size_header_mismatch");
        }
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long size = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }
            size = checked(size + read);
            if (size > manifest.Size || size > maximumPackageSize)
            {
                throw new UpgradeStageException("package_download_too_large");
            }
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        output.Flush(flushToDisk: true);
        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (size != manifest.Size || digest != manifest.Sha256)
        {
            throw new UpgradeStageException("package_digest_mismatch");
        }
    }

    void ExtractVerifiedPackage(string archivePath, string stagingDirectory)
    {
        Directory.CreateDirectory(stagingDirectory);
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory)) +
            Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
        {
                throw new UpgradeStageException("package_entry_count_invalid");
        }
        long declaredArchiveSize;
        try
        {
            declaredArchiveSize = archive.Entries.Aggregate(
                0L, (total, entry) => checked(total + entry.Length));
        }
        catch (OverflowException)
        {
            throw new UpgradeStageException("package_expansion_limit_exceeded");
        }
        if (declaredArchiveSize > maximumExpandedSize)
        {
            throw new UpgradeStageException("package_expansion_limit_exceeded");
        }
        EnsureAvailableSpace(declaredArchiveSize);
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long declaredExpanded = 0;
        long actualExpanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = ValidateArchivePath(entry.FullName);
            var isDirectory = name.EndsWith('/');
            var canonical = isDirectory ? name.TrimEnd('/') : name;
            if (!paths.TryAdd(canonical, isDirectory))
            {
                throw new UpgradeStageException("package_path_duplicate");
            }
            if (!isDirectory && paths.Keys.Any(path =>
                    path.StartsWith(canonical + "/", StringComparison.OrdinalIgnoreCase)))
            {
                throw new UpgradeStageException("package_file_directory_conflict");
            }
            foreach (var parent in ParentPaths(canonical))
            {
                if (paths.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                {
                    throw new UpgradeStageException("package_file_directory_conflict");
                }
            }
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x4000 or 0x8000) ||
                isDirectory && unixType == 0x8000 || !isDirectory && unixType == 0x4000)
            {
                throw new UpgradeStageException("package_special_file_rejected");
            }
            declaredExpanded = checked(declaredExpanded + entry.Length);
            if (declaredExpanded > maximumExpandedSize ||
                entry.Length > checked(entry.CompressedLength * 100 + 1024 * 1024))
            {
                throw new UpgradeStageException("package_expansion_limit_exceeded");
            }
            var destination = Path.GetFullPath(Path.Combine(stagingDirectory, name));
            if (!destination.StartsWith(
                    rootPrefix,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new UpgradeStageException("package_path_escape");
            }
            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            long actualEntryLength = 0;
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                actualEntryLength = checked(actualEntryLength + read);
                actualExpanded = checked(actualExpanded + read);
                if (actualEntryLength > entry.Length || actualExpanded > maximumExpandedSize)
                {
                    throw new UpgradeStageException("package_expansion_limit_exceeded");
                }
                output.Write(buffer, 0, read);
            }
            if (actualEntryLength != entry.Length)
            {
                throw new UpgradeStageException("package_entry_size_mismatch");
            }
            output.Flush(flushToDisk: true);
        }
        var executable = Path.Combine(stagingDirectory, executableName);
        if (!File.Exists(executable))
        {
            throw new UpgradeStageException("package_executable_missing");
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    async Task<BootstrapState> SuperviseAgentAsync(BootstrapState current, string executable)
    {
        var operationId = current.Pending?.OperationId ?? current.ReportOperationId;
        var child = await GetOrStartAgentAsync(
            current.Active, executable, operationId, current.ReportFailureCode);
        using var process = child.Process;
        using var leaseCts = new CancellationTokenSource();
        var leaseTask = MaintainLeaseAsync(child.LeaseId, leaseCts.Token);
        var processLifetime = Stopwatch.StartNew();
        var directiveInterval = Stopwatch.StartNew();
        var pendingWatchdog = Stopwatch.StartNew();
        var pendingBudget = current.Pending is null
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Clamp(
                current.Pending.DeadlineUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                0,
                120_000));
        ChildTerminationException? terminationFailure = null;
        try
        {
            while (true)
            {
                if (process.HasExited)
                {
                    DeleteFile(childPath);
                    Console.WriteLine($"bootstrap: Agent exited with {process.ExitCode}");
                    if (current.Pending is not null)
                    {
                        return RollBack(current, "candidate_exited_before_commit");
                    }
                    return process.ExitCode == 0 || processLifetime.Elapsed >= TimeSpan.FromSeconds(30)
                        ? ResetLaunchFailures(current)
                        : RecordLaunchFailure(current);
                }

                if (current.Pending is not null)
                {
                    current = ObserveUpgradeMarker(current);
                    if (current.Pending is not null &&
                        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= current.Pending.DeadlineUnixMs ||
                         pendingWatchdog.Elapsed >= pendingBudget))
                    {
                        await TerminateChildAsync(process, current.Pending.OperationId);
                        DeleteFile(childPath);
                        return RollBack(current, "candidate_health_deadline_exceeded");
                    }
                }

                if (directiveInterval.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    directiveInterval.Restart();
                    var token = ReadAgentToken();
                    if (token is not null)
                    {
                        BootstrapManifest? directive = null;
                        try
                        {
                            directive = await FetchManifestAsync(token);
                        }
                        catch (Exception exception)
                        {
                            Console.Error.WriteLine(
                                $"bootstrap: directive poll failed ({SafeMessage(exception)})");
                        }
                        if (directive?.Action == "rollback" &&
                            NeedsLocalRollback(current, directive.OperationId))
                        {
                            await TerminateChildAsync(process, directive.OperationId);
                            DeleteFile(childPath);
                            return RollBack(current, "controller_requested_rollback");
                        }
                        if (directive?.Action == "rollback" &&
                            ShouldReportUnneededRollback(current, directive))
                        {
                            await TerminateChildAsync(process, directive.OperationId);
                            DeleteFile(childPath);
                            return ReportUnneededRollback(current, directive.OperationId);
                        }
                        if (directive?.Action == "activate" &&
                            current.Pending?.OperationId != directive.OperationId &&
                            current.ReportOperationId != directive.OperationId)
                        {
                            await TerminateChildAsync(process, directive.OperationId);
                            DeleteFile(childPath);
                            try
                            {
                                return await StageAndActivateAsync(current, directive, token);
                            }
                            catch (Exception exception)
                            {
                                var failed = current with
                                {
                                    Pending = null,
                                    ReportOperationId = directive.OperationId,
                                    ReportFailureCode = FailureCode(exception),
                                };
                                WriteState(failed);
                                return failed;
                            }
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
        catch (ChildTerminationException exception)
        {
            terminationFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (!process.HasExited && terminationFailure is null)
                {
                    await TerminateChildAsync(process, operationId);
                    DeleteFile(childPath);
                }
            }
            finally
            {
                leaseCts.Cancel();
                try
                {
                    await leaseTask;
                }
                catch (OperationCanceledException)
                {
                }
                DeleteFile(leasePath);
            }
        }
    }

    BootstrapState ObserveUpgradeMarker(BootstrapState current)
    {
        var pending = current.Pending!;
        if (!TryReadUpgradeMarker(healthMarkerPath, out var marker) ||
            marker.OperationId != pending.OperationId || marker.PackageSha256 != current.Active.Sha256)
        {
            return current;
        }
        if (marker.Stage == "ready")
        {
            if (pending.PromotedConnectionGeneration != marker.ConnectionGeneration ||
                pending.PromotedSessionId != marker.SessionId)
            {
                pending = pending with
                {
                    PromotedSessionId = marker.SessionId,
                    PromotedConnectionGeneration = marker.ConnectionGeneration,
                };
                current = current with { Pending = pending };
                WriteState(current);
            }
            if (!TryReadUpgradeMarker(promotionMarkerPath, out var promoted) ||
                promoted.Stage != "promoted" || promoted.OperationId != marker.OperationId ||
                promoted.SessionId != marker.SessionId ||
                promoted.ConnectionGeneration != marker.ConnectionGeneration)
            {
                DurableFile.ReplaceText(
                    promotionMarkerPath,
                    JsonSerializer.Serialize(marker with
                    {
                        Stage = "promoted",
                        WrittenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }, jsonOptions));
            }
            return current;
        }
        if (marker.Stage == "committed")
        {
            return current;
        }
        if (marker.Stage != "server-confirmed")
        {
            return current;
        }
        var committed = current with
        {
            Pending = null,
            ReportOperationId = pending.OperationId,
            ReportFailureCode = null,
            ConsecutiveLaunchFailures = 0,
            NextLaunchUnixMs = 0,
        };
        WriteState(committed);
        DeleteUpgradeMarkers();
        Console.WriteLine($"bootstrap: package {ShortDigest(committed.Active.Sha256)} committed");
        return committed;
    }

    async Task<ChildHandle> GetOrStartAgentAsync(
        PackageSlot slot,
        string executable,
        string? operationId,
        string? failureCode)
    {
        if (TryReadChild(out var recorded))
        {
            unrecordedLeaseId = null;
            unrecordedLeaseWait = null;
            Process? existing = null;
            try
            {
                existing = Process.GetProcessById(recorded.Pid);
                if (!existing.HasExited && IsRecordedProcess(existing, recorded))
                {
                    if (recorded.Executable == Path.GetFullPath(executable) &&
                        recorded.PackageSha256 == slot.Sha256 && recorded.OperationId == operationId)
                    {
                        WriteLease(recorded.LeaseId);
                        Console.WriteLine($"bootstrap: re-adopted Agent process {recorded.Pid}");
                        return new ChildHandle(existing, recorded.LeaseId);
                    }
                    await TerminateChildAsync(existing, operationId);
                }
            }
            catch (ArgumentException)
            {
            }
            finally
            {
                if (existing is not null && existing.HasExited)
                {
                    existing.Dispose();
                }
            }
            DeleteFile(childPath);
        }
        else if (TryReadLease(out var orphanLease))
        {
            if (!string.Equals(unrecordedLeaseId, orphanLease.LeaseId, StringComparison.Ordinal))
            {
                unrecordedLeaseId = orphanLease.LeaseId;
                unrecordedLeaseWait = Stopwatch.StartNew();
            }
            if (unrecordedLeaseWait!.Elapsed < TimeSpan.FromSeconds(16))
            {
                throw new UnrecordedLeaseException();
            }

            // The child watches this lease and must have stopped after a complete local monotonic
            // orphan window. Never derive this safety delay from the persisted wall clock.
            DeleteFile(leasePath);
            unrecordedLeaseId = null;
            unrecordedLeaseWait = null;
        }
        else
        {
            unrecordedLeaseId = null;
            unrecordedLeaseWait = null;
        }

        var leaseId = Guid.NewGuid().ToString("N");
        WriteLease(leaseId);
        Process process;
        try
        {
            process = StartAgent(slot, executable, operationId, failureCode, leaseId);
        }
        catch (Exception exception)
        {
            DeleteFile(leasePath);
            throw new CandidateLaunchException(exception);
        }
        try
        {
            if (process.HasExited)
            {
                return new ChildHandle(process, leaseId);
            }
            var childRecord = new ChildRecord(
                1,
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(),
                Path.GetFullPath(executable),
                slot.Sha256,
                operationId,
                leaseId);
            DurableFile.ReplaceText(childPath, JsonSerializer.Serialize(childRecord, jsonOptions));
            return new ChildHandle(process, leaseId);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return new ChildHandle(process, leaseId);
        }
        catch
        {
            await TerminateChildAsync(process, operationId);
            process.Dispose();
            DeleteFile(leasePath);
            throw;
        }
    }

    Process StartAgent(
        PackageSlot slot,
        string executable,
        string? operationId,
        string? failureCode,
        string leaseId)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(dataDir);
        start.ArgumentList.Add("--package-version");
        start.ArgumentList.Add(slot.Version);
        start.ArgumentList.Add("--package-sha256");
        start.ArgumentList.Add(slot.Sha256);
        start.ArgumentList.Add("--bootstrap-lease");
        start.ArgumentList.Add(leasePath);
        start.ArgumentList.Add("--bootstrap-lease-id");
        start.ArgumentList.Add(leaseId);
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            start.ArgumentList.Add("--upgrade-operation");
            start.ArgumentList.Add(operationId);
            start.ArgumentList.Add("--upgrade-health-marker");
            start.ArgumentList.Add(healthMarkerPath);
        }
        if (!string.IsNullOrWhiteSpace(failureCode))
        {
            start.ArgumentList.Add("--upgrade-failure-code");
            start.ArgumentList.Add(failureCode);
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Agent process did not start");
    }

    async Task MaintainLeaseAsync(string leaseId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!cancellationToken.IsCancellationRequested)
        {
            WriteLease(leaseId);
            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    void WriteLease(string leaseId) => DurableFile.ReplaceText(
        leasePath,
        JsonSerializer.Serialize(
            new BootstrapLease(1, leaseId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            jsonOptions));

    async Task TerminateChildAsync(Process process, string? operationId)
    {
        for (var attempt = 0; attempt < 6 && !process.HasExited; attempt++)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                Console.Error.WriteLine($"bootstrap: child termination retry ({SafeMessage(exception)})");
            }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }
        }
        if (!process.HasExited)
        {
            throw new ChildTerminationException(operationId);
        }
    }

    BootstrapState RollBack(BootstrapState current, string code)
    {
        var operationId = current.Pending?.OperationId ?? current.ReportOperationId
            ?? throw new InvalidOperationException("no upgrade operation to roll back");
        var previous = current.Pending?.Previous ?? current.Fallback
            ?? throw new InvalidOperationException("no verified fallback package is available");
        var rolledBack = new BootstrapState(
            2,
            previous,
            null,
            null,
            operationId,
            code,
            0,
            0);
        WriteState(rolledBack);
        DeleteUpgradeMarkers();
        Console.Error.WriteLine(
            $"bootstrap: operation {operationId} rolled back to {ShortDigest(previous.Sha256)} ({code})");
        return rolledBack;
    }

    BootstrapState ReportUnneededRollback(BootstrapState current, string operationId)
    {
        var acknowledged = current with
        {
            ReportOperationId = operationId,
            ReportFailureCode = "rollback_before_activation",
            ConsecutiveLaunchFailures = 0,
            NextLaunchUnixMs = 0,
        };
        WriteState(acknowledged);
        DeleteUpgradeMarkers();
        return acknowledged;
    }

    BootstrapState RecordSupervisorFailure(
        BootstrapState current,
        string? operationId,
        string failureCode)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            throw new InvalidDataException("child termination failure has no upgrade operation");
        }
        var failed = current with
        {
            // Before activation the active slot is already the exact prior and must remain eligible
            // for rollback-before-activation acknowledgement. Pending state already carries the
            // operation identity when termination failed after activation.
            ReportOperationId = current.Pending?.OperationId ?? current.ReportOperationId,
            ReportFailureCode = failureCode,
            ConsecutiveLaunchFailures = 0,
            NextLaunchUnixMs = 0,
            PendingFailureReport = new BootstrapFailureReport(1, operationId, failureCode),
        };
        WriteState(failed);
        return failed;
    }

    async Task<BootstrapState> FlushPendingFailureReportAsync(BootstrapState current)
    {
        var report = current.PendingFailureReport;
        var token = ReadAgentToken();
        if (token is null || report is null)
        {
            return current;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/bootstrap/upgrade-failure")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        report, jsonOptions),
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            {
                response.EnsureSuccessStatusCode();
            }
            var acknowledged = current with { PendingFailureReport = null };
            WriteState(acknowledged);
            return acknowledged;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"bootstrap: failure report deferred ({SafeMessage(exception)})");
            return current;
        }
    }

    BootstrapState RecordLaunchFailure(BootstrapState current)
    {
        var failures = Math.Min(current.ConsecutiveLaunchFailures + 1, 16);
        var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(failures - 1, 6)));
        var changed = current with
        {
            ConsecutiveLaunchFailures = failures,
            NextLaunchUnixMs = DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToUnixTimeMilliseconds(),
        };
        WriteState(changed);
        return changed;
    }

    BootstrapState ResetLaunchFailures(BootstrapState current)
    {
        if (current.ConsecutiveLaunchFailures == 0 && current.NextLaunchUnixMs == 0)
        {
            return current;
        }
        var changed = current with { ConsecutiveLaunchFailures = 0, NextLaunchUnixMs = 0 };
        WriteState(changed);
        return changed;
    }

    string? ResolveExecutable(PackageSlot slot)
    {
        var executable = ResolveExecutableUnchecked(slot);
        if (executable is null)
        {
            return null;
        }
        var directory = Path.GetDirectoryName(executable)!;
        if (slot.Directory == "current")
        {
            return string.Equals(
                ComputeFileSha256(executable), slot.Sha256, StringComparison.Ordinal)
                ? executable
                : null;
        }
        return VerifyExtractedPackage(directory, slot.Sha256) ? executable : null;
    }

    string? ResolveExecutableUnchecked(PackageSlot slot)
    {
        var directory = Path.GetFullPath(Path.Combine(agentDir, slot.Directory));
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(agentDir)) +
            Path.DirectorySeparatorChar;
        if (!directory.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("active package path escapes the Agent directory");
        }
        var executable = Path.Combine(directory, executableName);
        return File.Exists(executable) ? executable : null;
    }

    bool VerifyExtractedPackage(string directory, string sha256)
    {
        try
        {
            var receipt = Path.Combine(directory, ".vivarium-package-sha256");
            if (!File.Exists(Path.Combine(directory, executableName)) || !File.Exists(receipt))
            {
                return false;
            }
            var value = JsonSerializer.Deserialize<PackageReceipt>(File.ReadAllText(receipt), jsonOptions);
            if (value is null || value.SchemaVersion != 1 || value.ArchiveSha256 != sha256 ||
                value.Files.Count == 0)
            {
                return false;
            }
            var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, receipt, StringComparison.Ordinal))
                .Select(path => new PackageFileReceipt(
                    Path.GetRelativePath(directory, path).Replace('\\', '/'),
                    ComputeFileSha256(path)))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToArray();
            return actual.SequenceEqual(value.Files.OrderBy(file => file.Path, StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    void WritePackageReceipt(string directory, string sha256)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new PackageFileReceipt(
                Path.GetRelativePath(directory, path).Replace('\\', '/'),
                ComputeFileSha256(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        DurableFile.ReplaceText(
            Path.Combine(directory, ".vivarium-package-sha256"),
            JsonSerializer.Serialize(new PackageReceipt(1, sha256, files), jsonOptions));
    }

    void EnsurePackageReceipt(PackageSlot slot)
    {
        if (slot.Directory == "current")
        {
            return;
        }
        var executable = ResolveExecutableUnchecked(slot)
            ?? throw new InvalidDataException("persisted package executable is missing");
        var directory = Path.GetDirectoryName(executable)!;
        var receipt = Path.Combine(directory, ".vivarium-package-sha256");
        if (!File.Exists(receipt))
        {
            WritePackageReceipt(directory, slot.Sha256);
        }
    }

    void VerifyPersistedSlot(PackageSlot slot)
    {
        if (ResolveExecutable(slot) is null)
        {
            throw new InvalidDataException("persisted package integrity verification failed");
        }
    }

    void EnsureAvailableSpace(long requiredBytes)
    {
        var rootPath = Path.GetPathRoot(agentDir)
            ?? throw new UpgradeStageException("package_storage_capacity_unknown");
        var available = new DriveInfo(rootPath).AvailableFreeSpace;
        if (requiredBytes < 0 || available < checked(requiredBytes + minimumFreeSpaceReserve))
        {
            throw new UpgradeStageException("package_storage_capacity_insufficient");
        }
    }

    PackageSlot EnsureSlotDigest(PackageSlot slot)
    {
        if (ValidDigest(slot.Sha256))
        {
            return slot;
        }
        var executable = ResolveExecutableUnchecked(slot)
            ?? throw new InvalidDataException("legacy package executable is missing");
        return slot with { Sha256 = ComputeFileSha256(executable) };
    }

    void ValidateManifest(BootstrapManifest manifest)
    {
        var rollback = manifest.Action == "rollback";
        if (manifest.SchemaVersion != 2 || manifest.Action is not ("activate" or "rollback") ||
            manifest.OperationId is null || manifest.OperationId.Length is < 1 or > 128 ||
            manifest.Version is null || manifest.Version.Length is < 1 or > 128 ||
            manifest.Rid != rid || !ValidDigest(manifest.Sha256) ||
            !ValidDigest(manifest.PriorSha256) ||
            manifest.DeadlineUnixMs <= 0 ||
            manifest.HealthTimeoutSeconds is < 1 or > 120 ||
            !rollback && (manifest.Size is < 1 or > maximumPackageSize ||
                !Uri.TryCreate(manifest.Url, UriKind.Relative, out _) ||
                !manifest.Url.StartsWith("/bootstrap/packages/", StringComparison.Ordinal) ||
                manifest.Url.StartsWith("//", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("package manifest violates the D30 contract");
        }
    }

    void ValidateState(BootstrapState value)
    {
        if (value.SchemaVersion != 2 || value.Active is null || !ValidSlot(value.Active) ||
            value.Fallback is not null && !ValidSlot(value.Fallback) ||
            value.Pending is not null &&
                (value.Pending.OperationId is null || value.Pending.OperationId.Length is < 1 or > 128 ||
                 value.Pending.Sha256 != value.Active.Sha256 || !ValidSlot(value.Pending.Previous) ||
                 value.Pending.DeadlineUnixMs <= 0) ||
            value.PendingFailureReport is not null &&
                (value.PendingFailureReport.SchemaVersion != 1 ||
                 value.PendingFailureReport.OperationId is null ||
                 value.PendingFailureReport.OperationId.Length is < 1 or > 128 ||
                 value.PendingFailureReport.FailureCode != "child_termination_failed") ||
            value.ConsecutiveLaunchFailures is < 0 or > 16 || value.NextLaunchUnixMs < 0)
        {
            throw new InvalidDataException("active package state violates the D30 contract");
        }
    }

    bool ValidSlot(PackageSlot slot) => slot.Rid == rid && ValidDigest(slot.Sha256) &&
        slot.Version is { Length: >= 1 and <= 128 } &&
        (slot.Directory == "current" || slot.Directory == $"packages/{slot.Sha256}");

    void WriteState(BootstrapState value)
    {
        ValidateState(value);
        DurableFile.ReplaceText(statePath, JsonSerializer.Serialize(value, jsonOptions));
    }

    bool TryReadUpgradeMarker(string path, out UpgradeMarker marker)
    {
        marker = default!;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            marker = JsonSerializer.Deserialize<UpgradeMarker>(File.ReadAllText(path), jsonOptions)!;
            return marker is not null && marker.SchemaVersion == 2 &&
                marker.Stage is "ready" or "committed" or "promoted" or "server-confirmed" &&
                marker.OperationId is { Length: >= 1 and <= 128 } &&
                ValidDigest(marker.PackageSha256) &&
                marker.SessionId is { Length: >= 1 and <= 128 } && marker.ConnectionGeneration > 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    bool TryReadChild(out ChildRecord child)
    {
        child = default!;
        try
        {
            if (!File.Exists(childPath))
            {
                return false;
            }
            child = JsonSerializer.Deserialize<ChildRecord>(File.ReadAllText(childPath), jsonOptions)!;
            return child is not null && child.SchemaVersion == 1 && child.Pid > 0 &&
                child.StartedUnixMs > 0 && child.Executable is { Length: >= 1 and <= 1024 } &&
                ValidDigest(child.PackageSha256) && child.LeaseId is { Length: 32 };
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    bool IsRecordedProcess(Process process, ChildRecord child)
    {
        try
        {
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            var actualExecutable = Path.GetFullPath(process.MainModule!.FileName);
            return Math.Abs(started - child.StartedUnixMs) <= 1000 &&
                string.Equals(
                    actualExecutable,
                    Path.GetFullPath(child.Executable),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                           System.ComponentModel.Win32Exception)
        {
            throw new IOException("cannot prove recorded Agent process identity", exception);
        }
    }

    bool TryReadLease(out BootstrapLease lease)
    {
        lease = default!;
        try
        {
            if (!File.Exists(leasePath))
            {
                return false;
            }
            lease = JsonSerializer.Deserialize<BootstrapLease>(File.ReadAllText(leasePath), jsonOptions)!;
            return lease is not null && lease.SchemaVersion == 1 && lease.LeaseId is { Length: 32 };
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    string? ReadAgentToken()
    {
        if (!File.Exists(tokenPath))
        {
            return null;
        }
        var token = File.ReadAllText(tokenPath).Trim();
        return token.Length is >= 32 and <= 512 &&
               token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? token
            : null;
    }

    void DeleteUpgradeMarkers()
    {
        DeleteFile(healthMarkerPath);
        DeleteFile(promotionMarkerPath);
    }

    void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    void CleanupAbandonedTemporaryContent(BootstrapState current)
    {
        foreach (var path in Directory.EnumerateFiles(agentDir, "download-*.tmp"))
        {
            DeleteFile(path);
        }
        foreach (var path in Directory.EnumerateDirectories(packagesDir, "staging-*"))
        {
            Directory.Delete(path, recursive: true);
        }
        var retained = new[] { current.Active, current.Fallback, current.Pending?.Previous }
            .Where(slot => slot is not null && slot.Directory != "current")
            .Select(slot => Path.GetFullPath(Path.Combine(agentDir, slot!.Directory)))
            .ToHashSet(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateDirectories(packagesDir))
        {
            if (!retained.Contains(Path.GetFullPath(path)))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    string ValidateArchivePath(string raw)
    {
        var name = raw.Replace('\\', '/');
        if (name.Length is < 1 or > maximumPathLength || name.StartsWith('/') ||
            name.Contains('\0') || name.Contains(':'))
        {
            throw new UpgradeStageException("package_path_noncanonical");
        }
        var segments = name.Split('/', StringSplitOptions.None);
        var finalEmpty = segments[^1].Length == 0;
        if (segments.Take(segments.Length - (finalEmpty ? 1 : 0)).Any(segment =>
                segment.Length is < 1 or > maximumSegmentLength || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(char.IsControl) || IsWindowsReserved(segment)))
        {
            throw new UpgradeStageException("package_path_noncanonical");
        }
        return name;
    }

    static IEnumerable<string> ParentPaths(string path)
    {
        var index = path.IndexOf('/');
        while (index >= 0)
        {
            yield return path[..index];
            index = path.IndexOf('/', index + 1);
        }
    }

    static bool IsWindowsReserved(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9';
    }

    static bool NeedsLocalRollback(BootstrapState value, string operationId) =>
        value.Pending?.OperationId == operationId;

    static bool ShouldReportUnneededRollback(BootstrapState value, BootstrapManifest directive) =>
        value.Pending is null && value.Active.Sha256 == directive.PriorSha256 &&
        (value.ReportOperationId != directive.OperationId ||
         value.ReportFailureCode != "rollback_before_activation");

    static bool ValidDigest(string? digest) => digest is { Length: 64 } &&
        digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    static string FailureCode(Exception exception) => exception is UpgradeStageException stage
        ? stage.Code
        : exception switch
        {
            HttpRequestException => "package_transport_failed",
            InvalidDataException => "package_validation_failed",
            IOException => "package_storage_failed",
            _ => "package_stage_failed",
        };
}

static string CurrentRid()
{
    var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
    var arch = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("bootstrap supports only x64 and arm64"),
    };
    var candidate = $"{os}-{arch}";
    return candidate is "win-x64" or "linux-x64" or "linux-arm64" or "osx-arm64"
        ? candidate
        : throw new PlatformNotSupportedException($"bootstrap RID '{candidate}' is unsupported");
}

static string ShortDigest(string? digest) => digest is null ? "unknown" : digest[..12];

static string SafeMessage(Exception exception)
{
    var value = exception is HttpRequestException ? exception.GetType().Name : exception.Message;
    value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
    return value.Length <= 256 ? value : value[..256];
}

internal sealed record BootstrapState(
    int SchemaVersion,
    PackageSlot Active,
    PackageSlot? Fallback,
    PendingUpgrade? Pending,
    string? ReportOperationId,
    string? ReportFailureCode,
    int ConsecutiveLaunchFailures,
    long NextLaunchUnixMs)
{
    public BootstrapFailureReport? PendingFailureReport { get; init; }
}

internal sealed record PackageSlot(string Version, string Rid, string Sha256, string Directory);

internal sealed record PendingUpgrade(
    string OperationId,
    string Sha256,
    PackageSlot Previous,
    long DeadlineUnixMs,
    long ActivatedUnixMs,
    string? PromotedSessionId,
    ulong? PromotedConnectionGeneration);

internal sealed record BootstrapManifest(
    int SchemaVersion,
    string Action,
    string OperationId,
    string Version,
    string Rid,
    string Sha256,
    string PriorSha256,
    long Size,
    string Url,
    int HealthTimeoutSeconds,
    long DeadlineUnixMs);

internal sealed record UpgradeMarker(
    int SchemaVersion,
    string Stage,
    string OperationId,
    string PackageSha256,
    string SessionId,
    ulong ConnectionGeneration,
    long WrittenUnixMs);

internal sealed record BootstrapLease(int SchemaVersion, string LeaseId, long WrittenUnixMs);

internal sealed record BootstrapFailureReport(
    int SchemaVersion,
    string OperationId,
    string FailureCode);

internal sealed record ChildRecord(
    int SchemaVersion,
    int Pid,
    long StartedUnixMs,
    string Executable,
    string PackageSha256,
    string? OperationId,
    string LeaseId);

internal sealed record ChildHandle(Process Process, string LeaseId);

internal sealed record PackageReceipt(
    int SchemaVersion,
    string ArchiveSha256,
    IReadOnlyList<PackageFileReceipt> Files);

internal sealed record PackageFileReceipt(string Path, string Sha256);

internal sealed class UpgradeStageException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class CandidateLaunchException(Exception innerException) :
    Exception("candidate process did not start", innerException);

internal sealed class UnrecordedLeaseException() :
    Exception("unrecorded Agent lease has not expired yet");

internal sealed class ChildTerminationException(string? operationId) :
    IOException("Agent child did not terminate within the bounded safety window")
{
    public string? OperationId { get; } = operationId;
}

internal static class DurableFile
{
    public static void ReplaceText(string path, string value)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("durable file has no parent directory");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".tmp";
        using (var stream = new FileStream(
                   temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024,
                   FileOptions.WriteThrough))
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, fullPath, overwrite: true);
        FlushDirectory(directory);
    }

    public static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var descriptor = Open(directory, 0);
        if (descriptor < 0)
        {
            throw new IOException("could not open durable state directory", Marshal.GetLastPInvokeError());
        }
        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException("could not flush durable state directory", Marshal.GetLastPInvokeError());
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
