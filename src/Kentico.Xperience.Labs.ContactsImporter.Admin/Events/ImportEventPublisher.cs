using CMS.Base;

using Microsoft.Extensions.Logging;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Raises <see cref="AfterContactImportEvent"/>.
/// </summary>
internal interface IImportEventPublisher
{
    /// <summary>
    /// Calls the registered handlers and waits for them. Never throws: problems are logged.
    /// </summary>
    /// <param name="job">Configuration of the import.</param>
    /// <param name="status">Status of the import after it reached <paramref name="outcome"/>.</param>
    /// <param name="outcome">What the import reached.</param>
    /// <param name="isDryRun">Nothing was written to the database (analysis, or the import had not started to write).</param>
    /// <param name="userId">User whose request caused the event; <c>null</c> = not known.</param>
    /// <param name="audience">Recipient list and consent as the processing used them; <c>null</c> = read them now.</param>
    public void Publish(ImportJob job, ImportJobStatus status, ContactImportStatus outcome, bool isDryRun, int? userId = null, ImportAudience? audience = null);
}

/// <summary>
/// Calls the handlers registered with <c>AddEventHandler&lt;AfterContactImportEvent, THandler&gt;()</c>.
/// </summary>
/// <remarks>
/// The importer processes in synchronous page command calls (<see cref="IImportProcessor.ProcessNext"/>), the
/// handlers are asynchronous. The handlers run on a thread pool thread and the calling request waits for them: no
/// work is left behind when the request ends, and a handler cannot use (or block on) the database connection that
/// holds the job lock, because the caller raises the event after the lock is released. Every handler runs, also
/// after another one failed; exceptions are logged and never reach the import.
/// </remarks>
internal sealed class ImportEventPublisher(
    IEnumerable<IAsyncEventHandler<AfterContactImportEvent>> handlers,
    TimeProvider timeProvider,
    ILogger<ImportEventPublisher> logger,
    IImportAudienceResolver? audienceResolver = null) : IImportEventPublisher
{
    private static readonly EventId eventId = new(0, "CONTACT_IMPORT_EVENT");

    public void Publish(ImportJob job, ImportJobStatus status, ContactImportStatus outcome, bool isDryRun, int? userId = null, ImportAudience? audience = null)
    {
        try
        {
            // Resolve once: the handlers are singletons, but the enumerable may be lazy.
            var handlerList = handlers.ToList();
            if (handlerList.Count == 0)
            {
                return;
            }

            var importEvent = new AfterContactImportEvent(CreateData(job, status, outcome, isDryRun, userId, audience ?? ResolveAudience(job)));

            Task.Run(() => InvokeHandlers(handlerList, importEvent, job.JobId)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            logger.LogError(eventId, exception, "Contact import {JobId}: the contact import event ({Status}) could not be raised.", job.JobId, outcome);
        }
    }

    /// <summary>
    /// Builds the event data: settings and counts only, no contact data.
    /// </summary>
    internal ContactImportEventData CreateData(ImportJob job, ImportJobStatus status, ContactImportStatus outcome, bool isDryRun, int? userId, ImportAudience? audience)
    {
        var options = job.Options;
        bool hasConsent = options.Consent != ConsentMode.None;

        return new ContactImportEventData
        {
            JobID = job.JobId,
            UserID = userId ?? job.CreatedByUserId,
            CreatedByUserID = job.CreatedByUserId,
            Timestamp = timeProvider.GetLocalNow().DateTime,
            FileName = job.File.Name,
            FileSize = job.File.Size,
            IsDryRun = isDryRun,
            Status = outcome,
            Options = ContactImportOptions.From(options),
            Counts = ContactImportCounts.From(ImportAggregates.From(status.Aggregates)),
            RecipientListID = options.RecipientListId,
            ConsentID = hasConsent ? options.ConsentId : null,
            ConsentHash = hasConsent && audience?.ConsentId == options.ConsentId ? audience?.ConsentHash : null,
            AttestationUserID = job.Attestation?.UserId,
            AttestationTimeUtc = job.Attestation?.Utc,
            AttestationStatementVersion = job.Attestation?.StatementVersion,
            ImportSourceFieldName = options.SourceField,
        };
    }

    private async Task InvokeHandlers(List<IAsyncEventHandler<AfterContactImportEvent>> handlerList, AfterContactImportEvent importEvent, Guid jobId)
    {
        foreach (var handler in handlerList)
        {
            try
            {
                // Not cancelled with the request: an audit record of something that happened must not be lost because the browser left.
                await handler.HandleAsync(importEvent, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(eventId, exception, "Contact import {JobId}: the '{Handler}' handler of the contact import event failed.", jobId, handler.GetType().FullName);
            }
        }
    }

    private ImportAudience? ResolveAudience(ImportJob job)
    {
        if (audienceResolver is null || job.Options.Consent == ConsentMode.None)
        {
            return null;
        }

        try
        {
            return audienceResolver.Resolve(job.Options with { RecipientListId = null }).Audience;
        }
        catch (Exception exception)
        {
            logger.LogWarning(eventId, exception, "Contact import {JobId}: the consent of the import could not be read for the contact import event.", job.JobId);

            return null;
        }
    }
}
