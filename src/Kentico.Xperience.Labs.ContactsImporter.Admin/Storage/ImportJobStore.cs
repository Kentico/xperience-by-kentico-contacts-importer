using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

internal sealed class ImportJobStore(IImportFileSystem fileSystem, IOptions<ContactsImporterOptions> options) : IImportJobStore
{
    internal const string JobFileName = "job.json";
    internal const string StatusFileName = "status.json";

    /// <summary>
    /// Copy of the status of a finished job (Completed, Failed, Cancelled). Status writes of a job that is not finished
    /// never touch it and reads prefer it, so a finished job stays finished whatever is written to <c>status.json</c> later.
    /// </summary>
    internal const string FinalStatusFileName = "status.final.json";
    internal const string ChunksDirectoryName = "chunks";
    internal const string ChunkDataExtension = ".ndjson.gz";
    internal const string ChunkInfoExtension = ".json";
    internal const string ReportFilePrefix = "report-";

    /// <summary>
    /// Parts of the value examples of the analysis, in the chunks folder: personal data like the chunks, deleted with them.
    /// </summary>
    internal const string SamplePartPrefix = "samples-";
    internal const string SamplePartExtension = ".json";

    private const string TempExtension = ".tmp";
    private const int ReadAttempts = 3;
    private const int WriteAttempts = 5;
    private const int UpdateAttempts = 5;
    private static readonly TimeSpan retryDelay = TimeSpan.FromMilliseconds(40);

    private readonly ConcurrentDictionary<Guid, Lock> statusLocks = new();
    private readonly Lazy<string> root = new(() => fileSystem.ResolveRoot(options.Value.StoragePath));

    public ImportJobStatus CreateJob(ImportJob job)
    {
        string jobDirectory = GetJobDirectory(job.JobId);
        if (fileSystem.DirectoryExists(jobDirectory))
        {
            throw new InvalidOperationException($"Import job '{job.JobId}' already exists.");
        }

        fileSystem.CreateDirectory(jobDirectory);
        var status = new ImportJobStatus { State = ImportJobState.Configuring, UpdatedUtc = job.CreatedUtc };
        WriteAtomic(jobDirectory, JobFileName, ImportJson.Serialize(job));
        WriteAtomic(jobDirectory, StatusFileName, ImportJson.Serialize(status));

        return status;
    }

    public bool JobExists(Guid jobId) => fileSystem.DirectoryExists(GetJobDirectory(jobId));

    public ImportJob? ReadJob(Guid jobId) => ReadJson<ImportJob>(GetJobDirectory(jobId), JobFileName, retryWhenMissing: true);

    public void WriteJob(ImportJob job) => WriteAtomic(GetExistingJobDirectory(job.JobId), JobFileName, ImportJson.Serialize(job));

    public ImportJobStatus? ReadStatus(Guid jobId) => ReadStatusSnapshot(GetJobDirectory(jobId))?.Status;

    public void WriteStatus(Guid jobId, ImportJobStatus status)
    {
        ThrowIfPaused(status);

        string jobDirectory = GetExistingJobDirectory(jobId);
        byte[] content = ImportJson.Serialize(status);
        lock (statusLocks.GetOrAdd(jobId, _ => new Lock()))
        {
            bool terminal = ImportJobStateMachine.IsTerminal(status.State);
            WriteStatusFiles(jobDirectory, content, terminal, leavesFinished: !terminal);
        }
    }

    public ImportJobStatus? UpdateStatus(Guid jobId, Func<ImportJobStatus, ImportJobStatus> update)
    {
        string jobDirectory = GetJobDirectory(jobId);
        if (!fileSystem.DirectoryExists(jobDirectory))
        {
            return null;
        }

        lock (statusLocks.GetOrAdd(jobId, _ => new Lock()))
        {
            int attempt = 0;
            while (true)
            {
                var current = ReadStatusSnapshot(jobDirectory);
                if (current is null)
                {
                    return null;
                }

                var updated = update(current.Status);
                ThrowIfPaused(updated);
                if (updated == current.Status)
                {
                    if (current.FromFinalFile)
                    {
                        // A write computed from an older status replaced the status of a finished job: put it back.
                        WriteAtomic(jobDirectory, StatusFileName, ImportJson.Serialize(current.Status));
                    }

                    return current.Status;
                }

                updated = updated with { Version = current.Status.Version + 1 };
                byte[] content = ImportJson.Serialize(updated);
                bool terminal = ImportJobStateMachine.IsTerminal(updated.State);
                bool lastAttempt = ++attempt >= UpdateAttempts;

                // No compare-and-swap in the storage API, so the closest substitute: look again right before the write
                // and right after it. A change by another application instance in between means the decision above
                // was made on old data, so it is made again on the new content.
                if (!lastAttempt && !IsStatusFileContent(jobDirectory, current.Content))
                {
                    continue;
                }

                WriteStatusFiles(jobDirectory, content, terminal, leavesFinished: !terminal && ImportJobStateMachine.IsTerminal(current.Status.State));

                bool stored = IsStatusFileContent(jobDirectory, content);
                if (terminal)
                {
                    if (!stored)
                    {
                        // The final status file already makes the job finished for every reader; this only repairs
                        // status.json after another instance replaced it.
                        WriteAtomic(jobDirectory, StatusFileName, content);
                    }

                    return updated;
                }

                if (lastAttempt || (stored && !HasFinalStatus(jobDirectory)))
                {
                    return updated;
                }

                // Something else is stored now. A higher version was written on top of this change and includes it.
                // The same or a lower version replaced it (or the job was finished from an older status): decide again.
                var after = ReadStatusSnapshot(jobDirectory);
                if (after is null)
                {
                    return null;
                }

                if (after.Status.Version > updated.Version)
                {
                    return updated;
                }
            }
        }
    }

    public void WriteChunk(Guid jobId, ImportChunkInfo chunk, ReadOnlySpan<byte> gzippedContent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunk.Index);

        string chunksDirectory = GetChunksDirectory(GetExistingJobDirectory(jobId));
        fileSystem.CreateDirectory(chunksDirectory);

        // The metadata file marks a complete chunk: remove it first, write it last.
        fileSystem.DeleteFile(fileSystem.Combine(chunksDirectory, ChunkInfoFileName(chunk.Index)));
        WriteAtomic(chunksDirectory, ChunkDataFileName(chunk.Index), gzippedContent);
        WriteAtomic(chunksDirectory, ChunkInfoFileName(chunk.Index), ImportJson.Serialize(chunk));
    }

    public ImportChunkInfo? ReadChunkInfo(Guid jobId, int index) =>
        index < 0 ? null : ReadJson<ImportChunkInfo>(GetChunksDirectory(GetJobDirectory(jobId)), ChunkInfoFileName(index));

    public Stream? OpenChunk(Guid jobId, int index)
    {
        if (index < 0)
        {
            return null;
        }

        string path = fileSystem.Combine(GetChunksDirectory(GetJobDirectory(jobId)), ChunkDataFileName(index));

        return fileSystem.FileExists(path) ? fileSystem.OpenRead(path) : null;
    }

    public IReadOnlyList<ImportChunkInfo> ListChunks(Guid jobId)
    {
        string chunksDirectory = GetChunksDirectory(GetJobDirectory(jobId));
        var chunks = new List<ImportChunkInfo>();
        foreach (string fileName in fileSystem.GetFileNames(chunksDirectory))
        {
            if (TryParseChunkInfoFileName(fileName, out int index)
                && ReadJson<ImportChunkInfo>(chunksDirectory, fileName) is { } chunk
                && chunk.Index == index)
            {
                chunks.Add(chunk);
            }
        }

        chunks.Sort((x, y) => x.Index.CompareTo(y.Index));

        return chunks;
    }

    public long GetChunksSize(Guid jobId, int? exceptIndex = null)
    {
        string? except = exceptIndex is { } index ? ChunkDataFileName(index) : null;
        long size = 0;
        foreach (var (name, length) in fileSystem.GetFileSizes(GetChunksDirectory(GetJobDirectory(jobId))))
        {
            if (name.EndsWith(ChunkDataExtension, StringComparison.Ordinal) && !string.Equals(name, except, StringComparison.Ordinal))
            {
                size += length;
            }
        }

        return size;
    }

    public bool HasChunks(Guid jobId) => fileSystem.GetFileNames(GetChunksDirectory(GetJobDirectory(jobId))).Count > 0;

    public void DeleteChunk(Guid jobId, int index)
    {
        if (index < 0)
        {
            return;
        }

        string chunksDirectory = GetChunksDirectory(GetJobDirectory(jobId));
        fileSystem.DeleteFile(fileSystem.Combine(chunksDirectory, ChunkInfoFileName(index)));
        fileSystem.DeleteFile(fileSystem.Combine(chunksDirectory, ChunkDataFileName(index)));
    }

    public void DeleteChunks(Guid jobId) => fileSystem.DeleteDirectory(GetChunksDirectory(GetJobDirectory(jobId)));

    public IReadOnlyList<long> ListSampleParts(Guid jobId)
    {
        var parts = new List<long>();
        foreach (string fileName in fileSystem.GetFileNames(GetChunksDirectory(GetJobDirectory(jobId))))
        {
            if (TryParseSamplePartFileName(fileName, out long firstRow))
            {
                parts.Add(firstRow);
            }
        }

        parts.Sort();

        return parts;
    }

    public void WriteSamplePart(Guid jobId, long firstRow, IReadOnlyList<ImportValueSample> samples)
    {
        string chunksDirectory = GetChunksDirectory(GetJobDirectory(jobId));
        if (fileSystem.DirectoryExists(chunksDirectory))
        {
            WriteAtomic(chunksDirectory, SamplePartFileName(firstRow), ImportJson.Serialize(samples));
        }
    }

    public IReadOnlyList<ImportValueSample>? ReadSamplePart(Guid jobId, long firstRow) =>
        ReadJson<List<ImportValueSample>>(GetChunksDirectory(GetJobDirectory(jobId)), SamplePartFileName(firstRow));

    public void DeleteSamplePart(Guid jobId, long firstRow) =>
        fileSystem.DeleteFile(fileSystem.Combine(GetChunksDirectory(GetJobDirectory(jobId)), SamplePartFileName(firstRow)));

    public IReadOnlyList<string> ListReports(Guid jobId) =>
        [.. fileSystem.GetFileNames(GetJobDirectory(jobId)).Where(IsReportFileName).Order(StringComparer.Ordinal)];

    public void WriteReport(Guid jobId, string fileName, ReadOnlySpan<byte> content) =>
        WriteAtomic(GetExistingJobDirectory(jobId), ValidReportFileName(fileName), content);

    public Stream? OpenReport(Guid jobId, string fileName)
    {
        string path = fileSystem.Combine(GetJobDirectory(jobId), ValidReportFileName(fileName));

        return fileSystem.FileExists(path) ? fileSystem.OpenRead(path) : null;
    }

    public void DeleteReport(Guid jobId, string fileName) =>
        fileSystem.DeleteFile(fileSystem.Combine(GetJobDirectory(jobId), ValidReportFileName(fileName)));

    public void DeleteReports(Guid jobId)
    {
        string jobDirectory = GetJobDirectory(jobId);
        foreach (string fileName in fileSystem.GetFileNames(jobDirectory).Where(IsReportFileName))
        {
            fileSystem.DeleteFile(fileSystem.Combine(jobDirectory, fileName));
        }
    }

    public void DeleteJob(Guid jobId)
    {
        fileSystem.DeleteDirectory(GetJobDirectory(jobId));
        statusLocks.TryRemove(jobId, out _);
    }

    public IReadOnlyList<Guid> ListJobs()
    {
        var jobIds = new List<Guid>();
        foreach (string name in fileSystem.GetDirectoryNames(root.Value))
        {
            if (ImportJobId.TryParse(name, out var jobId))
            {
                jobIds.Add(jobId);
            }
        }

        return jobIds;
    }

    public DateTime? GetJobFolderLastWriteTimeUtc(Guid jobId) => fileSystem.GetDirectoryLastWriteTimeUtc(GetJobDirectory(jobId));

    private static void ThrowIfPaused(ImportJobStatus status)
    {
        if (status.State == ImportJobState.Paused)
        {
            throw new ArgumentException("The Paused state is derived on read and cannot be stored.", nameof(status));
        }
    }

    private static string ChunkDataFileName(int index) => index.ToString(CultureInfo.InvariantCulture) + ChunkDataExtension;

    private static string ChunkInfoFileName(int index) => index.ToString(CultureInfo.InvariantCulture) + ChunkInfoExtension;

    private static bool TryParseChunkInfoFileName(string fileName, out int index)
    {
        index = -1;

        return fileName.EndsWith(ChunkInfoExtension, StringComparison.Ordinal)
            && int.TryParse(fileName.AsSpan(0, fileName.Length - ChunkInfoExtension.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static string SamplePartFileName(long firstRow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstRow);

        return SamplePartPrefix + firstRow.ToString("D12", CultureInfo.InvariantCulture) + SamplePartExtension;
    }

    private static bool TryParseSamplePartFileName(string fileName, out long firstRow)
    {
        firstRow = 0;

        return fileName.StartsWith(SamplePartPrefix, StringComparison.Ordinal)
            && fileName.EndsWith(SamplePartExtension, StringComparison.Ordinal)
            && long.TryParse(fileName.AsSpan(SamplePartPrefix.Length, fileName.Length - SamplePartPrefix.Length - SamplePartExtension.Length), NumberStyles.None, CultureInfo.InvariantCulture, out firstRow);
    }

    private static bool IsReportFileName(string fileName) =>
        fileName.StartsWith(ReportFilePrefix, StringComparison.Ordinal) && !fileName.EndsWith(TempExtension, StringComparison.Ordinal);

    /// <summary>
    /// Report names are built by the importer, never by a client; the check keeps a wrong name from leaving the job folder.
    /// </summary>
    private static string ValidReportFileName(string fileName) =>
        IsReportFileName(fileName) && fileName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !fileName.Contains("..", StringComparison.Ordinal)
            ? fileName
            : throw new ArgumentException("The report file name is not valid.", nameof(fileName));

    private string GetJobDirectory(Guid jobId) => fileSystem.Combine(root.Value, ImportJobId.Format(jobId));

    private string GetExistingJobDirectory(Guid jobId)
    {
        string jobDirectory = GetJobDirectory(jobId);

        return fileSystem.DirectoryExists(jobDirectory)
            ? jobDirectory
            : throw new InvalidOperationException($"Import job '{jobId}' does not exist.");
    }

    /// <summary>
    /// The status to work with: <c>status.json</c>, or the final status when the job is finished but <c>status.json</c>
    /// says otherwise (it was replaced by a write computed from an older status).
    /// </summary>
    private StatusSnapshot? ReadStatusSnapshot(string jobDirectory)
    {
        var status = ReadJson<ImportJobStatus>(jobDirectory, StatusFileName, out byte[]? content, retryWhenMissing: true);
        if (status is null || content is null)
        {
            return null;
        }

        if (!ImportJobStateMachine.IsTerminal(status.State)
            && ReadJson<ImportJobStatus>(jobDirectory, FinalStatusFileName) is { } final
            && ImportJobStateMachine.IsTerminal(final.State))
        {
            return new StatusSnapshot(final, content, FromFinalFile: true);
        }

        return new StatusSnapshot(status, content, FromFinalFile: false);
    }

    private bool HasFinalStatus(string jobDirectory) => fileSystem.FileExists(fileSystem.Combine(jobDirectory, FinalStatusFileName));

    /// <summary>
    /// Whether <c>status.json</c> holds exactly <paramref name="expected"/> right now.
    /// </summary>
    private bool IsStatusFileContent(string jobDirectory, byte[] expected)
    {
        string path = fileSystem.Combine(jobDirectory, StatusFileName);
        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                if (fileSystem.FileExists(path))
                {
                    return fileSystem.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file is being replaced; look again.
            }

            if (attempt < ReadAttempts)
            {
                Thread.Sleep(retryDelay);
            }
        }

        return false;
    }

    /// <summary>
    /// Writes <c>status.json</c>. The status of a finished job goes to the final status file first, so there is no
    /// moment in which only the replaceable file says the job is finished.
    /// </summary>
    private void WriteStatusFiles(string jobDirectory, byte[] content, bool terminal, bool leavesFinished)
    {
        if (terminal)
        {
            WriteAtomic(jobDirectory, FinalStatusFileName, content);
        }
        else if (leavesFinished)
        {
            fileSystem.DeleteFile(fileSystem.Combine(jobDirectory, FinalStatusFileName));
        }

        WriteAtomic(jobDirectory, StatusFileName, content);
    }

    private string GetChunksDirectory(string jobDirectory) => fileSystem.Combine(jobDirectory, ChunksDirectoryName);

    private void WriteAtomic(string directory, string fileName, ReadOnlySpan<byte> content)
    {
        string path = fileSystem.Combine(directory, fileName);
        string tempPath = fileSystem.Combine(directory, $"{fileName}.{Guid.NewGuid():N}{TempExtension}");
        try
        {
            fileSystem.WriteAllBytes(tempPath, content);
            ReplaceWithRetry(tempPath, path);
        }
        catch
        {
            fileSystem.DeleteFile(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Another process (a reader, antivirus, a storage sync) can hold the target for a moment, so the replace is retried.
    /// </summary>
    private void ReplaceWithRetry(string tempPath, string path)
    {
        for (int attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            try
            {
                fileSystem.ReplaceFile(tempPath, path);
                return;
            }
            catch (Exception ex) when (attempt < WriteAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(retryDelay);
            }
        }
    }

    /// <summary>
    /// Reads a JSON file. A file that is being replaced can be incomplete or (job and status files) missing for a moment,
    /// so the read is retried.
    /// </summary>
    private T? ReadJson<T>(string directory, string fileName, bool retryWhenMissing = false) where T : class =>
        ReadJson<T>(directory, fileName, out _, retryWhenMissing);

    private T? ReadJson<T>(string directory, string fileName, out byte[]? content, bool retryWhenMissing = false) where T : class
    {
        content = null;
        string path = fileSystem.Combine(directory, fileName);
        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            bool lastAttempt = attempt == ReadAttempts;
            try
            {
                if (fileSystem.FileExists(path))
                {
                    byte[] bytes = fileSystem.ReadAllBytes(path);
                    var value = ImportJson.Deserialize<T>(bytes);
                    content = bytes;

                    return value;
                }

                if (lastAttempt || !retryWhenMissing || !fileSystem.DirectoryExists(directory))
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                if (lastAttempt)
                {
                    throw new InvalidDataException($"File '{fileName}' of an import job cannot be read.", ex);
                }
            }

            Thread.Sleep(retryDelay);
        }

        return null;
    }
}

/// <param name="Status">Status to work with.</param>
/// <param name="Content">Bytes of <c>status.json</c> the status was read with.</param>
/// <param name="FromFinalFile"><paramref name="Status"/> comes from the final status file, not from <c>status.json</c>.</param>
internal sealed record StatusSnapshot(ImportJobStatus Status, byte[] Content, bool FromFinalFile);
