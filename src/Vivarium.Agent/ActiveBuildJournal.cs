using System.Diagnostics;
using System.Text.Json;

namespace Vivarium.Agent;

internal sealed record ActiveBuildJournalRecord(
    int SchemaVersion,
    string BuildId,
    int? ProcessId,
    long? ProcessStartUtcTicks);

/// <summary>
/// Durable local evidence that an accepted Build may still own host processes. It is deliberately
/// conservative: uncertain PID/start-time evidence is never converted into a clean-host claim.
/// </summary>
internal sealed class ActiveBuildJournal
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    private readonly string path;
    private readonly object gate = new();
    private ActiveBuildJournalRecord? current;

    public ActiveBuildJournal(string dataDir)
    {
        path = Path.Combine(dataDir, "active-build.json");
        current = Read();
    }

    public ActiveBuildJournalRecord? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Accept(string buildId)
    {
        var record = new ActiveBuildJournalRecord(1, buildId, null, null);
        Write(record);
    }

    public void ObserveProcess(string buildId, Process? process)
    {
        ActiveBuildJournalRecord record;
        if (process is null)
        {
            record = new ActiveBuildJournalRecord(1, buildId, null, null);
        }
        else
        {
            long startTicks;
            try
            {
                startTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               System.ComponentModel.Win32Exception or
                                               NotSupportedException)
            {
                throw new WorkloadTerminationException(
                    "could not record the started workload process identity", exception);
            }
            record = new ActiveBuildJournalRecord(1, buildId, process.Id, startTicks);
        }
        Write(record);
    }

    public void Complete(string buildId)
    {
        lock (gate)
        {
            if (current?.BuildId != buildId)
            {
                return;
            }
            File.Delete(path);
            current = null;
        }
    }

    private void Write(ActiveBuildJournalRecord record)
    {
        lock (gate)
        {
            DurableFile.ReplaceText(path, JsonSerializer.Serialize(record, JsonOptions));
            current = record;
        }
    }

    private ActiveBuildJournalRecord? Read()
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var record = JsonSerializer.Deserialize<ActiveBuildJournalRecord>(
                File.ReadAllText(path), JsonOptions);
            if (record is null || record.SchemaVersion != 1 ||
                record.BuildId.Length is < 1 or > 256 ||
                record.BuildId.Any(character => character is '\r' or '\n' or '\0') ||
                (record.ProcessId is null) != (record.ProcessStartUtcTicks is null) ||
                record.ProcessId is <= 0 || record.ProcessStartUtcTicks is <= 0)
            {
                throw new InvalidDataException("active Build journal is malformed");
            }
            return record;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("active Build journal is malformed", exception);
        }
    }
}
