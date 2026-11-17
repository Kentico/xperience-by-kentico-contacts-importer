using System.Globalization;

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
    ILogger<ImportProcessor> logger,
    IImportAudienceResolver? audienceResolver = null,
    IImportEventPublisher? events = null) : IImportProcessor
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
    private static readonly EventId attestationEvent = new(0, "CONTACT_IMPORT_ATTESTATION");

    // Timeout, deadlock victim, connection and availability errors, database read-only: the same call succeeds later.
    private static readonly HashSet<int> transientSqlErrors = [-2, 20, 64, 233, 1205, 3906, 3960, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920];

    public ImportProgress ProcessNext(Guid jobId, ImportPhase phase, string? driverToken, bool takeOver, CancellationToken cancellationToken = default, int? userId = null)
    {
        var call = new ProcessCall(userId);
        var progress = ProcessNext(jobId, phase, driverToken, takeOver, call, cancellationToken);

        // After the job lock is released: handlers neither run on its connection nor extend the time it is held.
        if (call.Reached is { } reached)
        {
            events?.Publish(reached.Job, reached.Status, reached.Outcome, reached.IsDryRun, userId, reached.Audience);
        }

        return progress;
    }

    private ImportProgress ProcessNext(Guid jobId, ImportPhase phase, string? driverToken, bool takeOver, ProcessCall call, CancellationToken cancellationToken)
    {
        ImportJob? job;
        try
        {
            job = store.ReadJob(jobId);
        }
        catch (InvalidDataException ex)
        {
            // Without a readable configuration there is nothing to report in an event; the failure is in the event log.
            return store.ReadStatus(jobId) is { } unreadable ? Fail(jobId, phase, unreadable, UnreadableJobError, ex) : NotFound(phase);
        }

        if (job is null)
        {
            return NotFound(phase);
        }

        if (phase != ImportPhase.Execute)
        {
            // The analysis writes nothing to the database; calls that overlap are sorted out by the call ID in the status.
            return Process(job, phase, driverToken, takeOver, call, cancellationToken);
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
            return Process(job, phase, driverToken, takeOver, call, cancellationToken);
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

    private ImportProgress Process(ImportJob job, ImportPhase phase, string? driverToken, bool takeOver, ProcessCall call, CancellationToken cancellationToken)
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
                    context = CreateContext(job, phase, out contextError);
                    contextChecked = true;
                }
                catch (Exception ex)
                {
                    schemaException = ex;

                    return current;
                }
            }

            // The job cannot be processed as configured: it does not match the contact class (a mapped field is gone, the
            // key or source field is not usable), its recipient list or consent is gone, or it needs an attestation
            // of the consent statement and has none. Unless the import is already writing contacts, the job goes back to Uploaded with the reason and keeps its
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
                : Fail(jobId, phase, status, UnexpectedError, schemaException, null, call, job);
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
            // The contact class, the recipient list or the consent changed while the import was writing contacts.
            return Fail(jobId, phase, status, contextError!, null, driver, call, job);
        }

        if (decision.Started)
        {
            logger.LogInformation(
                startedEvent,
                "Contact import {JobId}: {Phase} started by user {UserId} (import created by user {CreatedByUserId}): {Rows} rows in {Chunks} chunks, recipient list {RecipientListId}, consent {ConsentId}.",
                jobId,
                phase,
                FormatUser(call.UserId),
                job.CreatedByUserId,
                status.Profile.RowsValid,
                status.Chunks.Count,
                FormatId(context.Audience.RecipientListId),
                FormatId(context.Audience.ConsentId));
        }

        return Run(job, context, phase, status, driver, call, cancellationToken);
    }

    /// <summary>
    /// Builds the processing context of a call. Fails (message without contact data) when the import must not run
    /// as configured: it needs an attestation of the consent statement and has none for the current statement, its
    /// recipient list or consent does not exist, or the job does not match the contact class.
    /// </summary>
    private ImportProcessingContext? CreateContext(ImportJob job, ImportPhase phase, out string? error)
    {
        // The analysis writes nothing, so only the import needs the attestation.
        if (phase == ImportPhase.Execute && !ImportAttestationPolicy.IsSatisfied(job.Options, job.Attestation))
        {
            error = ImportAttestationPolicy.MissingError;

            return null;
        }

        // Without a resolver nothing vouches for the list and the consent: the context then refuses a job that uses them.
        var resolution = audienceResolver?.Resolve(job.Options) ?? new ImportAudienceResolution(ImportAudience.None);
        if (resolution.Audience is null)
        {
            error = resolution.Error ?? UnexpectedError;

            return null;
        }

        ImportProcessingContext.TryCreate(job, schemaProvider.GetMappableFields(), out var context, out error, resolution.Audience);

        return context;
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
        bool required = ImportAttestationPolicy.IsRequired(job.Options);
        bool attestationStored = false;
        if (required)
        {
            var before = store.ReadStatus(jobId);
            if (before is null)
            {
                return new ImportConfirmResult(ImportConfirmOutcome.JobNotFound);
            }

            if (before.State == ImportJobState.Analyzed && !IsAnalysisExpired(before, utcNow, options.Value))
            {
                if (!ImportAttestationPolicy.IsSatisfied(job.Options, attestation))
                {
                    return new ImportConfirmResult(ImportConfirmOutcome.AttestationRequired, before);
                }

                // Stored before the job becomes Confirmed, so a confirmed job that needs an attestation always has it
                // (the import checks again when it starts).
                store.WriteJob(job with { Attestation = attestation });
                attestationStored = true;
            }
        }

        var outcome = ImportConfirmOutcome.InvalidState;
        bool confirmedNow = false;
        var status = store.UpdateStatus(jobId, current =>
        {
            confirmedNow = false;
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

            // A job that needs an attestation is confirmed only by the call that stored one.
            if (required && !attestationStored)
            {
                outcome = ImportConfirmOutcome.AttestationRequired;
                return current;
            }

            outcome = ImportConfirmOutcome.Confirmed;
            confirmedNow = true;
            return ImportJobStateMachine.Transition(current, ImportJobState.Confirmed, utcNow);
        });
        if (status is null)
        {
            return new ImportConfirmResult(ImportConfirmOutcome.JobNotFound);
        }

        if (attestationStored && outcome != ImportConfirmOutcome.Confirmed)
        {
            // Not confirmed after all (the state changed meanwhile): the attestation does not stay behind.
            store.WriteJob(job with { Attestation = null });
        }
        else if (!required && outcome == ImportConfirmOutcome.Confirmed && attestation is not null && job.Attestation != attestation)
        {
            store.WriteJob(job with { Attestation = attestation });
        }

        if (confirmedNow && required)
        {
            logger.LogInformation(
                attestationEvent,
                "Contact import {JobId}: user {UserId} confirmed the import and attested documented consent (statement version {StatementVersion}). Recipient list {RecipientListId}, unsubscribed contacts: {Unsubscribed}, bounced addresses: {BouncedAddress}; consent {ConsentId}, mode {Consent}, revoked consent: {RevokedConsent}.",
                jobId,
                attestation!.UserId,
                attestation.StatementVersion,
                FormatId(job.Options.RecipientListId),
                job.Options.Unsubscribed,
                job.Options.BouncedAddress,
                FormatId(job.Options.Consent == ConsentMode.None ? null : job.Options.ConsentId),
                job.Options.Consent,
                job.Options.RevokedConsent);
        }

        return new ImportConfirmResult(outcome, status);
    }

    public ImportCancelResult Cancel(Guid jobId, int? userId = null)
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
            logger.LogInformation(
                cancelledEvent,
                "Contact import {JobId} was cancelled by user {UserId} after {Rows} processed rows ({Phase}).",
                jobId,
                FormatUser(userId),
                status.Aggregates.GetValueOrDefault(ImportAggregateKeys.Rows),
                status.Checkpoint.Phase);
            TryDeleteChunks(jobId);
            PublishCancelled(jobId, status, userId);
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

    /// <summary>
    /// Raises the event of a cancelled job. Nothing was written when the import (execute phase) never started.
    /// </summary>
    private void PublishCancelled(Guid jobId, ImportJobStatus status, int? userId)
    {
        if (events is null)
        {
            return;
        }

        ImportJob? job;
        try
        {
            job = store.ReadJob(jobId);
        }
        catch (InvalidDataException)
        {
            job = null;
        }

        if (job is not null)
        {
            events.Publish(job, status, ContactImportStatus.Cancelled, status.Checkpoint.Phase != ImportPhase.Execute, userId);
        }
    }

    private ImportProgress Run(ImportJob job, ImportProcessingContext context, ImportPhase phase, ImportJobStatus status, DriverIdentity driver, ProcessCall call, CancellationToken cancellationToken)
    {
        var jobId = job.JobId;
        var settings = options.Value;
        ChunkRecordReader? reader = null;
        try
        {
            // Parts beyond the checkpoint are left from an earlier attempt of this phase.
            DeleteReportParts(jobId, phase, status.Checkpoint.LastRow);

            var planner = new BatchPlanner(context);
            var report = new ImportReportPartWriter(store, jobId, phase, ImportReportColumns.From(job.Options));
            var startedUtc = UtcNow();

            while (true)
            {
                var checkpoint = status.Checkpoint;
                if (checkpoint.ChunkIndex >= status.Chunks.Count)
                {
                    return Finish(job, context, phase, status, driver, call);
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
                AudienceResult? audience = null;
                if (phase == ImportPhase.Execute && HasWrites(plan))
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
                        audience = WriteBatch(context, plan);
                    }
                    catch (Exception ex) when ((context.ReferenceGroups.Count > 0 || context.HasAudience) && IsConstraintConflict(ex))
                    {
                        // A referenced object (country, status, user, ...) was deleted after the batch was checked, or
                        // a contact was deleted before its membership or agreement was inserted. The write was rolled
                        // back as a whole, so the batch is planned once more: the checks now see what is gone.
                        // A second conflict is not this race and fails the job as before.
                        LogSafely(LogLevel.Warning, transientEvent, jobId, phase, "a referenced object was removed while a batch was written; the batch is checked and written again", ex);
                        plan = PlanBatch(planner, context, records);
                        if (HasWrites(plan))
                        {
                            audience = WriteBatch(context, plan);
                        }
                    }
                }
                else if (plan.Targets.Count > 0)
                {
                    // The analysis decides from the same state query with the same code; it only inserts nothing.
                    audience = AudiencePlanner.Decide(context, plan.Targets, repository.FindAudienceStates(context, plan.Targets), timeProvider.GetLocalNow().DateTime);
                }

                if (audience is not null)
                {
                    plan.Counts.Add(audience.Counts);
                }

                report.Append(audience?.Annotate(plan.Rows) ?? plan.Rows);

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

            return Fail(jobId, phase, status, ex.Message, ex, driver, call, job);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return Transient(jobId, phase, status, driver.Token, ex);
        }
        catch (Exception ex)
        {
            reader?.Dispose();
            reader = null;

            return Fail(jobId, phase, status, UnexpectedError, ex, driver, call, job);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    private static bool HasWrites(BatchPlan plan) => plan.Creates.Count > 0 || plan.Updates.Count > 0 || plan.Targets.Count > 0;

    /// <summary>
    /// Writes a batch. The recipient list and consent decisions are made inside the write transaction, from the
    /// state read there (<see cref="IContactImportRepository.Write"/>).
    /// </summary>
    private AudienceResult? WriteBatch(ImportProcessingContext context, BatchPlan plan)
    {
        var now = timeProvider.GetLocalNow().DateTime;
        var audience = plan.Targets.Count > 0
            ? new AudienceWrite(plan.Targets, states => AudiencePlanner.Decide(context, plan.Targets, states, now))
            : null;

        return repository.Write(context, plan.Creates, plan.Updates, now, audience);
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

    private ImportProgress Finish(ImportJob job, ImportProcessingContext context, ImportPhase phase, ImportJobStatus status, DriverIdentity driver, ProcessCall call)
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
            "Contact import {JobId}: {Phase} finished by user {UserId} (import created by user {CreatedByUserId}). Rows {Rows}, created {Created}, updated {Updated}, unchanged {Unchanged}, skipped {Skipped}, duplicate rows {DuplicateRows}. Recipient list {RecipientListId}: added {ListAdded}, resubscribed {ListResubscribed}, kept unsubscribed {ListKeptUnsubscribed}. Consent {ConsentId}: recorded {ConsentRecorded}, agreed again {ConsentReAgreed}, kept revoked {ConsentKeptRevoked}.",
            jobId,
            phase,
            FormatUser(call.UserId),
            job.CreatedByUserId,
            totals.Rows,
            totals.Created,
            totals.Updated,
            totals.Unchanged,
            totals.Skipped,
            totals.DuplicateRows,
            FormatId(context.Audience.RecipientListId),
            totals.ListAdded,
            totals.ListResubscribed,
            totals.ListKeptUnsubscribed,
            FormatId(context.Audience.ConsentId),
            totals.ConsentRecorded,
            totals.ConsentReAgreed,
            totals.ConsentKeptRevoked);

        call.Reach(job, updated, phase == ImportPhase.Analyze ? ContactImportStatus.Analyzed : ContactImportStatus.Completed, phase == ImportPhase.Analyze, context.Audience);

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
    private ImportProgress Fail(Guid jobId, ImportPhase phase, ImportJobStatus status, string safeMessage, Exception? exception, DriverIdentity? driver = null, ProcessCall? call = null, ImportJob? job = null)
    {
        var utcNow = UtcNow();
        bool failedNow = false;
        var updated = store.UpdateStatus(jobId, current =>
        {
            failedNow = !ImportJobStateMachine.IsTerminal(current.State) && (driver is null || IsDrivenBy(current, phase, driver));

            return failedNow ? ImportJobStateMachine.Transition(current, ImportJobState.Failed, utcNow, safeMessage) : current;
        }) ?? status;

        // Exception messages of the database can quote contact data (truncated or duplicate values), so only the
        // exception types, SQL error numbers and the stack trace are logged.
        logger.LogError(
            failedEvent,
            "Contact import {JobId}: {Phase} failed after row {LastRow} (chunk {Chunk}); request of user {UserId}, import created by user {CreatedByUserId}. {Message} Error: {Error}. Stack trace: {StackTrace}",
            jobId,
            phase,
            FormatUser(call?.UserId),
            job is null ? "unknown" : FormatId(job.CreatedByUserId),
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
        if (failedNow && job is not null)
        {
            // A failed analysis wrote nothing; a failed import may have written the batches before the failure.
            call?.Reach(job, updated, ContactImportStatus.Failed, phase == ImportPhase.Analyze, null);
        }

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

    private static string FormatUser(int? userId) => userId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "unknown";

    private static string FormatId(int? id) => id is { } value ? value.ToString(CultureInfo.InvariantCulture) : "none";

    /// <summary>
    /// One <see cref="ProcessNext(Guid, ImportPhase, string?, bool, CancellationToken, int?)"/> call: who asked, and
    /// the outcome the job reached in it, if any, which is reported in an event when the call is done.
    /// </summary>
    private sealed class ProcessCall(int? userId)
    {
        public int? UserId { get; } = userId;

        public ReachedOutcome? Reached { get; private set; }

        public void Reach(ImportJob job, ImportJobStatus status, ContactImportStatus outcome, bool isDryRun, ImportAudience? audience) =>
            Reached = new ReachedOutcome(job, status, outcome, isDryRun, audience);
    }

    private sealed record ReachedOutcome(ImportJob Job, ImportJobStatus Status, ContactImportStatus Outcome, bool IsDryRun, ImportAudience? Audience);

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
