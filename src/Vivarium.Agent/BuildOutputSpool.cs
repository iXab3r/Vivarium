using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>Private, bounded post-mask output retained independently of the terminal result.</summary>
internal sealed class BuildOutputSpool
{
    internal const long DefaultCapacityBytes = 128L * 1024 * 1024;
    private const int MaximumChunkBytes = 64 * 1024;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object gate = new();
    private readonly string directory;
    private readonly State state;
    private readonly Dictionary<(int, LogStream), ExactMasker> masks;
    private readonly bool producer;

    private BuildOutputSpool(string directory, State state, IReadOnlyList<ByteString> secrets, bool producer)
    {
        this.directory = directory; this.state = state; this.producer = producer;
        masks = state.Scopes.ToDictionary(s => (s.Step, s.Stream), _ => new ExactMasker(secrets));
    }

    internal static BuildOutputSpool Create(string dataDirectory, string buildId, int steps,
        IReadOnlyList<ByteString> secrets, long capacityBytes = DefaultCapacityBytes, int maximumChunks = 4096)
    {
        if (steps is < 0 or > 128 || capacityBytes is < 2 or > DefaultCapacityBytes || maximumChunks is < 2 or > 4096)
            throw new InvalidDataException("Build output bounds are invalid.");
        var directory = DirectoryFor(dataDirectory, buildId);
        var parent = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(parent) && Directory.EnumerateFileSystemEntries(parent).Any())
            throw new InvalidDataException("Another Build still owns the private output spool.");
        var state = new State { BuildId = buildId, Capacity = capacityBytes, MaximumChunks = maximumChunks,
            Scopes = Enumerable.Range(0, steps).SelectMany(step => new[]
            { new Scope { Step = step, Stream = LogStream.Stdout }, new Scope { Step = step, Stream = LogStream.Stderr } }).ToArray() };
        var result = new BuildOutputSpool(directory, state, secrets, producer: true);
        PrivateStorage.EnsureDirectory(directory); result.Save(); return result;
    }

    internal static BuildOutputSpool Open(string dataDirectory, string buildId)
    {
        var directory = DirectoryFor(dataDirectory, buildId);
        var manifest = Path.Combine(directory, "manifest.json");
        if (new FileInfo(manifest).Length > MaximumManifestBytes) throw new InvalidDataException("Build output metadata exceeds its bound.");
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(manifest), Json)
            ?? throw new InvalidDataException("Build output metadata is missing.");
        if (state.Version != 1 || state.BuildId != buildId || state.Capacity is < 2 or > DefaultCapacityBytes
            || state.MaximumChunks is < 2 or > 4096 || state.Scopes.Length > 256
            || state.Scopes.Length % 2 != 0 || state.Scopes.Select(s => (s.Step, s.Stream)).Distinct().Count() != state.Scopes.Length)
            throw new InvalidDataException("Build output metadata is invalid.");
        foreach (var scope in state.Scopes)
        {
            if (scope.Step < 0 || scope.Step >= state.Scopes.Length / 2 || scope.Stream is not (LogStream.Stdout or LogStream.Stderr)
                || scope.Acknowledged > scope.NextSequence || scope.AcknowledgedBytes > scope.Produced
                || scope.Entries.Count > state.MaximumChunks * 2 + 1)
                throw new InvalidDataException("Build output scope is invalid.");
            var position = scope.Acknowledged;
            var bytePosition = scope.AcknowledgedBytes;
            for (var index = 0; index < scope.Entries.Count; index++)
            {
                var entry = scope.Entries[index];
                if ((index == 0 ? entry.Start > position : entry.Start != position) || entry.End <= position || entry.End > scope.NextSequence
                    || (index == 0 ? entry.ByteStart > bytePosition : entry.ByteStart != bytePosition)
                    || entry.ByteEnd <= bytePosition || entry.ByteEnd > scope.Produced || entry.ByteEnd <= entry.ByteStart
                    || entry.End <= entry.Start || entry.Ordinal >= state.NextOrdinal
                    || entry.Hash is not null && (!Regex.IsMatch(entry.Hash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)
                        || entry.End - entry.Start != 1 || entry.ByteEnd - entry.ByteStart > MaximumChunkBytes || entry.Gap.Length != 0)
                    || entry.Hash is null && entry.Gap is not ("spool-quota" or "spool-unavailable"))
                    throw new InvalidDataException("Build output range is invalid.");
                position = entry.End;
                bytePosition = entry.ByteEnd;
            }
            if (position != scope.NextSequence || bytePosition != scope.Produced) throw new InvalidDataException("Build output contains an unrecorded gap.");
        }
        var data = state.Scopes.SelectMany(s => s.Entries).Where(e => e.Hash is not null).ToArray();
        if (data.Length > state.MaximumChunks || data.Sum(e => checked((long)(e.ByteEnd - e.ByteStart))) > state.Capacity)
            throw new InvalidDataException("Build output exceeds its retained quota.");
        return new(directory, state, [], producer: false);
    }

    internal void Append(int step, LogStream stream, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            if (state.Sealed || state.ProducerLost || !producer) throw new InvalidOperationException("Build output producer is closed.");
            if (bytes.Length > MaximumChunkBytes) throw new InvalidDataException("Build output input exceeds its chunk bound.");
            var scope = Find(step, stream);
            Emit(scope, masks[(step, stream)].Feed(bytes, final: false));
        }
    }

    internal IReadOnlyList<BuildOutputWatermark> Seal()
    {
        lock (gate)
        {
            if (state.ProducerLost) throw new InvalidOperationException("A lost producer has no final watermark.");
            if (!state.Sealed)
            {
                if (!producer) throw new InvalidOperationException("A lost producer has no final watermark.");
                foreach (var scope in state.Scopes) Emit(scope, masks[(scope.Step, scope.Stream)].Feed([], final: true));
                state.Sealed = true; Save();
            }
            return state.Scopes.Select(s => new BuildOutputWatermark { StepIndex = s.Step, Stream = s.Stream,
                FinalSequenceExclusive = s.NextSequence, ProducedBytes = s.Produced }).ToArray();
        }
    }

    internal BuildOutput[] Snapshot(string sessionId, int maximumChunks = 32)
    {
        lock (gate)
        {
            if (maximumChunks is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
            var result = new List<BuildOutput>();
            foreach (var scope in state.Scopes)
            foreach (var entry in scope.Entries)
            {
                if (result.Count == maximumChunks) return result.ToArray();
                var start = Math.Max(entry.Start, scope.Acknowledged);
                var byteStart = Math.Max(entry.ByteStart, scope.AcknowledgedBytes);
                byte[]? bytes = null;
                if (entry.Hash is not null)
                {
                    try
                    {
                        var path = Content(scope, entry);
                        if (new FileInfo(path).Length != checked((long)(entry.ByteEnd - entry.ByteStart))) throw new InvalidDataException();
                        bytes = File.ReadAllBytes(path);
                        if (Hash(bytes) != entry.Hash) throw new InvalidDataException();
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        entry.Hash = null; entry.Gap = "spool-unavailable"; state.Unavailable = true; TrySave();
                    }
                }
                result.Add(new BuildOutput { BuildId = state.BuildId, SessionId = sessionId,
                    StepIndex = scope.Step, Stream = scope.Stream, SourceSequenceStart = start,
                    SourceSequenceEndExclusive = entry.End, ProducedBytes = entry.ByteEnd - byteStart,
                    SourceByteStart = byteStart, SourceByteEndExclusive = entry.ByteEnd,
                    GapReason = entry.Gap, Data = entry.Hash is null ? ByteString.Empty
                        : ByteString.CopyFrom(bytes!) });
            }
            return result.ToArray();
        }
    }

    internal void Acknowledge(int step, LogStream stream, ulong exclusive, ulong producedBytes)
    {
        lock (gate)
        {
            var scope = Find(step, stream);
            if (exclusive > scope.NextSequence || producedBytes > scope.Produced) throw new InvalidDataException("Output ACK exceeds produced output.");
            if (exclusive <= scope.Acknowledged) return;
            if (producedBytes <= scope.AcknowledgedBytes) throw new InvalidDataException("Output ACK byte offset regressed.");
            var boundary = scope.Entries.Single(e => e.Start < exclusive && exclusive <= e.End);
            if (exclusive == boundary.End ? producedBytes != boundary.ByteEnd
                : boundary.Hash is not null || producedBytes <= boundary.ByteStart || producedBytes >= boundary.ByteEnd)
                throw new InvalidDataException("Output ACK has an inconsistent byte offset.");
            var retired = scope.Entries.Where(e => e.End <= exclusive).ToArray();
            scope.Acknowledged = exclusive;
            scope.AcknowledgedBytes = producedBytes;
            scope.Entries.RemoveAll(e => e.End <= exclusive);
            Save(); // Do not delete the only local copy before the cumulative receipt is durable.
            foreach (var entry in retired) DeleteFile(Content(scope, entry));
        }
    }

    internal void MarkResultAccepted() { lock (gate) { state.ResultAccepted = true; Save(); } }
    internal void MarkProducerLost() { lock (gate) { state.ProducerLost = true; Save(); } }
    internal bool ProducerLost { get { lock (gate) return state.ProducerLost; } }
    internal bool CanRelease { get { lock (gate) return (state.Sealed || state.ProducerLost) && state.ResultAccepted
        && state.Scopes.All(s => s.Acknowledged == s.NextSequence && s.AcknowledgedBytes == s.Produced); } }

    internal static BuildOutputSpool? Restore(string dataDirectory, string? pendingBuildId)
    {
        var parent = Path.Combine(Path.GetFullPath(dataDirectory), "build-output");
        if (!Directory.Exists(parent)) return null;
        var directories = Directory.GetDirectories(parent);
        if (directories.Length == 0) return null;
        if (directories.Length != 1) throw new InvalidDataException("Conflicting private output owners.");
        var manifest = Path.Combine(directories[0], "manifest.json");
        if (pendingBuildId is null && !Directory.EnumerateFileSystemEntries(directories[0]).Any()
            && Regex.IsMatch(Path.GetFileName(directories[0]), "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)
            && (File.GetAttributes(directories[0]) & FileAttributes.ReparsePoint) == 0)
        { Directory.Delete(directories[0]); return null; }
        if (new FileInfo(manifest).Length > MaximumManifestBytes) throw new InvalidDataException("Output metadata exceeds its bound.");
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(manifest), Json)
            ?? throw new InvalidDataException("Build output metadata is missing.");
        if (!string.Equals(directories[0], DirectoryFor(dataDirectory, state.BuildId),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Private output directory differs from its owner.");
        var result = Open(dataDirectory, state.BuildId);
        if (pendingBuildId == state.BuildId) return result;
        if (pendingBuildId is null && result.CanRelease) { result.DeleteCompleted(); return null; }
        throw new InvalidDataException("Unreconciled private output owner.");
    }

    internal void DeleteCompleted()
    {
        lock (gate)
        {
            if (!CanRelease) throw new InvalidOperationException("Build output is not acknowledged.");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Build output directory changed identity.");
            var files = Directory.GetFiles(directory);
            if (files.Any(p => !Regex.IsMatch(Path.GetFileName(p),
                "^(?:[0-9]+-[01]-[0-9]+\\.bin(?:\\.tmp)?|manifest\\.json(?:\\.tmp)?)$", RegexOptions.CultureInvariant)))
                throw new InvalidDataException("Unexpected private output file.");
            foreach (var file in files.Where(p => Path.GetFileName(p) != "manifest.json")) File.Delete(file);
            File.Delete(Path.Combine(directory, "manifest.json")); // Keep the receipt until content cleanup has completed.
            Directory.Delete(directory); // Exact derived directory, no recursive filesystem operation.
        }
    }

    private void Emit(Scope scope, byte[] bytes)
    {
        var maximum = checked((int)Math.Min(MaximumChunkBytes, state.Capacity / 2));
        for (var offset = 0; offset < bytes.Length; offset += maximum)
        {
            var data = bytes.AsSpan(offset, Math.Min(maximum, bytes.Length - offset));
            var entry = new Entry { Start = scope.NextSequence, End = checked(scope.NextSequence + 1),
                ByteStart = scope.Produced, ByteEnd = checked(scope.Produced + (ulong)data.Length),
                Ordinal = state.NextOrdinal++ };
            try
            {
                if (state.Unavailable) throw new IOException("Private output storage is unavailable.");
                var retained = state.Scopes.SelectMany(s => s.Entries).Where(e => e.Hash is not null).ToArray();
                entry.Head = retained.Count(e => e.Head) < state.MaximumChunks / 2
                    && retained.Where(e => e.Head).Sum(e => checked((long)(e.ByteEnd - e.ByteStart))) + data.Length <= state.Capacity / 2;
                while (retained.Length >= state.MaximumChunks
                    || retained.Sum(e => checked((long)(e.ByteEnd - e.ByteStart))) + data.Length > state.Capacity)
                {
                    var victim = state.Scopes.SelectMany(s => s.Entries.Select(e => (Scope: s, Entry: e)))
                        .Where(p => p.Entry.Hash is not null && !p.Entry.Head).OrderBy(p => p.Entry.Ordinal).First();
                    var path = Content(victim.Scope, victim.Entry);
                    victim.Entry.Hash = null; victim.Entry.Head = false; victim.Entry.Gap = "spool-quota";
                    Coalesce(victim.Scope); Save(); DeleteFile(path);
                    retained = state.Scopes.SelectMany(s => s.Entries).Where(e => e.Hash is not null).ToArray();
                }
                var target = Content(scope, entry);
                using (var file = new FileStream(target + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                { file.Write(data); file.Flush(flushToDisk: true); }
                File.Move(target + ".tmp", target);
                entry.Hash = Hash(data);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                entry.Hash = null; entry.Head = false; entry.Gap = "spool-unavailable"; state.Unavailable = true;
            }
            scope.NextSequence = entry.End; scope.Produced = entry.ByteEnd;
            scope.Entries.Add(entry); Coalesce(scope); TrySave();
        }
    }

    private static void Coalesce(Scope scope)
    {
        for (var i = scope.Entries.Count - 1; i > 0; i--)
        {
            var previous = scope.Entries[i - 1]; var current = scope.Entries[i];
            if (previous.Hash is not null || current.Hash is not null || previous.Gap != current.Gap || previous.End != current.Start) continue;
            previous.End = current.End; previous.ByteEnd = current.ByteEnd; scope.Entries.RemoveAt(i);
        }
    }

    private Scope Find(int step, LogStream stream) => state.Scopes.SingleOrDefault(s => s.Step == step && s.Stream == stream)
        ?? throw new InvalidDataException("Build output scope is not declared.");
    private void Save()
    {
        var json = JsonSerializer.Serialize(state, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes) throw new InvalidDataException("Output metadata exceeds its bound.");
        DurableFile.ReplaceText(Path.Combine(directory, "manifest.json"), json);
    }
    private void TrySave()
    {
        try { Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { state.Unavailable = true; }
    }
    private static void DeleteFile(string path) { if (File.Exists(path)) File.Delete(path); }
    private string Content(Scope scope, Entry entry) => Path.Combine(directory, $"{scope.Step}-{(int)scope.Stream}-{entry.Start}.bin");
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string DirectoryFor(string dataDirectory, string buildId)
    {
        if (string.IsNullOrWhiteSpace(buildId) || buildId.Length > 256 || buildId.Any(char.IsControl))
            throw new InvalidDataException("Build output identity is invalid.");
        return Path.Combine(Path.GetFullPath(dataDirectory), "build-output", Hash(Encoding.UTF8.GetBytes(buildId)));
    }

    private sealed class State
    {
        public int Version { get; set; } = 1;
        public required string BuildId { get; set; }
        public long Capacity { get; set; }
        public int MaximumChunks { get; set; }
        public bool Sealed { get; set; }
        public bool ResultAccepted { get; set; }
        public bool ProducerLost { get; set; }
        public bool Unavailable { get; set; }
        public ulong NextOrdinal { get; set; }
        public required Scope[] Scopes { get; set; }
    }
    private sealed class Scope
    {
        public int Step { get; set; }
        public LogStream Stream { get; set; }
        public ulong Produced { get; set; }
        public ulong NextSequence { get; set; }
        public ulong Acknowledged { get; set; }
        public ulong AcknowledgedBytes { get; set; }
        public List<Entry> Entries { get; set; } = [];
    }
    private sealed class Entry
    {
        public ulong Start { get; set; }
        public ulong End { get; set; }
        public ulong ByteStart { get; set; }
        public ulong ByteEnd { get; set; }
        public ulong Ordinal { get; set; }
        public bool Head { get; set; }
        public string? Hash { get; set; }
        public string Gap { get; set; } = "";
    }

    private sealed class ExactMasker
    {
        private readonly byte[][] patterns;
        private readonly int longest;
        private byte[] pending = [];
        internal ExactMasker(IReadOnlyList<ByteString> secrets)
        {
            if (secrets.Count > 64 || secrets.Any(s => s.Length is < 1 or > 4096))
                throw new InvalidDataException("Registered output masks exceed their bound.");
            patterns = secrets.Distinct().OrderByDescending(s => s.Length).Select(s => s.ToByteArray()).ToArray();
            longest = patterns.Select(p => p.Length).DefaultIfEmpty(1).Max();
        }
        internal byte[] Feed(ReadOnlySpan<byte> bytes, bool final)
        {
            var input = new byte[pending.Length + bytes.Length]; pending.CopyTo(input, 0); bytes.CopyTo(input.AsSpan(pending.Length));
            var limit = final ? input.Length : Math.Max(0, input.Length - longest + 1);
            using var output = new MemoryStream();
            var position = 0;
            while (position < limit)
            {
                var match = patterns.FirstOrDefault(pattern => input.AsSpan(position).StartsWith(pattern));
                if (match is null) output.WriteByte(input[position++]);
                else { output.Write("[REDACTED]"u8); position += match.Length; }
            }
            pending = input[position..]; return output.ToArray();
        }
    }
}
