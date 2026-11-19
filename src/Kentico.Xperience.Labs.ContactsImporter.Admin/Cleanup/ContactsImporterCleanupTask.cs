using CMS.Scheduler;

using Kentico.Xperience.Labs.ContactsImporter.Admin;

using Microsoft.Extensions.Logging;

[assembly: RegisterScheduledTask(ContactsImporterCleanupTask.Identifier, typeof(ContactsImporterCleanupTask))]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Optional scheduled task that runs the import file cleanup (<see cref="IImportJobCleanupService.Sweep"/>), for
/// applications where the importer is rarely opened. It runs only when an administrator creates a task configuration
/// for it in the Scheduled tasks application. The system creates it with <c>ActivatorUtilities</c>, so services come
/// from constructor injection.
/// </summary>
internal sealed class ContactsImporterCleanupTask(
    IImportJobCleanupService cleanupService,
    ILogger<ContactsImporterCleanupTask> logger) : IScheduledTask
{
    /// <summary>Task implementation identifier shown in the Scheduled tasks application.</summary>
    public const string Identifier = "Kentico.Xperience.Labs.ContactsImporter.FileCleanup";

    public Task<ScheduledTaskExecutionResult> Execute(ScheduledTaskConfigurationInfo task, CancellationToken cancellationToken) =>
        Task.FromResult(new ScheduledTaskExecutionResult(Run(cancellationToken)));

    /// <summary>
    /// Runs the sweep and returns the message for the task's Last result column. Never throws: a failure is logged
    /// and returned as the message, so the next scheduled run tries again.
    /// </summary>
    internal string Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return "Cancelled before the cleanup started.";
        }

        try
        {
            var result = cleanupService.Sweep();
            if (result.Errors > 0)
            {
                logger.LogWarning("Contacts Importer cleanup task could not process {Errors} of {JobsChecked} import jobs.", result.Errors, result.JobsChecked);
            }

            return FormatResult(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Contacts Importer cleanup task failed.");

            return $"Error: {ex.Message}";
        }
    }

    internal static string FormatResult(ImportSweepResult result) =>
        $"Checked {result.JobsChecked} imports: uploaded parts deleted for {result.ChunksDeleted}, reports deleted for {result.ReportsDeleted}, " +
        $"abandoned imports cancelled {result.JobsAbandoned}, imports deleted {result.JobsDeleted}, errors {result.Errors}.";
}
