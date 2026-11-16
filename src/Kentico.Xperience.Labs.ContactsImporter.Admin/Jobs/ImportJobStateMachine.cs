namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Job lifecycle rules (PLAN §4): allowed transitions, resets after configuration changes,
/// the derived Paused state, and the single-driver rule. Pure functions over <see cref="ImportJobStatus"/>;
/// callers persist the returned status.
/// </summary>
internal static class ImportJobStateMachine
{
    /// <summary>
    /// Upper limit of the heartbeat age of a job with slow batches, as a multiple of the configured age.
    /// </summary>
    internal const int MaxHeartbeatStaleFactor = 10;

    private static readonly Dictionary<ImportJobState, ImportJobState[]> transitions = new()
    {
        [ImportJobState.Configuring] = [ImportJobState.Uploading],
        [ImportJobState.Uploading] = [ImportJobState.Uploaded, ImportJobState.Configuring],
        [ImportJobState.Uploaded] = [ImportJobState.Analyzing, ImportJobState.Configuring],
        [ImportJobState.Analyzing] = [ImportJobState.Analyzed, ImportJobState.Uploaded, ImportJobState.Configuring],
        [ImportJobState.Analyzed] = [ImportJobState.Confirmed, ImportJobState.Uploaded, ImportJobState.Configuring],
        [ImportJobState.Confirmed] = [ImportJobState.Running, ImportJobState.Uploaded, ImportJobState.Configuring],
        [ImportJobState.Running] = [ImportJobState.Completed],
    };

    /// <summary>
    /// Completed, Failed and Cancelled: nothing follows.
    /// </summary>
    public static bool IsTerminal(ImportJobState state) =>
        state is ImportJobState.Completed or ImportJobState.Failed or ImportJobState.Cancelled;

    /// <summary>
    /// Whether a job in the stored state <paramref name="from"/> may move to <paramref name="to"/>.
    /// Every state that is not terminal can also move to Failed or Cancelled. Paused is never a stored state.
    /// </summary>
    public static bool CanTransition(ImportJobState from, ImportJobState to)
    {
        if (from == ImportJobState.Paused || to == ImportJobState.Paused || IsTerminal(from))
        {
            return false;
        }

        return to is ImportJobState.Failed or ImportJobState.Cancelled
            || (transitions.TryGetValue(from, out var targets) && targets.Contains(to));
    }

    /// <summary>
    /// Moves the job to <paramref name="to"/>. Throws <see cref="InvalidOperationException"/> when the transition is not allowed.
    /// </summary>
    /// <param name="status">Current status.</param>
    /// <param name="to">Target state.</param>
    /// <param name="utcNow">Current time.</param>
    /// <param name="error">Failure description, stored when the target is Failed.</param>
    public static ImportJobStatus Transition(ImportJobStatus status, ImportJobState to, DateTime utcNow, string? error = null) =>
        TryTransition(status, to, utcNow, out var updated, error)
            ? updated
            : throw new InvalidOperationException($"Import job cannot move from {status.State} to {to}.");

    public static bool TryTransition(ImportJobStatus status, ImportJobState to, DateTime utcNow, out ImportJobStatus updated, string? error = null)
    {
        if (!CanTransition(status.State, to))
        {
            updated = status;
            return false;
        }

        updated = status with { State = to, UpdatedUtc = utcNow };
        updated = to switch
        {
            ImportJobState.Configuring => ResetUpload(updated),
            ImportJobState.Uploaded => ResetProcessing(updated),
            ImportJobState.Analyzing => StartPhase(updated, ImportPhase.Analyze),
            ImportJobState.Running => StartPhase(updated, ImportPhase.Execute),
            ImportJobState.Completed or ImportJobState.Cancelled => Finish(updated, utcNow, null),
            ImportJobState.Failed => Finish(updated, utcNow, error ?? "Import failed."),
            ImportJobState.Uploading or ImportJobState.Analyzed or ImportJobState.Confirmed or ImportJobState.Paused => updated,
            _ => updated,
        };

        return true;
    }

    /// <summary>
    /// State to show: <see cref="ImportJobState.Paused"/> when the job is being analyzed or run and its heartbeat
    /// is stale (tab closed, computer asleep, network lost); otherwise the stored state.
    /// </summary>
    public static ImportJobState GetEffectiveState(ImportJobStatus status, DateTime utcNow, TimeSpan heartbeatStale) =>
        IsDriven(status.State) && IsHeartbeatStale(status, utcNow, heartbeatStale) ? ImportJobState.Paused : status.State;

    /// <summary>
    /// A heartbeat is stale when there is none or it is older than the heartbeat age of the job
    /// (<see cref="GetHeartbeatStale"/>: <paramref name="heartbeatStale"/>, longer for a job with slow batches).
    /// </summary>
    /// <param name="status">Current status.</param>
    /// <param name="utcNow">Current time.</param>
    /// <param name="heartbeatStale">Configured heartbeat age (<see cref="ContactsImporterOptions.HeartbeatStaleSeconds"/>).</param>
    public static bool IsHeartbeatStale(ImportJobStatus status, DateTime utcNow, TimeSpan heartbeatStale) =>
        status.HeartbeatUtc is not { } heartbeat || utcNow - heartbeat > GetHeartbeatStale(status, heartbeatStale);

    /// <summary>
    /// Heartbeat age after which the driver of the job counts as gone: the configured age, or twice the longest batch
    /// of the current phase when that is longer (at most <see cref="MaxHeartbeatStaleFactor"/> times the configured age).
    /// A driver sends a heartbeat with every batch, so a batch slower than the configured age would otherwise look like
    /// a closed browser tab and let another driver take over while the batch is still being written.
    /// </summary>
    public static TimeSpan GetHeartbeatStale(ImportJobStatus status, TimeSpan configured)
    {
        if (!(status.LongestBatchSeconds > 0) || configured <= TimeSpan.Zero)
        {
            return configured;
        }

        double seconds = Math.Min(status.LongestBatchSeconds * 2, configured.TotalSeconds * MaxHeartbeatStaleFactor);

        return seconds > configured.TotalSeconds ? TimeSpan.FromSeconds(seconds) : configured;
    }

    /// <summary>
    /// Compares two job configurations and says what the change requires.
    /// </summary>
    public static ImportConfigChange ClassifyConfigChange(ImportJob current, ImportJob updated)
    {
        bool uploadAffected = current.File != updated.File
            || !string.Equals(current.KeyField, updated.KeyField, StringComparison.Ordinal)
            || !string.Equals(current.DateFormat, updated.DateFormat, StringComparison.Ordinal)
            || !current.Mapping.SequenceEqual(updated.Mapping);
        if (uploadAffected)
        {
            return ImportConfigChange.Mapping;
        }

        return current.Options == updated.Options ? ImportConfigChange.None : ImportConfigChange.Options;
    }

    /// <summary>
    /// Applies the reset rules for a configuration change:
    /// a mapping change (file, key, mapping, date format) needs a new upload, so the job returns to Configuring and the
    /// caller must delete the uploaded chunks; an options change after analysis returns the job to Uploaded (analyze again).
    /// No configuration change is allowed once the job is running or finished.
    /// </summary>
    public static ImportConfigChangeResult ApplyConfigChange(ImportJobStatus status, ImportConfigChange change, DateTime utcNow)
    {
        if (change == ImportConfigChange.None)
        {
            return new ImportConfigChangeResult(true, status, false);
        }

        if (status.State is ImportJobState.Running or ImportJobState.Paused || IsTerminal(status.State))
        {
            return new ImportConfigChangeResult(false, status, false);
        }

        if (change == ImportConfigChange.Mapping)
        {
            return status.State == ImportJobState.Configuring
                ? new ImportConfigChangeResult(true, status with { UpdatedUtc = utcNow }, false)
                : new ImportConfigChangeResult(true, Transition(status, ImportJobState.Configuring, utcNow), true);
        }

        return status.State is ImportJobState.Analyzing or ImportJobState.Analyzed or ImportJobState.Confirmed
            ? new ImportConfigChangeResult(true, Transition(status, ImportJobState.Uploaded, utcNow), false)
            : new ImportConfigChangeResult(true, status with { UpdatedUtc = utcNow }, false);
    }

    /// <summary>
    /// Claims or keeps the right to drive the job (one driver per job) and records a heartbeat.
    /// </summary>
    /// <param name="status">Current status.</param>
    /// <param name="driverToken">Token the caller got from an earlier call, or <c>null</c> for a new driver.</param>
    /// <param name="takeOver">The user explicitly chose to continue a job another driver left (stale heartbeat).</param>
    /// <param name="instanceName">Name of the application instance handling the call.</param>
    /// <param name="utcNow">Current time.</param>
    /// <param name="heartbeatStale">Heartbeat age after which the current driver counts as gone.</param>
    public static ImportDriverResult AcquireDriver(ImportJobStatus status, string? driverToken, bool takeOver, string? instanceName, DateTime utcNow, TimeSpan heartbeatStale)
    {
        if (IsTerminal(status.State))
        {
            return new ImportDriverResult(ImportDriverOutcome.JobFinished, status, null);
        }

        bool isCurrentDriver = status.DriverToken is not null && string.Equals(status.DriverToken, driverToken, StringComparison.Ordinal);
        if (isCurrentDriver)
        {
            return Acquired(status, status.DriverToken!, instanceName, utcNow);
        }

        if (status.DriverToken is null)
        {
            return Acquired(status, NewDriverToken(), instanceName, utcNow);
        }

        if (!IsHeartbeatStale(status, utcNow, heartbeatStale))
        {
            return new ImportDriverResult(ImportDriverOutcome.ActiveElsewhere, status, null);
        }

        return takeOver
            ? Acquired(status, NewDriverToken(), instanceName, utcNow)
            : new ImportDriverResult(ImportDriverOutcome.StaleRequiresTakeOver, status, null);
    }

    private static bool IsDriven(ImportJobState state) => state is ImportJobState.Analyzing or ImportJobState.Running;

    private static ImportDriverResult Acquired(ImportJobStatus status, string token, string? instanceName, DateTime utcNow) =>
        AcquiredCall(status, token, NewDriverToken(), instanceName, utcNow);

    private static ImportDriverResult AcquiredCall(ImportJobStatus status, string token, string callId, string? instanceName, DateTime utcNow) =>
        new(
            ImportDriverOutcome.Acquired,
            status with { DriverToken = token, DriverCallId = callId, HeartbeatUtc = utcNow, LastInstance = instanceName, UpdatedUtc = utcNow },
            token,
            callId);

    private static string NewDriverToken() => Guid.NewGuid().ToString("N");

    private static ImportJobStatus ResetUpload(ImportJobStatus status) =>
        ResetProcessing(status) with { Chunks = [], Profile = new ImportProfile() };

    private static ImportJobStatus ResetProcessing(ImportJobStatus status) =>
        status with
        {
            Checkpoint = new ImportCheckpoint(),
            Aggregates = new Dictionary<string, long>(),
            Analysis = null,
            HeartbeatUtc = null,
            DriverToken = null,
            DriverCallId = null,
            LongestBatchSeconds = 0,
            Error = null,
        };

    private static ImportJobStatus StartPhase(ImportJobStatus status, ImportPhase phase) =>
        status with { Checkpoint = new ImportCheckpoint { Phase = phase }, Aggregates = new Dictionary<string, long>(), LongestBatchSeconds = 0, Error = null };

    private static ImportJobStatus Finish(ImportJobStatus status, DateTime utcNow, string? error) =>
        status with { FinishedUtc = utcNow, DriverToken = null, DriverCallId = null, Error = error };
}

/// <summary>
/// What a job configuration change touches.
/// </summary>
internal enum ImportConfigChange
{
    None,

    /// <summary>Only processing behaviors changed.</summary>
    Options,

    /// <summary>File, key field, column mapping or date format changed; uploaded data no longer matches.</summary>
    Mapping,
}

/// <param name="Allowed">False when the job is running or finished; the change must be refused.</param>
/// <param name="Status">Status to store when allowed.</param>
/// <param name="DeleteChunks">The caller must delete the uploaded chunks (a new upload is required).</param>
internal sealed record ImportConfigChangeResult(bool Allowed, ImportJobStatus Status, bool DeleteChunks);

internal enum ImportDriverOutcome
{
    /// <summary>The caller drives the job; use the returned token in later calls.</summary>
    Acquired,

    /// <summary>Another driver sent a heartbeat recently.</summary>
    ActiveElsewhere,

    /// <summary>The previous driver is gone; the user must explicitly choose to continue the import.</summary>
    StaleRequiresTakeOver,

    /// <summary>The job is Completed, Failed or Cancelled.</summary>
    JobFinished,
}

/// <param name="Outcome">Whether the caller may drive the job.</param>
/// <param name="Status">Status to store when <see cref="ImportDriverOutcome.Acquired"/>; unchanged otherwise.</param>
/// <param name="DriverToken">Token of the caller when acquired.</param>
/// <param name="CallId">ID of this processing call when acquired (<see cref="ImportJobStatus.DriverCallId"/>).</param>
internal sealed record ImportDriverResult(ImportDriverOutcome Outcome, ImportJobStatus Status, string? DriverToken, string? CallId = null);
