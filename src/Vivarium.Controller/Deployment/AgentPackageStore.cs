using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Deployment;

public sealed class AgentPackageStore
{
    public const long MaximumPackageSize = 512L * 1024 * 1024;
    private const int MaximumEntries = 2048;
    private const long MaximumExpandedSize = 2L * 1024 * 1024 * 1024;
    private const int MaximumPathLength = 512;
    private const int MaximumSegmentLength = 255;
    private readonly string root;
    private readonly VivariumDatabase database;
    private readonly TimeProvider timeProvider;
    private readonly bool developmentPublicationEnabled;
    private readonly object releaseGate = new();
    private IReadOnlyDictionary<string, AgentPackage> currentReleasePackages =
        new Dictionary<string, AgentPackage>(StringComparer.Ordinal);

    public AgentPackageStore(
        string dataDir,
        VivariumDatabase database,
        TimeProvider timeProvider,
        bool developmentPublicationEnabled = false)
    {
        root = Path.Combine(dataDir, "agent-packages");
        Directory.CreateDirectory(root);
        this.database = database;
        this.timeProvider = timeProvider;
        this.developmentPublicationEnabled = developmentPublicationEnabled;
    }

    public async Task<AgentPackagePublication> PublishAsync(
        ManagementRequestContext context,
        string version,
        string rid,
        Stream content,
        string? expectedSha256,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(content);
        version = ValidateVersion(version);
        rid = ValidateRid(rid);
        expectedSha256 = NormalizeOptionalDigest(expectedSha256);
        source = BoundSource(source);

        var temporary = Path.Combine(root, $"upload-{Guid.NewGuid():N}.tmp");
        string digest;
        long size;
        try
        {
            (digest, size) = await CopyAndHashAsync(content, temporary, cancellationToken);
            if (expectedSha256 is not null &&
                !string.Equals(expectedSha256, digest, StringComparison.Ordinal))
            {
                throw new AgentPackageException(
                    "agent_package_digest_mismatch",
                    "The uploaded package does not match the declared SHA-256 digest.");
            }

            ValidateArchive(temporary, rid);
            var requestId = context.RequestId ?? $"content:{digest}";
            var requestHash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"agent-package\n{version}\n{rid}\n{digest}")))
                .ToLowerInvariant();
            var finalPath = ContentPath(digest);
            if (File.Exists(finalPath) && !FileMatches(finalPath, digest, size))
            {
                File.Move(temporary, finalPath, overwrite: true);
            }
            else if (!File.Exists(finalPath))
            {
                try
                {
                    File.Move(temporary, finalPath);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    if (!FileMatches(finalPath, digest, size))
                    {
                        File.Move(temporary, finalPath, overwrite: true);
                    }
                }
            }

            var packageId = Guid.NewGuid().ToString("N");
            var createdAt = timeProvider.GetUtcNow();
            return await database.WriteAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();
                var requestReplay = FindRequest(
                    connection,
                    transaction,
                    context.Principal.ActorType,
                    context.Principal.ActorId,
                    requestId);
                if (requestReplay is not null)
                {
                    if (!string.Equals(requestReplay.Value.RequestHash, requestHash, StringComparison.Ordinal))
                    {
                        throw new AgentPackageException(
                            "idempotency_key_reused",
                            "The Idempotency-Key was already used for different package bytes.",
                            StatusCodes.Status409Conflict);
                    }

                    transaction.Commit();
                    return new AgentPackagePublication(
                        FindById(connection, null, requestReplay.Value.PackageId)!,
                        Replayed: true);
                }

                var existing = FindIdentity(connection, transaction, version, rid, digest);
                AgentPackage package;
                if (existing is null)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO agent_packages(
                            package_id, version, rid, sha256, size, source,
                            actor_type, actor_id, correlation_id, created_unix_ms)
                        VALUES (
                            $packageId, $version, $rid, $sha256, $size, $source,
                            $actorType, $actorId, $correlationId, $created);
                        """;
                    insert.Parameters.AddWithValue("$packageId", packageId);
                    insert.Parameters.AddWithValue("$version", version);
                    insert.Parameters.AddWithValue("$rid", rid);
                    insert.Parameters.AddWithValue("$sha256", digest);
                    insert.Parameters.AddWithValue("$size", size);
                    insert.Parameters.AddWithValue("$source", source);
                    insert.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
                    insert.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
                    insert.Parameters.AddWithValue("$correlationId", context.CorrelationId);
                    insert.Parameters.AddWithValue("$created", createdAt.ToUnixTimeMilliseconds());
                    insert.ExecuteNonQuery();
                    package = new AgentPackage(
                        packageId, version, rid, digest, size, createdAt, source);
                }
                else
                {
                    package = existing;
                }

                using (var receipt = connection.CreateCommand())
                {
                    receipt.Transaction = transaction;
                    receipt.CommandText = """
                        INSERT INTO agent_package_publication_requests(
                            actor_type, actor_id, request_id, request_hash, package_id, created_unix_ms)
                        VALUES ($actorType, $actorId, $requestId, $requestHash, $packageId, $created);
                        """;
                    receipt.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
                    receipt.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
                    receipt.Parameters.AddWithValue("$requestId", requestId);
                    receipt.Parameters.AddWithValue("$requestHash", requestHash);
                    receipt.Parameters.AddWithValue("$packageId", package.PackageId);
                    receipt.Parameters.AddWithValue("$created", createdAt.ToUnixTimeMilliseconds());
                    receipt.ExecuteNonQuery();
                }
                AuditEventStore.Append(connection, transaction, AuditEventDraft.Create(
                    context,
                    createdAt,
                    "agent-package.publish",
                    "agent-package",
                    package.PackageId,
                    existing is null ? AuditOutcome.Succeeded : AuditOutcome.NoChange,
                    existing is null ? "" : "content_already_published",
                    new Dictionary<string, string>
                    {
                        ["rid"] = package.Rid,
                        ["version"] = package.Version,
                        ["sha256"] = package.Sha256,
                        ["size"] = package.Size.ToString(
                            global::System.Globalization.CultureInfo.InvariantCulture),
                    }));
                transaction.Commit();
                return new AgentPackagePublication(package, Replayed: existing is not null);
            });
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public Task<AgentPackage?> FindAsync(string packageId) => database.ReadAsync(connection =>
        FindById(connection, null, packageId));

    public AgentPackage? FindCurrentRelease(string rid)
    {
        rid = ValidateRid(rid);
        lock (releaseGate)
        {
            return currentReleasePackages.GetValueOrDefault(rid);
        }
    }

    public async Task<AgentPackagePublication> PublishDevelopmentAsync(
        ManagementRequestContext context,
        string version,
        string rid,
        Stream content,
        string? expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (!developmentPublicationEnabled)
        {
            throw new AgentPackageException(
                "development_agent_package_api_disabled",
                "Development Agent package publication is disabled.",
                StatusCodes.Status404NotFound);
        }

        var publication = await PublishAsync(
            context,
            version,
            rid,
            content,
            expectedSha256,
            "development",
            cancellationToken);
        lock (releaseGate)
        {
            var updated = new Dictionary<string, AgentPackage>(
                currentReleasePackages, StringComparer.Ordinal)
            {
                [publication.Package.Rid] = publication.Package,
            };
            currentReleasePackages = updated;
        }

        return publication;
    }

    public Task<IReadOnlyList<AgentPackage>> ListAsync() => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT package_id, version, rid, sha256, size, source, created_unix_ms
            FROM agent_packages
            ORDER BY created_unix_ms DESC, package_id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<AgentPackage>();
        while (reader.Read())
        {
            result.Add(ReadPackage(reader));
        }

        return (IReadOnlyList<AgentPackage>)result;
    });

    public string? ResolveContentPath(AgentPackage package)
    {
        var path = ContentPath(package.Sha256);
        return FileMatches(path, package.Sha256, package.Size) ? path : null;
    }

    public async Task ImportBundledCatalogAsync(
        string catalogPath,
        string serverVersion,
        CancellationToken cancellationToken = default)
    {
        var fullCatalog = Path.GetFullPath(catalogPath);
        var catalogRoot = Path.GetDirectoryName(fullCatalog)
            ?? throw new AgentPackageException(
                "agent_package_catalog_invalid", "The bundled package catalog has no directory.");
        await using var catalogStream = File.OpenRead(fullCatalog);
        var catalog = await JsonSerializer.DeserializeAsync<AgentPackageCatalogDocument>(
            catalogStream, RestJson.SerializerOptions, cancellationToken)
            ?? throw new AgentPackageException(
                "agent_package_catalog_invalid", "The bundled package catalog is empty.");
        if (catalog.SchemaVersion != 1 || catalog.Packages is null || catalog.Packages.Count > 32)
        {
            throw new AgentPackageException(
                "agent_package_catalog_invalid",
                "The bundled package catalog must use schemaVersion 1 and contain at most 32 packages.");
        }

        serverVersion = ValidateVersion(serverVersion);
        var catalogRids = catalog.Packages.Select(entry => ValidateRid(entry.Rid)).ToArray();
        if (catalogRids.Length != AgentPackageRids.Supported.Count ||
            catalogRids.Distinct(StringComparer.Ordinal).Count() != catalogRids.Length ||
            !catalogRids.ToHashSet(StringComparer.Ordinal).SetEquals(AgentPackageRids.Supported))
        {
            throw new AgentPackageException(
                "agent_package_catalog_incomplete",
                "The bundled package catalog must contain exactly one package for every supported RID.");
        }
        if (catalog.Packages.Any(entry =>
                !string.Equals(ValidateVersion(entry.Version), serverVersion, StringComparison.Ordinal)))
        {
            throw new AgentPackageException(
                "agent_package_catalog_version_mismatch",
                $"Every bundled Agent package must match Server version '{serverVersion}'.");
        }

        var imported = new Dictionary<string, AgentPackage>(StringComparer.Ordinal);

        foreach (var entry in catalog.Packages)
        {
            var relative = entry.File.Replace('\\', '/');
            if (relative.StartsWith('/') || relative.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new AgentPackageException(
                    "agent_package_catalog_path_invalid",
                    "A bundled package path escapes or is not canonical.");
            }

            var packagePath = Path.GetFullPath(Path.Combine(catalogRoot, relative));
            var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalogRoot)) +
                Path.DirectorySeparatorChar;
            if (!packagePath.StartsWith(
                    rootPrefix,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new AgentPackageException(
                    "agent_package_catalog_path_invalid",
                    "A bundled package path escapes the catalog directory.");
            }

            await using var content = File.OpenRead(packagePath);
            var publication = await PublishAsync(
                ManagementRequestContext.System(
                    "bundled-agent-package-import",
                    $"bundle:{entry.Rid}:{entry.Version}:{entry.Sha256}"),
                entry.Version,
                entry.Rid,
                content,
                entry.Sha256,
                "bundled",
                cancellationToken);
            imported.Add(publication.Package.Rid, publication.Package);
        }

        lock (releaseGate)
        {
            currentReleasePackages = imported;
        }
    }

    private static AgentPackage? FindIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string version,
        string rid,
        string digest)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT package_id, version, rid, sha256, size, source, created_unix_ms
            FROM agent_packages
            WHERE version = $version AND rid = $rid AND sha256 = $sha256;
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$rid", rid);
        command.Parameters.AddWithValue("$sha256", digest);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPackage(reader) : null;
    }

    private static AgentPackage? FindById(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string packageId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT package_id, version, rid, sha256, size, source, created_unix_ms
            FROM agent_packages WHERE package_id = $packageId;
            """;
        command.Parameters.AddWithValue("$packageId", packageId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPackage(reader) : null;
    }

    private static (string PackageId, string RequestHash)? FindRequest(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actorType,
        string actorId,
        string requestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT package_id, request_hash FROM agent_package_publication_requests
            WHERE actor_type = $actorType AND actor_id = $actorId AND request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$actorType", actorType);
        command.Parameters.AddWithValue("$actorId", actorId);
        command.Parameters.AddWithValue("$requestId", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static AgentPackage ReadPackage(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
        reader.GetString(5));

    private static async Task<(string Digest, long Size)> CopyAndHashAsync(
        Stream source,
        string destination,
        CancellationToken cancellationToken)
    {
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
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            size = checked(size + read);
            if (size > MaximumPackageSize)
            {
                throw new AgentPackageException(
                    "agent_package_too_large",
                    $"Agent packages are limited to {MaximumPackageSize} bytes.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        if (size == 0)
        {
            throw new AgentPackageException(
                "agent_package_empty", "An Agent package cannot be empty.");
        }

        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
    }

    private static void ValidateArchive(string path, string rid)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntries)
            {
                throw new AgentPackageException(
                    "agent_package_archive_invalid",
                    $"An Agent package must contain between 1 and {MaximumEntries} entries.");
            }

            var expectedExecutable = rid == "win-x64" ? "vivarium-agent.exe" : "vivarium-agent";
            var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            var hasExecutable = false;
            foreach (var entry in archive.Entries)
            {
                var name = ValidatePortableArchivePath(entry.FullName);
                var isDirectory = name.EndsWith('/');
                var canonical = isDirectory ? name.TrimEnd('/') : name;
                if (!names.TryAdd(canonical, isDirectory) ||
                    !isDirectory && names.Keys.Any(path =>
                        path.StartsWith(canonical + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new AgentPackageException(
                        "agent_package_archive_path_invalid",
                        "The Agent package contains a noncanonical, duplicate, or escaping path.");
                }
                foreach (var parent in ParentPaths(canonical))
                {
                    if (names.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                    {
                        throw new AgentPackageException(
                            "agent_package_archive_path_invalid",
                            "The Agent package contains a file/directory path conflict.");
                    }
                }

                var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixMode is not (0 or 0x4000 or 0x8000) ||
                    isDirectory && unixMode == 0x8000 || !isDirectory && unixMode == 0x4000)
                {
                    throw new AgentPackageException(
                        "agent_package_archive_special_file_rejected",
                        "Agent packages may contain only regular files and directories.");
                }

                expanded = checked(expanded + entry.Length);
                if (expanded > MaximumExpandedSize ||
                    entry.Length > checked(entry.CompressedLength * 100 + 1024 * 1024))
                {
                    throw new AgentPackageException(
                        "agent_package_archive_expanded_too_large",
                        "The expanded Agent package exceeds the safety limit.");
                }

                hasExecutable |= !isDirectory &&
                    string.Equals(name, expectedExecutable, StringComparison.Ordinal);
            }

            if (!hasExecutable)
            {
                throw new AgentPackageException(
                    "agent_package_executable_missing",
                    $"The Agent package must contain '{expectedExecutable}' at its root.");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            throw new AgentPackageException(
                "agent_package_archive_invalid",
                "The uploaded Agent package is not a valid ZIP archive.")
            {
                Source = exception.Source,
            };
        }
    }

    private string ContentPath(string digest) => Path.Combine(root, $"{digest}.zip");

    private static bool FileMatches(string path, string digest, long size)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != size)
            {
                return false;
            }
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(actual, digest, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string ValidatePortableArchivePath(string raw)
    {
        var name = raw.Replace('\\', '/');
        if (name.Length is < 1 or > MaximumPathLength || name.StartsWith('/') ||
            name.Contains('\0') || name.Contains(':'))
        {
            throw new AgentPackageException(
                "agent_package_archive_path_invalid", "An Agent package path is not portable.");
        }
        var segments = name.Split('/', StringSplitOptions.None);
        var finalEmpty = segments[^1].Length == 0;
        if (segments.Take(segments.Length - (finalEmpty ? 1 : 0)).Any(segment =>
                segment.Length is < 1 or > MaximumSegmentLength || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(char.IsControl) || IsWindowsReserved(segment)))
        {
            throw new AgentPackageException(
                "agent_package_archive_path_invalid", "An Agent package path is not portable.");
        }
        return name;
    }

    private static IEnumerable<string> ParentPaths(string path)
    {
        var index = path.IndexOf('/');
        while (index >= 0)
        {
            yield return path[..index];
            index = path.IndexOf('/', index + 1);
        }
    }

    private static bool IsWindowsReserved(string segment)
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

    private static string ValidateVersion(string version)
    {
        version = version?.Trim() ?? string.Empty;
        if (version.Length is < 1 or > 128 || version.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '+' and not '_'))
        {
            throw new AgentPackageException(
                "agent_package_version_invalid",
                "Package version must be 1-128 portable identifier characters.");
        }

        return version;
    }

    private static string ValidateRid(string rid)
    {
        rid = rid?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AgentPackageRids.Supported.Contains(rid))
        {
            throw new AgentPackageException(
                "agent_package_rid_unsupported", $"Package RID '{rid}' is not supported.");
        }

        return rid;
    }

    private static string? NormalizeOptionalDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        digest = digest.Trim().ToLowerInvariant();
        if (digest.Length != 64 || digest.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new AgentPackageException(
                "agent_package_digest_invalid", "SHA-256 must be 64 hexadecimal characters.");
        }

        return digest;
    }

    private static string BoundSource(string source)
    {
        source = string.IsNullOrWhiteSpace(source) ? "api" : source.Trim();
        if (source.Length > 64 || source.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new AgentPackageException(
                "agent_package_source_invalid", "Package source is invalid.");
        }

        return source;
    }
}
