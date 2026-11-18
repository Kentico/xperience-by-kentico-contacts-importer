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
        [ImportJobState.Uploading] = [ImportJobState.Uploaded],
        [ImportJobState.Uploaded] = [ImportJobState.Analyzing],
        [ImportJobState.Analyzing] = [ImportJobState.Analyzed, ImportJobState.Uploaded],
        [ImportJobState.Analyzed] = [ImportJobState.Confirmed, ImportJobState.Uploaded],
        [ImportJobState.Confirmed] = [ImportJobState.Running, ImportJobState.Uploaded],
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
    /// <param name="error">Failure message, stored when the target is Failed.</param>
    public static ImportJobStatus Transition(ImportJobStatus status, ImportJobState to, DateTime utcNow, ImportMessage? error = null) =>
        TryTransition(status, to, utcNow, out var updated, error)
            ? updated
            : throw new InvalidOperationException($"Import job cannot move from {status.State} to {to}.");

    public static bool TryTransition(ImportJobStatus status, ImportJobState to, DateTime utcNow, out ImportJobStatus updated, ImportMessage? error = null)
    {
        if (!CanTransition(status.State, to))
        {
            updated = status;
            return false;
        }

        updated = status with { State = to, UpdatedUtc = utcNow };
        updated = to switch
        {
            ImportJobState.Uploaded => ResetProcessing(updated),
            ImportJobState.Analyzing => StartPhase(updated, ImportPhase.Analyze),
            ImportJobState.Running => StartPhase(updated, ImportPhase.Execute),
            ImportJobState.Completed or ImportJobState.Cancelled => Finish(updated, utcNow, null),
            ImportJobState.Failed => Finish(updated, utcNow, error ?? ImportMessages.Failed),
            ImportJobState.Configuring or ImportJobState.Uploading or ImportJobState.Analyzed or ImportJobState.Confirmed or ImportJobState.Paused => updated,
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
    /// Whether a driver works on the job right now: it is being analyzed or run and its heartbeat is fresh (the
    /// opposite of the derived <see cref="ImportJobState.Paused"/>).
    /// </summary>
    public static bool IsActivelyDriven(ImportJobStatus status, DateTime utcNow, TimeSpan heartbeatStale) =>
        IsDriven(status.State) && !IsHeartbeatStale(status, utcNow, heartbeatStale);

    /// <summary>
    /// Whether the job holds the one unfinished Run (PLAN D60): its import was confirmed and has not ended, so no other
    /// import may be confirmed or started. Stored states Confirmed and Running (also when the heartbeat is stale and the
    /// job shows as Paused); a status already read as Paused counts when it is in the execute phase. The heartbeat does
    /// not matter. Analyses (Uploaded, Analyzing, Analyzed, Paused in the analysis) and finished jobs never count.
    /// </summary>
    public static bool IsUnfinishedRun(ImportJobStatus status) =>
        status.State is ImportJobState.Confirmed or ImportJobState.Running
        || (status.State == ImportJobState.Paused && status.Checkpoint.Phase == ImportPhase.Execute);

    /// <summary>
    /// Whether the job <paramref name="other"/> keeps the confirmed job <paramref name="starting"/> from starting its
    /// Run (<c>Confirmed → Running</c>): <paramref name="other"/> is an unfinished Run (<see cref="IsUnfinishedRun"/>),
    /// except a job confirmed later that has not started either. Of two confirmed jobs (confirmed by an earlier version,
    /// before confirmations were serialized) the one confirmed first may start, so they never wait for each other.
    /// </summary>
    /// <param name="other">Status of the other job.</param>
    /// <param name="otherId">ID of the other job; decides between two jobs confirmed at the same time.</param>
    /// <param name="starting">Status of the job about to start.</param>
    /// <param name="startingId">ID of the job about to start.</param>
    public static bool BlocksRunStart(ImportJobStatus other, Guid otherId, ImportJobStatus starting, Guid startingId)
    {
        if (otherId == startingId || !IsUnfinishedRun(other))
        {
            return false;
        }

        if (other.State != ImportJobState.Confirmed || starting.State != ImportJobState.Confirmed)
        {
            return true;
        }

        int order = other.UpdatedUtc.CompareTo(starting.UpdatedUtc);

        return order < 0 || (order == 0 && otherId.CompareTo(startingId) < 0);
    }

    /// <summary>
    /// Whether the job ended an import that wrote contacts: Completed, or Failed or Cancelled in the execute phase.
    /// </summary>
    public static bool IsEndedRun(ImportJobStatus status) =>
        IsTerminal(status.State) && status.Checkpoint.Phase == ImportPhase.Execute && status.FinishedUtc is not null;

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
            || !current.Mapping.SequenceEqual(updated.Mapping)
            || current.ConsentMapping != updated.ConsentMapping;
        if (uploadAffected)
        {
            return ImportConfigChange.Mapping;
        }

        return current.Options == updated.Options ? ImportConfigChange.None : ImportConfigChange.Options;
    }

    /// <summary>
    /// Applies the rules for a configuration change (PLAN D50): a mapping change (file, key, mapping, consent columns,
    /// date format) is allowed only while the job is Configuring, before the upload starts; afterwards a different file
    /// or mapping is a new import. An options change after analysis returns the job to Uploaded (analyze again).
    /// No change is allowed once the job is running or finished; an unchanged configuration is always accepted.
    /// </summary>
    public static ImportConfigChangeResult ApplyConfigChange(ImportJobStatus status, ImportConfigChange change, DateTime utcNow)
    {
        if (change == ImportConfigChange.None)
        {
            return new ImportConfigChangeResult(true, status);
        }

        if (status.State is ImportJobState.Running or ImportJobState.Paused || IsTerminal(status.State))
        {
            return new ImportConfigChangeResult(false, status);
        }

        if (change == ImportConfigChange.Mapping)
        {
            return status.State == ImportJobState.Configuring
                ? new ImportConfigChangeResult(true, status with { UpdatedUtc = utcNow })
                : new ImportConfigChangeResult(false, status);
        }

        return status.State is ImportJobState.Analyzing or ImportJobState.Analyzed or ImportJobState.Confirmed
            ? new ImportConfigChangeResult(true, Transition(status, ImportJobState.Uploaded, utcNow))
            : new ImportConfigChangeResult(true, status with { UpdatedUtc = utcNow });
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
            return Acquired(status, ProposedOrNewDriverToken(driverToken), instanceName, utcNow);
        }

        if (!IsHeartbeatStale(status, utcNow, heartbeatStale))
        {
            return new ImportDriverResult(ImportDriverOutcome.ActiveElsewhere, status, null);
        }

        return takeOver
            ? Acquired(status, ProposedOrNewDriverToken(driverToken), instanceName, utcNow)
            : new ImportDriverResult(ImportDriverOutcome.StaleRequiresTakeOver, status, null);
    }

    /// <summary>
    /// The user paused the analysis or the Run in the tab that drives it: the heartbeat is cleared, so the job shows as
    /// <see cref="ImportJobState.Paused"/> at once instead of after the heartbeat age. Only the current driver
    /// (<paramref name="driverToken"/>) may do this, and only while the job is Analyzing or Running. The stored state and
    /// the driver token stay: the same tab continues without a take over; another tab or user takes over as from any
    /// paused job. A Paused Run still holds the one unfinished Run (<see cref="IsUnfinishedRun"/>).
    /// </summary>
    /// <param name="status">Current status.</param>
    /// <param name="driverToken">Driver token of the caller.</param>
    /// <param name="utcNow">Current time.</param>
    /// <param name="updated">Status to store when paused; <paramref name="status"/> otherwise.</param>
    /// <returns><c>false</c> when the caller does not drive the job or the job is not being analyzed or run.</returns>
    public static bool TryPauseByDriver(ImportJobStatus status, string? driverToken, DateTime utcNow, out ImportJobStatus updated)
    {
        if (!IsDriven(status.State)
            || status.DriverToken is null
            || !string.Equals(status.DriverToken, driverToken, StringComparison.Ordinal))
        {
            updated = status;
            return false;
        }

        updated = status.HeartbeatUtc is null ? status : status with { HeartbeatUtc = null, UpdatedUtc = utcNow };
        return true;
    }

    /// <summary>
    /// Token of a new driver: the one the browser made and stored before the call, when it is a GUID ("N" format),
    /// so a tab reloaded while its first call runs still knows its token; otherwise a new one.
    /// </summary>
    private static string ProposedOrNewDriverToken(string? proposed) =>
        proposed is { Length: 32 } && Guid.TryParseExact(proposed, "N", out var parsed) ? parsed.ToString("N") : NewDriverToken();

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
            PhaseStartedUtc = null,
            PhaseStartedByUserId = null,
            Error = null,
            ErrorArguments = null,
        };

    private static ImportJobStatus StartPhase(ImportJobStatus status, ImportPhase phase) =>
        status with
        {
            Checkpoint = new ImportCheckpoint { Phase = phase },
            Aggregates = new Dictionary<string, long>(),
            LongestBatchSeconds = 0,
            PhaseStartedUtc = status.UpdatedUtc,
            PhaseStartedByUserId = null,
            Error = null,
            ErrorArguments = null,
        };

    private static ImportJobStatus Finish(ImportJobStatus status, DateTime utcNow, ImportMessage? error) =>
        (status with { FinishedUtc = utcNow, DriverToken = null, DriverCallId = null }).WithError(error);
}

/// <summary>
/// What a job configuration change touches.
/// </summary>
internal enum ImportConfigChange
{
    None,

    /// <summary>Only processing behaviors changed.</summary>
    Options,

    /// <summary>File, key field, column mapping, consent columns or date format changed; allowed only before the upload starts.</summary>
    Mapping,
}

/// <param name="Allowed">False when the job's state does not accept the change; the change must be refused.</param>
/// <param name="Status">Status to store when allowed.</param>
internal sealed record ImportConfigChangeResult(bool Allowed, ImportJobStatus Status);

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
