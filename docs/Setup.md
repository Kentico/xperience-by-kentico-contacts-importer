# Setup (developers and administrators)

How to install and configure the Contacts Importer. For marketers, see [Import contacts](./Importing-Contacts.md). Back to the [Usage Guide](./Usage-Guide.md).

## Install the package

```powershell
dotnet add package --prerelease Kentico.Xperience.Labs.ContactsImporter.Admin
```

Register the services in `Program.cs`:

```csharp
using Kentico.Xperience.Labs.ContactsImporter.Admin;

builder.Services.AddContactsImporter();
```

The package adds the **Contacts Importer (Labs)** application to the **Digital marketing** category of the administration (`/admin/contacts-importer`). It installs no database objects: import jobs are files in a storage folder (see [Storage](#storage)).

## Options

Options are read from the `ContactsImporter` section of the configuration (for example `appsettings.json`). The optional delegate of `AddContactsImporter` runs after that and can override them:

```csharp
builder.Services.AddContactsImporter(options =>
{
    options.BatchSize = 500;
});
```

```json
{
  "ContactsImporter": {
    "StoragePath": "~/App_Data/contacts-importer",
    "MaxFileSizeBytes": 5368709120,
    "ChunkSizeBytes": 4194304
  }
}
```

| Option | Default | What it does |
| --- | --- | --- |
| `StoragePath` | `~/App_Data/contacts-importer` | Root folder of the import job files (settings, status, uploaded data, row reports). Application-relative (`~/...`) or absolute. Accessed through CMS.IO. Must be shared by all instances (see [Storage](#storage)). |
| `MaxFileSizeBytes` | 5 GB | Largest source file a marketer can import. The server also limits the uploaded (compressed) data of one import to twice this size. |
| `ChunkSizeBytes` | 4 MB | Target size of one uploaded part (gzipped, before base64). The server accepts parts up to twice this size. See [Request size and timeouts](#request-size-and-timeouts). |
| `MaxChunkUncompressedBytes` | 128 MB | Parts that unpack to more than this are refused. |
| `BatchSize` | 1000 | Rows written to the database in one batch (one transaction). With `AdaptiveBatchSize`, the largest import batch. The check (dry run) always uses this size. |
| `AdaptiveBatchSize` | `true` | The import adapts its batch size to the database: batches slower than `TargetBatchSeconds` get smaller, clearly faster ones larger (up to ×1.5 per batch), between `MinBatchSize` and `BatchSize`. A database timeout, deadlock or resource limit halves the next batch. Protects the live site on small database plans. `false` = every batch has `BatchSize` rows. |
| `TargetBatchSeconds` | 2 | Seconds of database work (lookups and writes) one import batch aims for. Decimals allowed. |
| `MinBatchSize` | 50 | Smallest import batch. A value above `BatchSize` means a fixed batch size. |
| `ProcessingTimeBudgetSeconds` | 15 | Seconds one processing request works before it returns progress to the browser. Keep it well below the request timeouts of your hosting (CDN, reverse proxy). |
| `AnalysisValidHours` | 24 | Hours a finished check (dry run) stays valid. An import run later is checked again. |
| `HeartbeatStaleSeconds` | 60 | Seconds without progress after which a running check or import counts as paused, and another browser tab or user can continue it. |
| `ReportRetentionDays` | 7 | Days a row report without personal data is kept after the import ends. |
| `ReportWithKeysRetentionHours` | 24 | Hours a row report that includes the match value (personal data) is kept after the import ends. |
| `AbandonedJobRetentionDays` | 7 | Days without activity after which an unfinished import is cancelled and its files deleted. |
| `StatusRetentionDays` | 90 | Days the status of an ended import (counts only, no contact data) stays in the list of recent imports. Then its folder is deleted. |

Every number must be greater than 0 (`TargetBatchSeconds` also finite) and `StoragePath` must not be empty. Invalid options throw an error that names the option when the importer first reads them (not at application startup).

## Storage

The browser reads the file; only the mapped columns are uploaded, as gzipped parts. These parts and the row reports hold personal data. They are stored under `StoragePath` through [CMS.IO](https://docs.kentico.com/documentation/developers-and-admins/api/files-api-and-cms-io) and are deleted as described in [File cleanup](#file-cleanup).

### Web farm and SaaS

Each request of an import can reach a different application instance. All instances must see the same storage folder.

- **Single instance**: nothing to do.
- **Several instances with separate disks** (on-premises web farm, containers without a shared volume): map `StoragePath` to shared storage with a [storage path mapping](https://docs.kentico.com/documentation/developers-and-admins/api/files-api-and-cms-io/file-system-providers/storage-path-mapping).
- **Azure App Service (SaaS and private cloud)**: the instances share one local file system, so the default folder works across instances. But the default `~/App_Data/contacts-importer` folder is not a registered path, so the cloud storage mapping does not include it and its files can be lost on redeployment or slot swap. To keep them, register the path before the mapping call:

  ```csharp
  using CMS.IO;

  builder.Services.AddStoragePathRegistration("~/App_Data/contacts-importer", PathType.SharedPersistent);
  builder.Services.AddXperienceCloudStoragePathMapping(); // SaaS; AddAppServiceStoragePathMapping() for private cloud
  ```

  `SharedPersistent` keeps the list of recent imports and the reports through deployments. Use the same path as `StoragePath`. The package does not register the path itself.

Running the importer in a real web farm or on SaaS has not been tested yet.

### Build and publish

The Web SDK treats `*.json` files in the project folder as content. The package includes a `.targets` file that removes `App_Data\contacts-importer\**` from `Content` and `None` items, so job files created while developing are not copied to the build output or published.

- It covers only the default path. If `StoragePath` points to another folder inside the project folder, add the same two `Remove` items for that folder to your project file.
- To turn it off, set the MSBuild property `ContactsImporterExcludeStorageFromProject` to `false`.

### File cleanup

There is no background worker. Files are deleted when an import ends, and by a cleanup that runs when someone opens the list of imports or an import page (at most once every 10 minutes per application instance). In an application where the importer is rarely opened, old files stay until someone opens it, unless you enable the optional scheduled task.

| Files | Deleted when |
| --- | --- |
| Whole import (configuration, status, uploaded parts, reports) | The marketer cancels it before Run, starts a new import instead, or selects **Delete** in the list of imports. |
| Uploaded parts, and the examples of changed values that Review shows (match values and contact values) | The import completes, fails or is cancelled. A new check replaces the examples. |
| Row report without match values | `ReportRetentionDays` after the import ended. |
| Row report with match values | `ReportWithKeysRetentionHours` after the import ended. |
| Unfinished import without activity | After `AbandonedJobRetentionDays`: cancelled, uploaded parts (with the examples) and reports deleted. |
| Import status (counts only) | `StatusRetentionDays` after the import ended. |

If your storage keeps deleted files (for example Azure Blob soft delete or backups), they stay there for that period.

#### Optional scheduled task

The package registers a [scheduled task](https://docs.kentico.com/documentation/developers-and-admins/customization/scheduled-tasks) that runs the same cleanup with the same retention options. It does nothing until you configure it:

1. Open the **Scheduled tasks** application.
1. Select **New scheduled task configuration**.
1. Enter a name (for example *Contacts Importer file cleanup*) and set **Task implementation** to `Kentico.Xperience.Labs.ContactsImporter.FileCleanup`.
1. Select **Enabled** and set the **Task schedule**. Daily is a good default. A file is deleted at the first run after its limit, so with a daily schedule a report with match values can stay up to a day past `ReportWithKeysRetentionHours`; run it hourly to keep it closer.
1. **Save**.

Each run checks every import and applies the rules in the table above. The **Last result** column shows the counts (imports checked, uploaded parts and reports deleted, abandoned imports cancelled, imports deleted, errors). An import that cannot be processed is counted as an error and logged to the event log; the other imports are still cleaned, and the next run tries it again. The cleanup on opening the importer keeps running as before.

## Permissions

The application declares these permissions. Assign them to roles in the [Role management](https://docs.kentico.com/documentation/developers-and-admins/configuration/users/role-management) application (permission set for **Contacts Importer (Labs)**):

| Permission | Allows |
| --- | --- |
| **View** (built-in) | Open the application and see the list of imports. |
| **Import contacts** (`ContactsImporter.Import`) | Create, upload, check, run, continue, cancel and delete imports, and download reports. A user with this permission can work with every import, not only their own (needed to continue an import another user started). |
| **Confirm consent for imported contacts** (`ContactsImporter.ConfirmConsent`) | Run imports that add contacts to a recipient list, subscribe them again, or record consent agreements. The server checks this when the import is confirmed. |

A typical setup: marketers who import get View + Import contacts. Only people who may confirm that contacts gave consent also get Confirm consent for imported contacts.

## Contact field for the import label

Marketers can write a label (for example `import-2026-11-17-newsletter.csv`) into a contact field to find the contacts of an import later, for example in a contact group condition. The field must be a text field that the import does not map to a file column; its current value is replaced.

Add a dedicated field for this:

1. Open **Modules** → **Contact management** → **Classes** → **Contact management - Contact** → **Database columns**.
2. Add a field, for example `AcmeContactImportSource`, data type **Text**, size 200, not required.
3. To use it in contact group conditions, add it to the **Contact edit** UI form.

See [Add custom fields to contacts](https://docs.kentico.com/documentation/developers-and-admins/digital-marketing-setup/contact-configuration#add-custom-fields-to-contacts).

## Index for a custom match field

Contacts are matched by email by default; `ContactEmail` has a database index. Marketers can match by another field (sized text, whole number or GUID). Custom fields added in **Modules** have no index, so matching by one is slow on large contact tables. The Map step warns about it.

Add an index for the field you match by, with a prefix in its name so it does not clash with system objects:

```sql
CREATE NONCLUSTERED INDEX [IX_OM_Contact_AcmeContactExternalID]
    ON [dbo].[OM_Contact] ([AcmeContactExternalID]);
```

Xperience does not manage this index. Create it in every environment with your usual database change process.

## Request size and timeouts

Uploaded parts are sent base64 encoded in admin page command requests, so a request is about 4/3 of the part size. The browser aims for `ChunkSizeBytes` per part; the server accepts parts up to twice that. Keep `2 × ChunkSizeBytes × 4/3` (about 11 MB with the default) below the request body limits on the way to the application: Kestrel `MaxRequestBodySize` (default 30,000,000 bytes), IIS `maxAllowedContentLength`, and any reverse proxy or CDN.

Processing requests run for about `ProcessingTimeBudgetSeconds` plus one batch. With `AdaptiveBatchSize`, import batches aim for `TargetBatchSeconds` of database time; a single batch on an overloaded database can still take longer. Keep that well below the request timeout of the hosting (for example a CDN timeout).

## Event log

Imports write to the [event log](https://docs.kentico.com/documentation/developers-and-admins/development/logging) through `ILogger`. Event codes: `CONTACT_IMPORT_START`, `CONTACT_IMPORT_FINISH`, `CONTACT_IMPORT_CANCEL`, `CONTACT_IMPORT_DELETE`, `CONTACT_IMPORT_FAILURE`, `CONTACT_IMPORT_ATTESTATION`, `CONTACT_IMPORT_REJECTED`, `CONTACT_IMPORT_RETRY`, `CONTACT_IMPORT_EVENT`. The logger category starts with `Kentico`, so `Information` entries reach the Xperience event log with the default configuration. Entries hold no contact data.

For an audit trail in your own code, see [Audit contact imports](./Usage-Guide.md#audit-contact-imports).
