using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

internal sealed class ImportProcessor(
    IImportJobStore store,
    IContactFieldSchemaProvider schemaProvider,
    IContactImportRepository repository,
    IImportJobCleanupService cleanup,
    IImportCacheInvalidator cacheInvalidator,
    IOptions<ContactsImporterOptions> options,
    TimeProvider timeProvider,
    ILogger<ImportProcessor> logger) : IImportProcessor
{
    internal const string UnexpectedError = "The import stopped because of an unexpected error. See the event log for details.";
    internal const string UnreadableJobError = "The import configuration cannot be read. Start a new import.";
    internal const string TransientError = "A temporary problem interrupted the import. It continues with the next attempt.";
    internal const string BusyError = "Another request is still processing the import. It continues with the next attempt.";
    internal const string CacheError = "The import is done, but clearing the cache failed. It is tried again with the next attempt.";

    private const int ConstraintConflictSqlError = 547;

    private static readonly EventId startedEvent = new(0, "CONTACT_IMPORT_START");
    private static readonly EventId finishedEvent = new(0, "CONTACT_IMPORT_FINISH");
    private static readonly EventId cancelledEvent = new(0, "CONTACT_IMPORT_CANCEL");
    private static readonly EventId failedEvent = new(0, "CONTACT_IMPORT_FAILURE");
    private static readonly EventId transientEvent = new(0, "CONTACT_IMPORT_RETRY");
    private static readonly EventId rejectedEvent = new(0, "CONTACT_IMPORT_REJECTED");

    // Timeout, deadlock victim, connection and availability errors, database read-only: the same call succeeds later.
    private static readonly HashSet<int> transientSqlErrors = [-2, 20, 64, 233, 1205, 3906, 3960, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920];

    public ImportProgress ProcessNext(Guid jobId, ImportPhase phase, string? driverToken, bool takeOver, CancellationToken cancellationToken = default)
    {
        ImportJob? job;
        try
        {
            job = store.ReadJob(jobId);
        }
        catch (InvalidDataException ex)
        {
            return store.ReadStatus(jobId) is { } unreadable ? Fail(jobId, phase, unreadable, UnreadableJobError, ex) : NotFound(phase);
        }

        if (job is null)
        {
            return NotFound(phase);
        }

        if (phase != ImportPhase.Execute)
        {
            // The analysis writes nothing to the database; calls that overlap are sorted out by the call ID in the status.
            return Process(job, phase, driverToken, takeOver, cancellationToken);
        }

        // One call at a time imports a job, enforced by the database: a call repeated by the browser (lost response,
        // timeout) or a take over waits here while an earlier call still looks up or writes contacts, so no two calls
        // decide about the same rows. Without it both could create the same contacts, because under read committed
        // snapshot isolation (the default of Azure SQL) a lookup does not see rows another call has not committed yet.
        IDisposable? jobLock;
        try
        {
            jobLock = repository.TryLockJob(jobId, options.Value.JobLockTimeout);
        }
        catch (Exception ex)
        {
            return store.ReadStatus(jobId) is { } current ? Transient(jobId, phase, current, driverToken, ex) : NotFound(phase);
        }

        if (jobLock is null)
        {
            return Busy(jobId, phase, driverToken, takeOver);
        }

        using (jobLock)
        {
            return Process(job, phase, driverToken, takeOver, cancellationToken);
        }
    }

    /// <summary>
    /// Another call holds the job lock. Reports what the caller would be told anyway (the job is finished, driven from
    /// another tab, ...) when that needs no status change; otherwise the call is to be repeated.
    /// </summary>
    private ImportProgress Busy(Guid jobId, ImportPhase phase, string? driverToken, bool takeOver)
    {
        var status = store.ReadStatus(jobId);
        if (status is null)
        {
            return NotFound(phase);
        }

        var decision = DecideStart(status, phase, driverToken, takeOver, UtcNow(), options.Value);
        if (decision.Stop is { } stop && ReferenceEquals(decision.Status, status))
        {
            return Progress(stop, phase, status, null);
        }

        logger.LogInformation(transientEvent, "Contact import {JobId}: {Phase}: another call is still processing the import; this call did nothing.", jobId, phase);

        return Progress(ImportProcessOutcome.TransientError, phase, status, driverToken, BusyError);
    }

    private ImportProgress Process(ImportJob job, ImportPhase phase, string? driverToken, bool takeOver, CancellationToken cancellationToken)
    {
        var jobId = job.JobId;
        var settings = options.Value;
        var utcNow = UtcNow();
        StartDecision? decision = null;
        ImportProcessingContext? context = null;
        string? contextError = null;
        bool contextChecked = false;
        Exception? schemaException = null;

        // The function can run more than once (see IImportJobStore.UpdateStatus), so it only decides from "current".
        var status = store.UpdateStatus(jobId, current =>
        {
            decision = DecideStart(current, phase, driverToken, takeOver, utcNow, settings);
            if (decision.Stop is not null)
            {
                return decision.Status;
            }

            if (!contextChecked)
            {
                try
                {
                    ImportProcessingContext.TryCreate(job, schemaProvider.GetMappableFields(), out context, out contextError);
                    contextChecked = true;
                }
                catch (Exception ex)
                {
                    schemaException = ex;

                    return current;
                }
            }

            // The job does not match the contact class (a mapped field is gone, the key or source field is not usable).
            // Unless the import is already writing contacts, the job goes back to Uploaded with the reason and keeps its
            // uploaded data: the marketer changes the options or the mapping instead of uploading the file again.
            if (context is null && current.State != ImportJobState.Running)
            {
                decision = new StartDecision(RejectConfiguration(current, utcNow, contextError!), ImportProcessOutcome.Failed, ConfigurationRejected: true);
            }

            return decision.Status;
        });
        if (status is null || decision is null)
        {
            return NotFound(phase);
        }

        if (schemaException is not null)
        {
            return IsTransient(schemaException)
                ? Transient(jobId, phase, status, driverToken, schemaException)
                : Fail(jobId, phase, status, UnexpectedError, schemaException);
        }

        if (decision.Stop is { } stop)
        {
            if (decision.ConfigurationRejected)
            {
                logger.LogWarning(rejectedEvent, "Contact import {JobId}: {Phase} cannot start; the import returned to the options. {Message}", jobId, phase, status.Error);
            }

            return Progress(stop, phase, status, null);
        }

        var driver = new DriverIdentity(decision.Token!, decision.CallId!);
        if (context is null)
        {
            // The contact class changed while the import was writing contacts.
            return Fail(jobId, phase, status, contextError!, null, driver);
        }

        if (decision.Started)
        {
            logger.LogInformation(startedEvent, "Contact import {JobId}: {Phase} started ({Rows} rows in {Chunks} chunks).", jobId, phase, status.Profile.RowsValid, status.Chunks.Count);
        }

        return Run(job, context, phase, status, driver, cancellationToken);
    }

    public ImportProgress? GetProgress(Guid jobId)
    {
        var status = store.ReadStatus(jobId);

        return status is null ? null : Progress(ImportProcessOutcome.Processed, status.Checkpoint.Phase, status, null);
    }

    public ImportConfirmResult Confirm(Guid jobId, ImportAttestation? attestation = null)
    {
        var job = store.ReadJob(jobId);
        if (job is null)
        {
            return new ImportConfirmResult(ImportConfirmOutcome.JobNotFound);
        }

        var utcNow = UtcNow();
        var outcome = ImportConfirmOutcome.InvalidState;
        var status = store.UpdateStatus(jobId, current =>
        {
            if (current.State == ImportJobState.Confirmed)
            {
                outcome = ImportConfirmOutcome.Confirmed;
                return current;
            }

            if (current.State != ImportJobState.Analyzed)
            {
                outcome = ImportConfirmOutcome.InvalidState;
                return current;
            }

            if (IsAnalysisExpired(current, utcNow, options.Value))
            {
                outcome = ImportConfirmOutcome.AnalysisExpired;
                return ImportJobStateMachine.Transition(current, ImportJobState.Uploaded, utcNow);
            }

            outcome = ImportConfirmOutcome.Confirmed;
            return ImportJobStateMachine.Transition(current, ImportJobState.Confirmed, utcNow);
        });
        if (status is null)
        {
            return new ImportConfirmResult(ImportConfirmOutcome.JobNotFound);
        }

        if (outcome == ImportConfirmOutcome.Confirmed && attestation is not null && job.Attestation != attestation)
        {
            store.WriteJob(job with { Attestation = attestation });
        }

        return new ImportConfirmResult(outcome, status);
    }

    public ImportCancelResult Cancel(Guid jobId)
    {
        var utcNow = UtcNow();
        var outcome = ImportCancelOutcome.AlreadyFinished;
        var status = store.UpdateStatus(jobId, current =>
        {
            if (ImportJobStateMachine.IsTerminal(current.State))
            {
                outcome = ImportCancelOutcome.AlreadyFinished;
                return current;
            }

            outcome = ImportCancelOutcome.Cancelled;
            return ImportJobStateMachine.Transition(current, ImportJobState.Cancelled, utcNow);
        });
        if (status is null)
        {
            return new ImportCancelResult(ImportCancelOutcome.JobNotFound);
        }

        if (outcome == ImportCancelOutcome.Cancelled)
        {
            logger.LogInformation(cancelledEvent, "Contact import {JobId} was cancelled after {Rows} processed rows.", jobId, status.Aggregates.GetValueOrDefault(ImportAggregateKeys.Rows));
            TryDeleteChunks(jobId);
        }

        return new ImportCancelResult(outcome, status);
    }

    /// <summary>
    /// Whether an exception is a temporary condition: the same call can succeed later, so the job must not fail.
    /// </summary>
    internal static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or IOException)
            {
                return true;
            }

            if (current is SqlException sqlException)
            {
                foreach (SqlError error in sqlException.Errors)
                {
                    if (transientSqlErrors.Contains(error.Number))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an exception is SQL Server error 547: a statement conflicted with a foreign key (or check) constraint.
    /// </summary>
    internal static bool IsConstraintConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException && sqlException.Errors.Cast<SqlError>().Any(error => error.Number == ConstraintConflictSqlError))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides, inside the status update, whether this call may work on the job: checks the state for the phase,
    /// the drift guard, claims the driver role and starts the phase when it has not started.
    /// </summary>
    internal static StartDecision DecideStart(ImportJobStatus current, ImportPhase phase, string? driverToken, bool takeOver, DateTime utcNow, ContactsImporterOptions settings)
    {
        var startState = phase == ImportPhase.Analyze ? ImportJobState.Uploaded : ImportJobState.Confirmed;
        var finishedState = phase == ImportPhase.Analyze ? ImportJobState.Analyzed : ImportJobState.Completed;

        if (current.State == finishedState)
        {
            return new StartDecision(current, ImportProcessOutcome.PhaseFinished);
        }

        if (current.State != startState && current.State != DrivenState(phase))
        {
            return new StartDecision(current, ImportProcessOutcome.InvalidState);
        }

        bool starts = current.State == startState;
        if (starts && phase == ImportPhase.Execute && IsAnalysisExpired(current, utcNow, settings))
        {
            return new StartDecision(ImportJobStateMachine.Transition(current, ImportJobState.Uploaded, utcNow), ImportProcessOutcome.AnalysisExpired);
        }

        // The heartbeat age is the configured one, extended for a job with slow batches (ImportJobStateMachine.GetHeartbeatStale).
        var driver = ImportJobStateMachine.AcquireDriver(current, driverToken, takeOver, Environment.MachineName, utcNow, settings.HeartbeatStale);
        switch (driver.Outcome)
        {
            case ImportDriverOutcome.Acquired:
                break;
            case ImportDriverOutcome.ActiveElsewhere:
                return new StartDecision(current, ImportProcessOutcome.ActiveElsewhere);
            case ImportDriverOutcome.StaleRequiresTakeOver:
                return new StartDecision(current, ImportProcessOutcome.StaleRequiresTakeOver);
            case ImportDriverOutcome.JobFinished:
            default:
                return new StartDecision(current, ImportProcessOutcome.InvalidState);
        }

        var status = starts ? ImportJobStateMachine.Transition(driver.Status, DrivenState(phase), utcNow) : driver.Status;

        return new StartDecision(status, null, driver.DriverToken, starts, driver.CallId);
    }

    /// <summary>
    /// Status of a job whose configuration cannot be processed, found before the import wrote anything: back in
    /// Uploaded (analysis and driver cleared, uploaded data kept) with the reason in <see cref="ImportJobStatus.Error"/>.
    /// The next start of a phase clears the reason.
    /// </summary>
    internal static ImportJobStatus RejectConfiguration(ImportJobStatus current, DateTime utcNow, string error)
    {
        var uploaded = current.State == ImportJobState.Uploaded
            ? current with { UpdatedUtc = utcNow }
            : ImportJobStateMachine.Transition(current, ImportJobState.Uploaded, utcNow);

        return uploaded with { Error = error };
    }

    private static ImportJobState DrivenState(ImportPhase phase) => phase == ImportPhase.Analyze ? ImportJobState.Analyzing : ImportJobState.Running;

    private static bool IsAnalysisExpired(ImportJobStatus status, DateTime utcNow, ContactsImporterOptions settings) =>
        status.Analysis is not { } analysis || utcNow - analysis.FinishedUtc > settings.AnalysisValid;

    private static ImportProgress NotFound(ImportPhase phase) => new() { Outcome = ImportProcessOutcome.JobNotFound, Phase = phase };

    private ImportProgress Run(ImportJob job, ImportProcessingContext context, ImportPhase phase, ImportJobStatus status, DriverIdentity driver, CancellationToken cancellationToken)
    {
        var jobId = job.JobId;
        var settings = options.Value;
        ChunkRecordReader? reader = null;
        try
        {
            // Parts beyond the checkpoint are left from an earlier attempt of this phase.
            DeleteReportParts(jobId, phase, status.Checkpoint.LastRow);

            var planner = new BatchPlanner(context);
            var report = new ImportReportPartWriter(store, jobId, phase, job.Options.ReportIncludesKey);
            var startedUtc = UtcNow();

            while (true)
            {
                var checkpoint = status.Checkpoint;
                if (checkpoint.ChunkIndex >= status.Chunks.Count)
                {
                    return Finish(job, phase, status, driver);
                }

                var batchStartedUtc = UtcNow();
                var chunk = status.Chunks[checkpoint.ChunkIndex];
                reader ??= OpenChunk(jobId, context, checkpoint);
                var records = reader.ReadBatch(settings.BatchSize);
                if (records.Count == 0)
                {
                    throw new ImportDataException($"Uploaded data chunk {checkpoint.ChunkIndex} is incomplete. Upload the file again.");
                }

                var plan = PlanBatch(planner, context, records);

                // Dry run = same code path with the writes off (PLAN D2).
                if (phase == ImportPhase.Execute && (plan.Creates.Count > 0 || plan.Updates.Count > 0))
                {
                    // The lookup can take long on a busy database. Before anything is written, make sure this call
                    // still drives the job (not cancelled, not taken over, not replaced by a repeated call) and
                    // refresh the heartbeat, so nobody takes over during the write.
                    if (!TryKeepDriving(jobId, phase, driver, settings, ref status))
                    {
                        reader.Dispose();
                        reader = null;

                        return Interrupted(jobId, phase, status);
                    }

                    try
                    {
                        repository.Write(context, plan.Creates, plan.Updates, timeProvider.GetLocalNow().DateTime);
                    }
                    catch (Exception ex) when (context.ReferenceGroups.Count > 0 && IsConstraintConflict(ex))
                    {
                        // A referenced object (country, status, user, ...) was deleted after the batch was checked.
                        // The write was rolled back as a whole, so the batch is planned once more: the check now skips
                        // the rows that refer to it. A second conflict is not this race and fails the job as before.
                        LogSafely(LogLevel.Warning, transientEvent, jobId, phase, "a referenced object was removed while a batch was written; the batch is checked and written again", ex);
                        plan = PlanBatch(planner, context, records);
                        if (plan.Creates.Count > 0 || plan.Updates.Count > 0)
                        {
                            repository.Write(context, plan.Creates, plan.Updates, timeProvider.GetLocalNow().DateTime);
                        }
                    }
                }

                report.Append(plan.Rows);

                long offset = checkpoint.RowOffset + records.Count;
                bool chunkDone = offset >= chunk.Rows;
                if (chunkDone)
                {
                    reader.Dispose();
                    reader = null;
                }

                var next = new ImportCheckpoint
                {
                    Phase = phase,
                    ChunkIndex = chunkDone ? checkpoint.ChunkIndex + 1 : checkpoint.ChunkIndex,
                    RowOffset = chunkDone ? 0 : offset,
                    LastRow = records[^1].Row,
                };
                if (!TrySaveCheckpoint(jobId, phase, driver, next, plan.Counts, (UtcNow() - batchStartedUtc).TotalSeconds, ref status))
                {
                    reader?.Dispose();
                    reader = null;

                    return Interrupted(jobId, phase, status);
                }

                bool moreRows = next.ChunkIndex < status.Chunks.Count;
                if (moreRows && (cancellationToken.IsCancellationRequested || UtcNow() - startedUtc >= settings.ProcessingTimeBudget))
                {
                    return Progress(ImportProcessOutcome.Processed, phase, status, driver.Token);
                }
            }
        }
        catch (ImportDataException ex)
        {
            reader?.Dispose();
            reader = null;

            return Fail(jobId, phase, status, ex.Message, ex, driver);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return Transient(jobId, phase, status, driver.Token, ex);
        }
        catch (Exception ex)
        {
            reader?.Dispose();
            reader = null;

            return Fail(jobId, phase, status, UnexpectedError, ex, driver);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    private BatchPlan PlanBatch(BatchPlanner planner, ImportProcessingContext context, List<ImportRecord> records)
    {
        // References are checked after value conversion and before the keys are looked up: in the analysis and in the
        // import alike, and in the import under the job lock on its connection.
        var batch = planner.Prepare(records, repository.FindMissingReferences);
        var matches = repository.FindMatches(context, [.. batch.Groups.Select(g => g.KeyValue)]);
        var equivalentKeys = batch.HasSimilarKeys ? repository.FindEquivalentKeys([.. batch.Groups.Select(g => g.KeyText)]) : null;

        return planner.Classify(batch, matches, equivalentKeys);
    }

    private ChunkRecordReader OpenChunk(Guid jobId, ImportProcessingContext context, ImportCheckpoint checkpoint)
    {
        var stream = store.OpenChunk(jobId, checkpoint.ChunkIndex)
            ?? throw new ImportDataException($"Uploaded data chunk {checkpoint.ChunkIndex} is missing. Upload the file again.");
        var reader = new ChunkRecordReader(stream, context, checkpoint.ChunkIndex);
        try
        {
            if (reader.Skip(checkpoint.RowOffset) != checkpoint.RowOffset)
            {
                throw new ImportDataException($"Uploaded data chunk {checkpoint.ChunkIndex} is incomplete. Upload the file again.");
            }

            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Checks, against the stored status, that this call still drives the job. Stores a heartbeat when the last one is
    /// older than a quarter of the heartbeat age; a batch of normal speed never gets there, its checkpoint is the heartbeat.
    /// </summary>
    private bool TryKeepDriving(Guid jobId, ImportPhase phase, DriverIdentity driver, ContactsImporterOptions settings, ref ImportJobStatus status)
    {
        var utcNow = UtcNow();
        var refreshAfter = settings.HeartbeatStale / 4;
        bool driving = false;
        var updated = store.UpdateStatus(jobId, current =>
        {
            driving = IsDrivenBy(current, phase, driver);

            return driving && (current.HeartbeatUtc is not { } heartbeat || utcNow - heartbeat >= refreshAfter)
                ? current with { HeartbeatUtc = utcNow }
                : current;
        });
        if (updated is null)
        {
            return false;
        }

        status = updated;

        return driving;
    }

    /// <summary>
    /// Stores the checkpoint, the counts and a heartbeat, unless the job was cancelled or taken over meanwhile.
    /// </summary>
    private bool TrySaveCheckpoint(Guid jobId, ImportPhase phase, DriverIdentity driver, ImportCheckpoint checkpoint, ImportCounts counts, double batchSeconds, ref ImportJobStatus status)
    {
        var utcNow = UtcNow();
        bool saved = false;
        var updated = store.UpdateStatus(jobId, current =>
        {
            saved = IsDrivenBy(current, phase, driver);

            return saved
                ? current with
                {
                    Checkpoint = checkpoint,
                    Aggregates = counts.AddTo(current.Aggregates),
                    HeartbeatUtc = utcNow,
                    UpdatedUtc = utcNow,
                    LongestBatchSeconds = Math.Max(current.LongestBatchSeconds, batchSeconds),
                }
                : current;
        });
        if (updated is null)
        {
            return false;
        }

        status = updated;

        return saved;
    }

    private ImportProgress Finish(ImportJob job, ImportPhase phase, ImportJobStatus status, DriverIdentity driver)
    {
        var jobId = job.JobId;
        if (phase == ImportPhase.Execute)
        {
            // Before the job is stored as Completed: when this fails or the application stops, the next call clears again.
            try
            {
                cacheInvalidator.InvalidateAfterImport(job);
            }
            catch (Exception ex)
            {
                LogSafely(LogLevel.Error, failedEvent, jobId, phase, "clearing the cache after the import failed", ex);

                return Progress(ImportProcessOutcome.TransientError, phase, status, driver.Token, CacheError);
            }
        }

        var utcNow = UtcNow();
        bool finished = false;
        var updated = store.UpdateStatus(jobId, current =>
        {
            finished = IsDrivenBy(current, phase, driver);
            if (!finished)
            {
                return current;
            }

            return phase == ImportPhase.Analyze
                ? ImportJobStateMachine.Transition(current, ImportJobState.Analyzed, utcNow) with
                {
                    Analysis = new ImportAnalysisSummary { FinishedUtc = utcNow, Aggregates = current.Aggregates },
                    DriverToken = null,
                    DriverCallId = null,
                    HeartbeatUtc = null,
                }
                : ImportJobStateMachine.Transition(current, ImportJobState.Completed, utcNow);
        }) ?? status;
        if (!finished)
        {
            return Interrupted(jobId, phase, updated);
        }

        if (phase == ImportPhase.Execute)
        {
            TryDeleteChunks(jobId);
        }

        var totals = ImportAggregates.From(updated.Aggregates);
        logger.LogInformation(
            finishedEvent,
            "Contact import {JobId}: {Phase} finished. Rows {Rows}, created {Created}, updated {Updated}, unchanged {Unchanged}, skipped {Skipped}, duplicate rows {DuplicateRows}.",
            jobId,
            phase,
            totals.Rows,
            totals.Created,
            totals.Updated,
            totals.Unchanged,
            totals.Skipped,
            totals.DuplicateRows);

        return Progress(ImportProcessOutcome.PhaseFinished, phase, updated, null);
    }

    private ImportProgress Interrupted(Guid jobId, ImportPhase phase, ImportJobStatus status)
    {
        if (ImportJobStateMachine.IsTerminal(status.State))
        {
            // A cancellation could not delete the chunk this call was reading.
            TryDeleteChunks(jobId);
        }

        return Progress(ImportProcessOutcome.Interrupted, phase, status, null);
    }

    /// <summary>
    /// Fails the job. A call that knows its driver identity fails the job only while it still drives it: an older call
    /// that was replaced or taken over must not end the job for the driver that continues it.
    /// </summary>
    private ImportProgress Fail(Guid jobId, ImportPhase phase, ImportJobStatus status, string safeMessage, Exception? exception, DriverIdentity? driver = null)
    {
        var utcNow = UtcNow();
        var updated = store.UpdateStatus(jobId, current =>
            ImportJobStateMachine.IsTerminal(current.State) || (driver is not null && !IsDrivenBy(current, phase, driver))
                ? current
                : ImportJobStateMachine.Transition(current, ImportJobState.Failed, utcNow, safeMessage)) ?? status;

        // Exception messages of the database can quote contact data (truncated or duplicate values), so only the
        // exception types, SQL error numbers and the stack trace are logged.
        logger.LogError(
            failedEvent,
            "Contact import {JobId}: {Phase} failed after row {LastRow} (chunk {Chunk}). {Message} Error: {Error}. Stack trace: {StackTrace}",
            jobId,
            phase,
            updated.Checkpoint.LastRow,
            updated.Checkpoint.ChunkIndex,
            safeMessage,
            exception is null ? "none" : DescribeSafely(exception),
            exception?.StackTrace ?? "none");

        if (updated.State != ImportJobState.Failed)
        {
            return Interrupted(jobId, phase, updated);
        }

        TryDeleteChunks(jobId);

        return Progress(ImportProcessOutcome.Failed, phase, updated, null);
    }

    private ImportProgress Progress(ImportProcessOutcome outcome, ImportPhase phase, ImportJobStatus status, string? token, string? error = null) =>
        new()
        {
            Outcome = outcome,
            Phase = phase,
            State = ImportJobStateMachine.GetEffectiveState(status, UtcNow(), options.Value.HeartbeatStale),
            DriverToken = token,
            RowsProcessed = status.Aggregates.GetValueOrDefault(ImportAggregateKeys.Rows),
            RowsTotal = status.Profile.RowsValid,
            Aggregates = ImportAggregates.From(status.Aggregates),
            Analysis = status.Analysis is null ? null : ImportAggregates.From(status.Analysis.Aggregates),
            AnalyzedUtc = status.Analysis?.FinishedUtc,
            Error = error ?? status.Error,
        };

    private static bool IsDrivenBy(ImportJobStatus status, ImportPhase phase, DriverIdentity driver) =>
        status.State == DrivenState(phase)
        && string.Equals(status.DriverToken, driver.Token, StringComparison.Ordinal)
        && string.Equals(status.DriverCallId, driver.CallId, StringComparison.Ordinal);

    private void DeleteReportParts(Guid jobId, ImportPhase phase, long afterRow)
    {
        foreach (var (fileName, firstRow) in ImportReportReader.ListParts(store, jobId, phase))
        {
            if (firstRow > afterRow)
            {
                store.DeleteReport(jobId, fileName);
            }
        }
    }

    /// <summary>
    /// Deletes the uploaded data of a finished job. When a file is still open (a processing call is reading it),
    /// that call or the cleanup sweep deletes it later.
    /// </summary>
    private void TryDeleteChunks(Guid jobId)
    {
        try
        {
            cleanup.DeleteChunks(jobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Contact import {JobId}: uploaded data could not be deleted yet; the cleanup deletes it later.", jobId);
        }
    }

    private ImportProgress Transient(Guid jobId, ImportPhase phase, ImportJobStatus status, string? token, Exception exception)
    {
        LogSafely(LogLevel.Warning, transientEvent, jobId, phase, "a temporary problem interrupted the call; the next call continues", exception);

        return Progress(ImportProcessOutcome.TransientError, phase, store.ReadStatus(jobId) ?? status, token, TransientError);
    }

    /// <summary>
    /// Logs a problem without the exception object: database error messages can quote contact data (truncated or
    /// duplicate values), so only exception types and SQL error numbers are logged.
    /// </summary>
    private void LogSafely(LogLevel level, EventId eventId, Guid jobId, ImportPhase phase, string what, Exception exception) =>
        logger.Log(level, eventId, "Contact import {JobId}: {Phase}: {What} ({Error}).", jobId, phase, what, DescribeSafely(exception));

    private static string DescribeSafely(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            string description = current.GetType().Name;
            if (current is SqlException sqlException)
            {
                description += " (SQL error " + string.Join(", ", sqlException.Errors.Cast<SqlError>().Select(e => e.Number).Distinct()) + ")";
            }

            parts.Add(description);
        }

        return string.Join(" <- ", parts);
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    /// <param name="Token">Driver token the browser sends with every call.</param>
    /// <param name="CallId">ID of this call (<see cref="ImportJobStatus.DriverCallId"/>).</param>
    private sealed record DriverIdentity(string Token, string CallId);
}

/// <param name="Status">Status to store.</param>
/// <param name="Stop">Outcome to return without processing; <c>null</c> when the call may process.</param>
/// <param name="Token">Driver token of the caller when it may process.</param>
/// <param name="Started">The phase was started by this call.</param>
/// <param name="CallId">ID of this processing call when it may process.</param>
/// <param name="ConfigurationRejected">The job cannot be processed as configured and returned to Uploaded.</param>
internal sealed record StartDecision(ImportJobStatus Status, ImportProcessOutcome? Stop, string? Token = null, bool Started = false, string? CallId = null, bool ConfigurationRejected = false);
