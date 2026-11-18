namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// File storage of import jobs under the configured storage path:
/// <c>{root}/{jobId}/job.json</c>, <c>status.json</c>, <c>chunks/{n}.ndjson.gz</c> (+ <c>chunks/{n}.json</c> metadata,
/// <c>chunks/samples-{first row}.json</c> value examples of the analysis)
/// and <c>report-*</c> files. Job IDs are GUIDs, so a job path can never leave the root.
/// </summary>
/// <remarks>
/// Files are written to a temporary file and then moved into place. Reads return <c>null</c> for missing files
/// and throw <see cref="InvalidDataException"/> for unreadable content.
/// </remarks>
internal interface IImportJobStore
{
    /// <summary>
    /// Creates the job folder with <c>job.json</c> and an initial <c>status.json</c> in the
    /// <see cref="ImportJobState.Configuring"/> state. Throws when the job already exists.
    /// </summary>
    public ImportJobStatus CreateJob(ImportJob job);

    public bool JobExists(Guid jobId);

    public ImportJob? ReadJob(Guid jobId);

    /// <summary>Replaces <c>job.json</c> of an existing job.</summary>
    public void WriteJob(ImportJob job);

    public ImportJobStatus? ReadStatus(Guid jobId);

    /// <summary>
    /// Replaces the status of an existing job unconditionally, also a finished one. For creating and repairing jobs;
    /// everything that changes a job that may be in use goes through <see cref="UpdateStatus"/>.
    /// </summary>
    public void WriteStatus(Guid jobId, ImportJobStatus status);

    /// <summary>
    /// Reads, changes and writes the status as one step and returns the stored status (<c>null</c> when the job has
    /// no status). Nothing is written when <paramref name="update"/> returns the status it got.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calls for the same job are serialized within one application instance. Other instances (web farm, shared
    /// storage) are handled optimistically, because the storage has no compare-and-swap: the file is read again right
    /// before the write and once more after it; when another instance changed it in between,
    /// <paramref name="update"/> runs again on the new content. So <paramref name="update"/> can run several times
    /// and must decide from the status it is given only (for example: is the caller still the driver?).
    /// </para>
    /// <para>
    /// Guaranteed: a finished job stays finished. Completed, Failed and Cancelled are also written to a second file
    /// that later status writes never touch and every read prefers, so a write that was computed from an older
    /// status cannot bring a cancelled job back, whenever it arrives.
    /// </para>
    /// <para>
    /// Not guaranteed: a change that does not finish the job (a checkpoint, a heartbeat, a takeover) can still be
    /// overwritten when another instance reads, writes and checks its own write entirely between this instance's last
    /// read and its write (milliseconds on a file share, longer on blob storage). The loser is not told. Processing
    /// stays correct because every batch is idempotent and every checkpoint write checks the driver again: at most one
    /// batch is processed twice, and the driver whose claim was overwritten stops at its next status check.
    /// </para>
    /// </remarks>
    public ImportJobStatus? UpdateStatus(Guid jobId, Func<ImportJobStatus, ImportJobStatus> update);

    /// <summary>
    /// Stores a chunk and its metadata, replacing a chunk with the same index.
    /// </summary>
    public void WriteChunk(Guid jobId, ImportChunkInfo chunk, ReadOnlySpan<byte> gzippedContent);

    /// <summary>Metadata of a stored chunk, or <c>null</c> when the chunk is not stored.</summary>
    public ImportChunkInfo? ReadChunkInfo(Guid jobId, int index);

    /// <summary>Opens the gzipped chunk content, or returns <c>null</c> when the chunk is not stored.</summary>
    public Stream? OpenChunk(Guid jobId, int index);

    /// <summary>Metadata of all stored chunks, ordered by index.</summary>
    public IReadOnlyList<ImportChunkInfo> ListChunks(Guid jobId);

    /// <summary>Total size in bytes of the stored chunk data (gzipped) of a job.</summary>
    /// <param name="jobId">Job.</param>
    /// <param name="exceptIndex">Chunk to leave out (the one being replaced); <c>null</c> = none.</param>
    public long GetChunksSize(Guid jobId, int? exceptIndex = null);

    /// <summary>Whether the job has any chunk files (complete or not).</summary>
    public bool HasChunks(Guid jobId);

    public void DeleteChunk(Guid jobId, int index);

    /// <summary>
    /// Deletes the uploaded chunks and everything else stored with them (the value examples of the analysis).
    /// </summary>
    public void DeleteChunks(Guid jobId);

    /// <summary>
    /// First rows of the stored parts of the value examples of the analysis (<see cref="ImportValueSample"/>), ordered.
    /// The examples hold personal data, so they are stored with the uploaded data (<c>chunks/samples-{first row}.json</c>)
    /// and deleted with it (<see cref="DeleteChunks"/>).
    /// </summary>
    public IReadOnlyList<long> ListSampleParts(Guid jobId);

    /// <summary>
    /// Stores a part of the value examples, replacing a part with the same first row. Does nothing when the uploaded data
    /// is already deleted (the job finished meanwhile), so no examples are left without it.
    /// </summary>
    public void WriteSamplePart(Guid jobId, long firstRow, IReadOnlyList<ImportValueSample> samples);

    /// <summary>A part of the value examples, or <c>null</c> when it does not exist.</summary>
    public IReadOnlyList<ImportValueSample>? ReadSamplePart(Guid jobId, long firstRow);

    /// <summary>Deletes a part of the value examples; does nothing when it does not exist.</summary>
    public void DeleteSamplePart(Guid jobId, long firstRow);

    /// <summary>File names of the job's reports (<c>report-*</c>).</summary>
    public IReadOnlyList<string> ListReports(Guid jobId);

    /// <summary>
    /// Stores a report file, replacing a file with the same name. The name must start with <c>report-</c>
    /// and consist of letters, digits, <c>-</c>, <c>_</c> and <c>.</c> only.
    /// </summary>
    public void WriteReport(Guid jobId, string fileName, ReadOnlySpan<byte> content);

    /// <summary>Opens a report file, or returns <c>null</c> when it does not exist.</summary>
    public Stream? OpenReport(Guid jobId, string fileName);

    /// <summary>Deletes one report file; does nothing when it does not exist.</summary>
    public void DeleteReport(Guid jobId, string fileName);

    public void DeleteReports(Guid jobId);

    /// <summary>Deletes the job folder with everything in it.</summary>
    public void DeleteJob(Guid jobId);

    /// <summary>IDs of all job folders under the root.</summary>
    public IReadOnlyList<Guid> ListJobs();

    /// <summary>Last write time of the job folder, when the storage tracks it.</summary>
    public DateTime? GetJobFolderLastWriteTimeUtc(Guid jobId);
}
