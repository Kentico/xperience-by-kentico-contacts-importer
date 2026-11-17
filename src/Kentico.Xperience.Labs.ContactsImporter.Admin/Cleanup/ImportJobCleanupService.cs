using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Deletes import files that hold personal data as soon as they are not needed, and old jobs after their
/// retention (PLAN §11 "File cleanup"). There is no background worker: callers run the cleanup when a job
/// finishes and the sweep when the importer application loads or a command runs.
/// </summary>
internal interface IImportJobCleanupService
{
    /// <summary>
    /// Deletes the uploaded chunks of a job. Call when the job reaches Completed, Failed or Cancelled.
    /// </summary>
    public void DeleteChunks(Guid jobId);

    /// <summary>
    /// "Delete import files now": deletes the uploaded chunks and reports of a job and, when the job is not
    /// finished, cancels it (which raises <see cref="AfterContactImportEvent"/>). The job stays in the recent imports
    /// list. Returns <c>false</c> when the job does not exist.
    /// </summary>
    /// <param name="jobId">Job whose files are deleted.</param>
    /// <param name="userId">User who asked for it, for the event; <c>null</c> = not known.</param>
    public bool DeleteImportFiles(Guid jobId, int? userId = null);

    /// <summary>
    /// Runs <see cref="Sweep"/> unless it ran in this application instance within the last
    /// <see cref="ImportJobCleanupService.SweepInterval"/>. Cheap to call from every command.
    /// </summary>
    public ImportSweepResult? SweepIfDue();

    /// <summary>
    /// Checks every job folder and applies the retention rules. Problems with one job never stop the sweep.
    /// </summary>
    public ImportSweepResult Sweep();
}

/// <param name="JobsChecked">Job folders looked at.</param>
/// <param name="ChunksDeleted">Jobs whose uploaded chunks were deleted.</param>
/// <param name="ReportsDeleted">Jobs whose reports were deleted.</param>
/// <param name="JobsAbandoned">Unfinished jobs without activity that were cancelled and cleaned.</param>
/// <param name="JobsDeleted">Job folders deleted entirely.</param>
/// <param name="Errors">Jobs that could not be processed.</param>
internal sealed record ImportSweepResult(int JobsChecked, int ChunksDeleted, int ReportsDeleted, int JobsAbandoned, int JobsDeleted, int Errors);

internal sealed class ImportJobCleanupService(
    IImportJobStore store,
    IOptions<ContactsImporterOptions> options,
    TimeProvider timeProvider,
    ILogger<ImportJobCleanupService> logger,
    IImportEventPublisher? events = null) : IImportJobCleanupService
{
    /// <summary>Shortest time between two lazy sweeps in one application instance.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    internal static ImportMessage AbandonedError => ImportMessages.Abandoned;

    private readonly Lock sweepLock = new();
    private DateTime? lastSweepUtc;

    public void DeleteChunks(Guid jobId) => store.DeleteChunks(jobId);

    public bool DeleteImportFiles(Guid jobId, int? userId = null)
    {
        if (!store.JobExists(jobId))
        {
            return false;
        }

        var utcNow = UtcNow();
        bool cancelled = false;
        var updated = store.UpdateStatus(jobId, status =>
        {
            cancelled = !ImportJobStateMachine.IsTerminal(status.State);

            return cancelled ? ImportJobStateMachine.Transition(status, ImportJobState.Cancelled, utcNow) : status;
        });
        store.DeleteChunks(jobId);
        store.DeleteReports(jobId);
        if (cancelled && updated is not null)
        {
            PublishCancelled(jobId, updated, userId);
        }

        return true;
    }

    public ImportSweepResult? SweepIfDue()
    {
        var utcNow = UtcNow();
        lock (sweepLock)
        {
            if (lastSweepUtc is { } last && utcNow - last < SweepInterval)
            {
                return null;
            }

            lastSweepUtc = utcNow;
        }

        return Sweep();
    }

    public ImportSweepResult Sweep()
    {
        var utcNow = UtcNow();
        var counts = new SweepCounts();
        foreach (var jobId in store.ListJobs())
        {
            counts.JobsChecked++;
            try
            {
                SweepJob(jobId, utcNow, counts);
            }
            catch (Exception ex)
            {
                // One unreadable or locked job must not stop the cleanup of the others.
                counts.Errors++;
                logger.LogWarning(ex, "Contacts Importer cleanup failed for import job {JobId}.", jobId);
            }
        }

        return new ImportSweepResult(counts.JobsChecked, counts.ChunksDeleted, counts.ReportsDeleted, counts.JobsAbandoned, counts.JobsDeleted, counts.Errors);
    }

    private void SweepJob(Guid jobId, DateTime utcNow, SweepCounts counts)
    {
        var settings = options.Value;
        var abandonedAfter = TimeSpan.FromDays(settings.AbandonedJobRetentionDays);

        ImportJobStatus? status;
        try
        {
            status = store.ReadStatus(jobId);
        }
        catch (InvalidDataException)
        {
            status = null;
        }

        if (status is null)
        {
            // No readable status: nothing can continue this job. Delete it once it is old enough, so a job
            // that is just being created is left alone, or right away when its age cannot be told.
            var createdUtc = TryReadJob(jobId)?.CreatedUtc ?? store.GetJobFolderLastWriteTimeUtc(jobId);
            if (createdUtc is null || utcNow - createdUtc > abandonedAfter)
            {
                store.DeleteJob(jobId);
                counts.JobsDeleted++;
            }

            return;
        }

        if (!ImportJobStateMachine.IsTerminal(status.State))
        {
            SweepUnfinishedJob(jobId, status, utcNow, abandonedAfter, counts);
            return;
        }

        var finishedUtc = status.FinishedUtc ?? status.UpdatedUtc;
        var age = utcNow - finishedUtc;
        if (age > TimeSpan.FromDays(settings.StatusRetentionDays))
        {
            store.DeleteJob(jobId);
            counts.JobsDeleted++;
            return;
        }

        if (store.HasChunks(jobId))
        {
            store.DeleteChunks(jobId);
            counts.ChunksDeleted++;
        }

        if (age > GetReportRetention(jobId, settings) && store.ListReports(jobId).Count > 0)
        {
            store.DeleteReports(jobId);
            counts.ReportsDeleted++;
        }
    }

    private void SweepUnfinishedJob(Guid jobId, ImportJobStatus status, DateTime utcNow, TimeSpan abandonedAfter, SweepCounts counts)
    {
        var lastActivityUtc = status.HeartbeatUtc is { } heartbeat && heartbeat > status.UpdatedUtc ? heartbeat : status.UpdatedUtc;
        if (utcNow - lastActivityUtc <= abandonedAfter)
        {
            return;
        }

        bool cancelled = false;
        var updated = store.UpdateStatus(jobId, current =>
        {
            cancelled = !ImportJobStateMachine.IsTerminal(current.State);

            return cancelled
                ? ImportJobStateMachine.Transition(current, ImportJobState.Cancelled, utcNow).WithError(AbandonedError)
                : current;
        });
        store.DeleteChunks(jobId);
        store.DeleteReports(jobId);
        counts.JobsAbandoned++;
        if (cancelled && updated is not null)
        {
            PublishCancelled(jobId, updated, null);
        }
    }

    /// <summary>
    /// Raises the event of a job this service cancelled. Nothing was written when the import never started.
    /// </summary>
    private void PublishCancelled(Guid jobId, ImportJobStatus status, int? userId)
    {
        if (events is not null && TryReadJob(jobId) is { } job)
        {
            events.Publish(job, status, ContactImportStatus.Cancelled, status.Checkpoint.Phase != ImportPhase.Execute, userId);
        }
    }

    /// <summary>
    /// Reports with the key column hold personal data and get the short retention. When the job configuration
    /// cannot be read, the short retention applies to be safe.
    /// </summary>
    private TimeSpan GetReportRetention(Guid jobId, ContactsImporterOptions settings)
    {
        bool includesKey = TryReadJob(jobId)?.Options.ReportIncludesKey ?? true;

        return includesKey ? TimeSpan.FromHours(settings.ReportWithKeysRetentionHours) : TimeSpan.FromDays(settings.ReportRetentionDays);
    }

    private ImportJob? TryReadJob(Guid jobId)
    {
        try
        {
            return store.ReadJob(jobId);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed class SweepCounts
    {
        public int JobsChecked { get; set; }

        public int ChunksDeleted { get; set; }

        public int ReportsDeleted { get; set; }

        public int JobsAbandoned { get; set; }

        public int JobsDeleted { get; set; }

        public int Errors { get; set; }
    }
}
