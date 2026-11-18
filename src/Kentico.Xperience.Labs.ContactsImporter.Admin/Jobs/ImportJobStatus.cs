using System.Text.Json.Serialization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Import job state, stored as <c>status.json</c>. Holds counts only, no contact data.
/// </summary>
internal sealed record ImportJobStatus
{
    /// <summary>
    /// Stored state. Never <see cref="ImportJobState.Paused"/>; that state is derived on read
    /// (<see cref="ImportJobStateMachine.GetEffectiveState"/>).
    /// </summary>
    public ImportJobState State { get; init; } = ImportJobState.Configuring;

    /// <summary>
    /// Uploaded chunks in file order. Filled when the upload completes; while uploading, the chunk files are the
    /// source of truth (<see cref="IImportJobStore.ListChunks"/>) because parallel uploads would overwrite each other here.
    /// </summary>
    public IReadOnlyList<ImportChunkInfo> Chunks { get; init; } = [];

    public ImportProfile Profile { get; init; } = new();

    public ImportCheckpoint Checkpoint { get; init; } = new();

    /// <summary>
    /// Running outcome counts of the analyze/execute phase.
    /// </summary>
    public IReadOnlyDictionary<string, long> Aggregates { get; init; } = new Dictionary<string, long>();

    /// <summary>
    /// Counts of the finished analysis; <c>null</c> until the job is Analyzed and after anything resets the analysis.
    /// </summary>
    public ImportAnalysisSummary? Analysis { get; init; }

    public DateTime? HeartbeatUtc { get; init; }

    public string? DriverToken { get; init; }

    public string? LastInstance { get; init; }

    public DateTime UpdatedUtc { get; init; }

    /// <summary>
    /// When the job reached Completed, Failed or Cancelled. Retention periods count from here.
    /// </summary>
    public DateTime? FinishedUtc { get; init; }

    /// <summary>
    /// Why the job failed or cannot start: the resource key of the message (<see cref="ImportTextKeys"/>); the UI pages
    /// show it in the user's admin UI language. Jobs of earlier versions hold English text here. Read and write it
    /// with <see cref="ErrorMessage"/> and <see cref="WithError"/>.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Arguments of the <see cref="Error"/> message; <c>null</c> when it has none.</summary>
    public IReadOnlyList<string>? ErrorArguments { get; init; }

    /// <summary><see cref="Error"/> with its arguments.</summary>
    [JsonIgnore]
    public ImportMessage? ErrorMessage => ImportMessage.FromStored(Error, ErrorArguments);

    /// <summary>
    /// Number of times the status was changed through <see cref="IImportJobStore.UpdateStatus"/>. Part of the check
    /// that keeps application instances from overwriting each other's changes (see <see cref="IImportJobStore.UpdateStatus"/>).
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Identifies the processing call that currently works on the job. Every call that claims the driver role gets a new
    /// one, so when a browser repeats a call (lost response, timeout) while the first one still runs, the older call
    /// stops at its next status check instead of both processing the same rows.
    /// </summary>
    public string? DriverCallId { get; init; }

    /// <summary>
    /// Longest time one batch of the current phase took, in seconds. Extends the heartbeat age after which the driver
    /// counts as gone (<see cref="ImportJobStateMachine.GetHeartbeatStale"/>), so a slow database does not let another
    /// driver take over in the middle of a batch.
    /// </summary>
    public double LongestBatchSeconds { get; init; }

    /// <summary>
    /// When the current phase (analysis or import) started; <c>null</c> before it starts and for jobs of earlier versions.
    /// Shown to users who want to run another import while this one runs.
    /// </summary>
    public DateTime? PhaseStartedUtc { get; init; }

    /// <summary>User whose request started the current phase; <c>null</c> when not known.</summary>
    public int? PhaseStartedByUserId { get; init; }

    /// <summary>
    /// The user paused the analysis or the Run with Pause (<see cref="ImportJobStateMachine.TryPauseByDriver"/>): the
    /// import stays paused when it is opened again, until someone continues it. Cleared by the next processing call that
    /// gets the driver role (also a take over) and by every state change (finished, cancelled, reset to Uploaded).
    /// An import paused any other way (tab closed, computer asleep, network lost) does not have it and continues on open.
    /// </summary>
    public bool PausedByUser { get; init; }

    /// <summary>The status with <paramref name="error"/> as <see cref="Error"/> and <see cref="ErrorArguments"/>.</summary>
    public ImportJobStatus WithError(ImportMessage? error) =>
        this with { Error = error?.Key, ErrorArguments = error is { Arguments.Count: > 0 } ? error.Arguments : null };
}

/// <summary>
/// Job lifecycle states (PLAN §4). Serialized by name (<c>"Uploading"</c>).
/// </summary>
[JsonConverter(typeof(ExactNameEnumConverter<ImportJobState>))]
internal enum ImportJobState
{
    Configuring,
    Uploading,
    Uploaded,
    Analyzing,
    Analyzed,
    Confirmed,
    Running,

    /// <summary>Derived on read from a stale heartbeat; never stored.</summary>
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One uploaded chunk. Also stored next to the chunk as <c>chunks/{index}.json</c>.
/// </summary>
internal sealed record ImportChunkInfo
{
    public required int Index { get; init; }

    /// <summary>Lowercase hex SHA-256 of the gzipped bytes.</summary>
    public required string Sha256 { get; init; }

    public required int Rows { get; init; }

    /// <summary>Row number of the first record in the chunk.</summary>
    public long FirstRow { get; init; }

    /// <summary>Row number of the last record in the chunk.</summary>
    public long LastRow { get; init; }
}

/// <summary>
/// Full-file statistics computed in the browser.
/// </summary>
internal sealed record ImportProfile
{
    public long RowsRead { get; init; }

    public long RowsValid { get; init; }

    public long RowsRejected { get; init; }

    public long LikelyDuplicates { get; init; }

    public int ChunkCount { get; init; }
}

/// <summary>
/// Position the next processing call continues from.
/// </summary>
internal sealed record ImportCheckpoint
{
    public ImportPhase Phase { get; init; } = ImportPhase.Analyze;

    public int ChunkIndex { get; init; }

    /// <summary>Number of records of chunk <see cref="ChunkIndex"/> that are already processed.</summary>
    public long RowOffset { get; init; }

    /// <summary>Source file row number of the last processed record; 0 when nothing is processed yet.</summary>
    public long LastRow { get; init; }

    /// <summary>
    /// Rows of the next import batch, adapted to the database (<see cref="ContactsImporterOptions.AdaptiveBatchSize"/>).
    /// <c>null</c> = the configured <see cref="ContactsImporterOptions.BatchSize"/>: the analysis, the first batch of
    /// an import, an import back at the configured size, and jobs of earlier versions. Kept here so the next call, on any instance, continues with it.
    /// </summary>
    public int? BatchSize { get; init; }
}

/// <summary>
/// Result of the last finished analysis (dry run). Kept while the import runs, so the results can show
/// expected next to actual counts.
/// </summary>
internal sealed record ImportAnalysisSummary
{
    public required DateTime FinishedUtc { get; init; }

    public IReadOnlyDictionary<string, long> Aggregates { get; init; } = new Dictionary<string, long>();
}

internal enum ImportPhase
{
    Analyze,
    Execute,
}
