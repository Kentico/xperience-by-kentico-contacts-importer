namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Runs the analysis (dry run) and the import of a job in bounded steps driven by the browser (PLAN D11),
/// and the operations around them.
/// </summary>
internal interface IImportProcessor
{
    /// <summary>
    /// Does the next piece of work of a phase: processes batches of uploaded rows in file order until the time budget
    /// (<see cref="ContactsImporterOptions.ProcessingTimeBudgetSeconds"/>) is used, storing a checkpoint after every batch,
    /// then returns the progress. The first call of a phase starts it (<c>Uploaded → Analyzing</c>,
    /// <c>Confirmed → Running</c>), the call that processes the last row finishes it (<c>Analyzed</c>, <c>Completed</c>).
    /// Call again while the outcome is <see cref="ImportProcessOutcome.Processed"/>. Problems of the job or its data are
    /// reported through <see cref="ImportProgress.Outcome"/>, not thrown.
    /// </summary>
    /// <param name="jobId">Job to process.</param>
    /// <param name="phase">Analysis (same code path, nothing written) or import.</param>
    /// <param name="driverToken">Token from the previous call's progress; <c>null</c> on the first call of a browser tab.</param>
    /// <param name="takeOver">The user chose to continue a job another tab or user left (stale heartbeat).</param>
    /// <param name="cancellationToken">Stops after the current batch; the job stays resumable.</param>
    /// <param name="userId">User whose request this is, for the event log and <see cref="AfterContactImportEvent"/>; <c>null</c> = not known.</param>
    /// <remarks>
    /// The call that finishes the analysis, completes the import or fails the job raises
    /// <see cref="AfterContactImportEvent"/> before it returns: after the outcome is stored and after the job lock
    /// is released, waiting for the handlers.
    /// </remarks>
    public ImportProgress ProcessNext(Guid jobId, ImportPhase phase, string? driverToken, bool takeOver, CancellationToken cancellationToken = default, int? userId = null);

    /// <summary>
    /// Current progress of a job without doing any work; <c>null</c> when the job does not exist.
    /// </summary>
    public ImportProgress? GetProgress(Guid jobId);

    /// <summary>
    /// Confirms an analyzed job for import (<c>Analyzed → Confirmed</c>). Refused when the analysis is older than
    /// <see cref="ContactsImporterOptions.AnalysisValidHours"/>: the job then returns to Uploaded and must be analyzed again.
    /// </summary>
    /// <remarks>
    /// A job that adds contacts to a recipient list or records consent agreements
    /// (<see cref="ImportAttestationPolicy.IsRequired"/>) is confirmed only with an attestation by a known user for the
    /// current consent statement (<see cref="ImportAttestationPolicy.StatementVersion"/>); otherwise the result is
    /// <see cref="ImportConfirmOutcome.AttestationRequired"/> and nothing changes. The caller checks that the user
    /// may attest. The attestation is written to the event log.
    /// </remarks>
    /// <param name="jobId">Job to confirm.</param>
    /// <param name="attestation">Who confirmed and which consent statement they saw; stored in the job configuration.</param>
    public ImportConfirmResult Confirm(Guid jobId, ImportAttestation? attestation = null);

    /// <summary>
    /// Cancels a job that is not finished. A processing call in progress, also one on another application instance,
    /// stops at its next status check (before its next database write or with its checkpoint); contacts written so
    /// far stay. Uploaded data is deleted. Raises <see cref="AfterContactImportEvent"/>.
    /// </summary>
    /// <param name="jobId">Job to cancel.</param>
    /// <param name="userId">User who cancels, for the event log and the event; <c>null</c> = not known.</param>
    public ImportCancelResult Cancel(Guid jobId, int? userId = null);
}

internal enum ImportProcessOutcome
{
    /// <summary>Work was done and more is left: call again with the returned driver token.</summary>
    Processed,

    /// <summary>The phase is finished: the job is Analyzed (analysis) or Completed (import).</summary>
    PhaseFinished,

    JobNotFound,

    /// <summary>The job is in a state the phase cannot start or continue from.</summary>
    InvalidState,

    /// <summary>Another browser tab or user is processing the job (recent heartbeat).</summary>
    ActiveElsewhere,

    /// <summary>The previous driver is gone; call again with <c>takeOver</c> after the user chose to continue.</summary>
    StaleRequiresTakeOver,

    /// <summary>The analysis is too old to import from; the job returned to Uploaded and must be analyzed again.</summary>
    AnalysisExpired,

    /// <summary>
    /// The job was cancelled, taken over by another driver, or continued by a newer call with the same driver token
    /// (a repeated request) during the call.
    /// </summary>
    Interrupted,

    /// <summary>A temporary problem (database timeout, deadlock, storage); nothing is lost, call again after a pause.</summary>
    TransientError,

    /// <summary>
    /// The phase cannot go on; see <see cref="ImportProgress.Error"/>. Usually the job is finished
    /// (<see cref="ImportProgress.State"/> is Failed, uploaded data deleted). When the job configuration does not match
    /// the contact class and the import has not started writing contacts (start of a phase, or during the analysis),
    /// the job instead returns to Uploaded and keeps its uploaded data: the options or the mapping can be fixed
    /// without uploading the file again.
    /// </summary>
    Failed,
}

/// <summary>
/// Result of a processing call and the progress of the job. Counts only, no contact data.
/// </summary>
internal sealed record ImportProgress
{
    public required ImportProcessOutcome Outcome { get; init; }

    public required ImportPhase Phase { get; init; }

    /// <summary>Job state after the call; <see cref="ImportJobState.Paused"/> when derived from a stale heartbeat.</summary>
    public ImportJobState State { get; init; }

    /// <summary>Token to pass to the next call; <c>null</c> when the caller does not drive the job.</summary>
    public string? DriverToken { get; init; }

    /// <summary>Rows processed in the current phase.</summary>
    public long RowsProcessed { get; init; }

    /// <summary>Rows to process (valid uploaded rows).</summary>
    public long RowsTotal { get; init; }

    /// <summary>Running counts of the current phase.</summary>
    public ImportAggregates Aggregates { get; init; } = new();

    /// <summary>Counts of the finished analysis, when there is one (expected next to actual).</summary>
    public ImportAggregates? Analysis { get; init; }

    /// <summary>When the analysis finished.</summary>
    public DateTime? AnalyzedUtc { get; init; }

    /// <summary>Problem description without contact data, for a failed job or a temporary error.</summary>
    public ImportMessage? Error { get; init; }
}

internal enum ImportConfirmOutcome
{
    Confirmed,
    JobNotFound,

    /// <summary>The job is not Analyzed.</summary>
    InvalidState,

    /// <summary>The analysis is too old; the job returned to Uploaded.</summary>
    AnalysisExpired,

    /// <summary>
    /// The job changes recipient list subscriptions or consent agreements and was not confirmed with a valid
    /// attestation of the current consent statement. Nothing changed.
    /// </summary>
    AttestationRequired,
}

/// <param name="Outcome">Result of the confirmation.</param>
/// <param name="Status">Stored status after the call; <c>null</c> when the job does not exist.</param>
internal sealed record ImportConfirmResult(ImportConfirmOutcome Outcome, ImportJobStatus? Status = null);

internal enum ImportCancelOutcome
{
    Cancelled,
    JobNotFound,

    /// <summary>The job is already Completed, Failed or Cancelled.</summary>
    AlreadyFinished,
}

/// <param name="Outcome">Result of the cancellation.</param>
/// <param name="Status">Stored status after the call; <c>null</c> when the job does not exist.</param>
internal sealed record ImportCancelResult(ImportCancelOutcome Outcome, ImportJobStatus? Status = null);
