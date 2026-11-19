# Usage Guide

> [!NOTE]
> This package replaces `Kentico.Xperience.Contacts.Importer`, which is deprecated (final release under the old ID: 1.0.0-prerelease-5). Version 1.0.0-prerelease-6 is the first release under the new ID. Replace that package reference with `Kentico.Xperience.Labs.ContactsImporter.Admin`; see [Installation](#installation).

The Contacts Importer lets marketers import contacts into Xperience by Kentico from CSV, TSV, NDJSON, JSON and Excel (`.xlsx`) files, also gzipped (`.gz`) or zipped (`.zip`). It matches rows to existing contacts, checks the whole file in a dry run before anything changes, and can add contacts to a recipient list and record consent agreements.

The file is read in the browser. Only the mapped columns are uploaded, and the server writes contacts in batches while the browser tab stays open.

## Contents

- [Setup](./Setup.md) (developers and administrators): installation, options, storage for web farm and SaaS, file cleanup and the optional scheduled task, permissions, import label field, index for a custom match field, request limits, event log.
- [Import contacts](./Importing-Contacts.md) (marketers): steps, formats, options, recipient lists, consent, reports, recent imports.
- [Audit contact imports](#audit-contact-imports) (developers): the `AfterContactImportEvent`.
- [Limitations and documented choices](#limitations-and-documented-choices)
- [Administration language](#administration-language)

## Installation

Add the package to your application:

```powershell
dotnet add package --prerelease Kentico.Xperience.Labs.ContactsImporter.Admin
```

## Registration

Register the services in your application's `Program.cs`:

```csharp
using Kentico.Xperience.Labs.ContactsImporter.Admin;

builder.Services.AddContactsImporter();
```

Then assign the permissions and, for a web farm or SaaS, set up the storage. See [Setup](./Setup.md).

## Administration

The package adds the **Contacts Importer (Labs)** application to the **Digital marketing** category, at `/admin/contacts-importer`. Its **Imports** page lists recent imports and starts new ones.

## Audit contact imports

The importer raises `AfterContactImportEvent` (namespace `Kentico.Xperience.Labs.ContactsImporter.Admin`) when:

- a check (dry run) finishes: `Status = Analyzed`, `IsDryRun = true`;
- an import completes: `Completed`;
- a check or import fails: `Failed`;
- an import is cancelled, deleted before it ended (cancelled before Run, **Delete** in the list), or cancelled by the cleanup because it was abandoned: `Cancelled`. A deleted import raises the event before its files are deleted; deleting an ended import raises no event.

`IsDryRun` is `true` when nothing was written to the database (a check, or an import that ended before it started to write).

The event data (`ContactImportEventData`) holds settings and counts only, never contact data:

| Property | Content |
| --- | --- |
| `JobID` | ID of the import. |
| `UserID` | User whose request finished the check or import or cancelled it; the creator when not known (for example an abandoned import). |
| `CreatedByUserID` | User who created the import. |
| `Timestamp` | When the event was raised, server local time. |
| `FileName`, `FileSize` | Name and size of the imported file. The file never reaches the server. |
| `IsDryRun`, `Status` | See above. `Status` is `ContactImportStatus`: `Analyzed`, `Completed`, `Failed`, `Cancelled`. |
| `Options` | `ContactImportOptions`: `NewContacts`, `ExistingContacts`, `EmptyValues`, `MultipleMatches`, `InvalidEmail`, `Unsubscribed`, `BouncedAddress`, `Consent`, `RevokedConsent`, `SourceStamp` (enums), `SourceValue`, `ReportIncludesKey`. |
| `Counts` | `ContactImportCounts`: `Rows`, `Created`, `Updated`, `Unchanged`, `ContactsUpdated`, the `Skipped*` counts, `DuplicateRows`, `MultipleMatches`, `ImportedWithoutEmail`, the `List*`, `Bounced*` and `Consent*` counts, and `FieldChanges` (per field: `FieldName`, `Changed`, `Cleared`). For a check these are expected values. |
| `RecipientListID` | Recipient list contacts are added to, or `null`. |
| `ConsentID`, `ConsentHash` | Consent agreements are recorded for, and the hash of its text (the version the agreements refer to); `null` when no consent is recorded. |
| `AttestationUserID`, `AttestationTimeUtc`, `AttestationStatementVersion` | Who confirmed the consent statement, when (UTC), and which statement version; `null` when the import needs no confirmation. |
| `ImportSourceFieldName` | Contact field that gets the import label, or `null`. The label is `Options.SourceValue`. |

### Sample handler

```csharp
using CMS.Base;

using Kentico.Xperience.Labs.ContactsImporter.Admin;

using Microsoft.Extensions.Logging;

public class ContactImportAuditHandler(ILogger<ContactImportAuditHandler> logger)
    : IAsyncEventHandler<AfterContactImportEvent>
{
    public Task HandleAsync(AfterContactImportEvent asyncEvent, CancellationToken cancellationToken)
    {
        var data = asyncEvent.Data;

        logger.LogWarning(
            new EventId(0, "CONTACT_IMPORT_AUDIT"),
            "Import {JobId} of {FileName} by user {UserId}: {Status} (dry run: {IsDryRun}). Created {Created}, updated {Updated}, list adds {ListAdded}, consents {ConsentRecorded}. Attested by {AttestationUserId} at {AttestationTimeUtc} (statement {Version}).",
            data.JobID, data.FileName, data.UserID, data.Status, data.IsDryRun,
            data.Counts.Created, data.Counts.ContactsUpdated, data.Counts.ListAdded, data.Counts.ConsentRecorded,
            data.AttestationUserID, data.AttestationTimeUtc, data.AttestationStatementVersion);

        return Task.CompletedTask;
    }
}
```

Register it in `Program.cs`:

```csharp
builder.Services.AddEventHandler<AfterContactImportEvent, ContactImportAuditHandler>();
```

Handlers are singletons. Notes:

- The request that caused the event waits for all handlers. Keep handlers short; send slow work to a queue.
- A handler exception is logged and does not change the import or stop other handlers.
- Delivery is best effort: if the application stops between storing the outcome and calling the handlers, the event is not raised again. In a web farm an outcome can, rarely, be reported twice.

The importer also writes its own event log entries; see [Event log](./Setup.md#event-log).

## Limitations and documented choices

The importer is built for speed on large files. Some behavior is intentional and differs from editing contacts one by one in the administration.

### Writes and events

- **Contacts are written with set-based SQL that bypasses the object API.** Handlers of per-object events (`ContactInfo.TYPEINFO.Events`, `ConsentAgreementInfo` events, object event handlers) do not run for imported contacts, list memberships or consent agreements. Type-level bulk insert events still run.
- **No automation or activity triggers.** Automation processes that start when a contact is created or updated do not start for imported contacts.
- **Contact groups are not recalculated.** Imported contacts are not added to contact groups with conditions. Recalculate the groups or schedule their recalculation ([docs](https://docs.kentico.com/documentation/business-users/digital-marketing/contact-groups#recalculate-contact-groups)). The Results step reminds marketers and links to Contact groups. Recipient lists are not affected.
- **Changes cannot be undone.** The check (dry run) and the confirmation are the safety net.

### Cache

- **Caches are cleared by type keys after a completed import** (`om.contact|all`, and for lists and consent `om.contactgroupmembercontact|all`, `emaillibrary.emailsubscriptionconfirmation|all`, `om.recipientlist|byid|<id>`, `cms.consentagreement|all`, plus the cached "is agreed" results of consents). Custom caches that depend only on per-contact keys (such as `om.contact|byid|<id>`) stay stale until they expire.
- **The full cache clear runs only when an import completes.** After a paused, cancelled, failed or abandoned import, cached contact data may be stale. Batches that created contacts already touched `om.contact|all`, but updates are not covered. Clear the cache in the **System** application (**Overview** → **Clear cache**).

### Data

- **Clean data is expected.** Rows with the same match value are applied in file order; the last non-empty value wins. The check can over-count new contacts when the file has duplicates.
- **Rows without a valid match value are always skipped** (missing, too long, or an invalid email when matching by email). They are not uploaded; their row numbers are in the skipped rows download of the Upload step. Fix the file and import again.
- **Reference fields take IDs only.** Country, state, contact status and owner fields need the ID of the object. A name, or an ID that does not exist, skips the row (`invalidValue`). A state is not checked against the country.
- **Dates have no time zone conversion.** The date and time in the file are stored as they are; a time zone offset is ignored.
- **Bounce records are never changed.** Xperience does not send to addresses with a hard bounce or soft bounces at the limit, also when the import adds them to a list.
- **No double opt-in email.** Contacts added to a recipient list are subscribed directly.
- **Very long numbers in NDJSON** (more than 15 digits, for example IDs) can lose digits, because JSON numbers are read as floating point. Put such values in quotes in the file.

### Running imports

- **The browser drives the import.** Keep the tab open. If it is closed, the computer sleeps, or the network is lost, the import pauses and continues from where it stopped when the import is opened again. An import paused with **Pause** is the exception: it stays paused until someone selects **Continue**. **Pause** waits for the batch in progress, so it can take a few seconds.
- **A cancelled import stops after the batch it is working on.** That batch is written and in the row report, but the results can undercount by up to one batch.
- **The import slows down when the database is busy.** It writes smaller batches when batches take longer than `TargetBatchSeconds` or the database times out, so the live site keeps working on small database plans; it speeds up again when the database is free. Settings: `AdaptiveBatchSize`, `TargetBatchSeconds`, `MinBatchSize` ([Options](./Setup.md#options)).
- **Do not import during deployments.** The administration is not available while the application is read-only; run the import again after the deployment. Runs are safe to repeat.
- **One run at a time.** An import whose run was confirmed and has not finished (running, paused, or confirmed and not started yet) blocks the run of every other import, whatever its last progress. A paused run blocks new runs until someone continues or cancels it. The Review step names it with a link to open it and disables **Run import** (it checks every 10 seconds); the server refuses the confirmation and the start of another run. Checks (analysis) are never blocked, and an import that has started can always continue. The check and the confirmation hold a database application lock (`sp_getapplock`), so two confirmations at the same moment, also on different instances of a web farm, cannot both pass.
- **The upload is limited** by `MaxFileSizeBytes` (5 GB by default), and the uploaded data of one import by twice that. Request body limits and timeouts of the hosting must fit the part size and processing time; see [Request size and timeouts](./Setup.md#request-size-and-timeouts).
- **Excel files are read in the browser.** The workbook's text table stays in browser memory while the sheet is read (about 130 MB for 1 million rows with unique emails); an `.xlsx` inside a `.zip` is unpacked into memory first. Excel's row limit (1,048,576) applies. For very large files, CSV uses less memory.

### Storage and files

- **Shared storage is needed for a web farm or SaaS.** All instances must see the same storage folder, and on Azure App Service the folder should be registered for storage mapping to survive deployments. See [Storage](./Setup.md#storage). Real web farm and SaaS runs have not been tested yet.
- **Deleted imports leave only event log entries and audit events.** Deleting an import (cancel before Run, **Delete** on Results or in the list) removes its configuration, status, uploaded data and reports; keep your own record through the audit event if you need one. An import that is being checked or run right now by another tab or user cannot be deleted.
- **The Import contacts permission shows existing contact data.** A check (which changes nothing) tells which match values belong to existing contacts, and **Examples of changes** shows current field values. The Contacts application's permissions are not checked. See [Permissions](./Setup.md#permissions).
- **Uploaded data and reports with match values are personal data** on the server until they are deleted (see [File cleanup](./Setup.md#file-cleanup)). Cleanup runs when the importer is opened, and on a schedule if you enable the optional scheduled task. Storage with soft delete or backups keeps deleted files for its own period.

## Administration language

The importer shows its texts in the admin UI language of the user and formats numbers, sizes and dates for that language. The package includes English (`en-US`). To add a language, translate both resource files of the package, `ContactsImporterClientResources` (texts of the browser) and `ContactsImporterResources` (messages of the server), and register them for the language as described in [Translate the admin UI](https://docs.kentico.com/documentation/developers-and-admins/customization/admin-ui-localization/translate-the-admin-ui). `dotnet run -- --kxp-export-localization-resources` exports their keys (`contactsimporter.*`) under `en-US/custom/`.

The downloadable row reports keep their column names (`row`, `outcome`, `reason`, ...) and codes in every language, so files can be processed the same way whatever language the user works in. The list of skipped rows the browser creates during the upload has the same fixed column names; its reasons are texts in the user's language.

The consent statement of the Run confirmation has a version (`1`). Translations of the statement are confirmed under the same version; the language the user saw is not stored.
