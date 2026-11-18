# Import contacts (marketers)

How to import contacts with the **Contacts Importer (Labs)** application. For installation and configuration, see [Setup](./Setup.md). Back to the [Usage Guide](./Usage-Guide.md).

## Before you start

- You need the **View** and **Import contacts** permissions of the application. To add contacts to a recipient list or record consent, you also need **Confirm consent for imported contacts**. See [Permissions](./Setup.md#permissions).
- Clean data works best: one row per contact. See [Duplicate rows](#duplicate-rows).
- Large imports take time. Run them outside busy hours, keep the browser tab open, and do not import while the application is being deployed.
- To add contacts to a recipient list, create the list first (**Recipient lists** application). To record consent, create a dedicated consent first (**Data protection** application). See [Consent agreements](#consent-agreements).

## Steps

Open **Contacts Importer (Labs)** and select **New import**. An import goes through these steps:

1. **File** – choose the file and check how it is read.
2. **Map** – choose which columns to import, the contact field of each, and the field that matches existing contacts. **Save and upload** starts the upload.
3. **Upload** – the browser reads the whole file and uploads the mapped columns. The upload runs in a bar next to the Options step, so you can choose the options meanwhile.
4. **Options** – choose what happens to new and existing contacts, the recipient list, consent and the import label.
5. **Review** – the whole file is checked against existing contacts (a dry run). Nothing is changed.
6. **Run** – the import writes the contacts.
7. **Results** – expected and imported counts, and row reports.

### 1. File

The file stays on your computer. The browser reads it; only the columns you map are uploaded.

Supported formats:

| Format | Notes |
| --- | --- |
| CSV / TSV (`.csv`, `.tsv`, `.tab`, `.txt`) | Delimiter (comma, semicolon, tab, pipe) and encoding are detected; you can change them. Encodings: UTF-8, UTF-16, Windows-1252, Windows-1250, ISO-8859-2. The first row holds the column names, or choose **Data (columns are numbered)**. Values can be quoted with `"`. |
| NDJSON (`.ndjson`, `.jsonl`) | One JSON object per line. Same encodings as CSV. |
| JSON (`.json`) | One array of objects: `[ {…}, {…} ]`. |
| Excel (`.xlsx`) | The first sheet, or the sheet you choose when the workbook has several. The first row holds the column names. Other spreadsheet formats (`.xls`, `.xlsb`, `.xlsm`, `.ods`, `.numbers`) are not supported: save as `.xlsx` or CSV. |
| Gzip (`.gz`) | A gzipped CSV, TSV, NDJSON or JSON file, for example `contacts.csv.gz`. Not for Excel files. |
| Zip (`.zip`) | A zip with exactly one file in one of the formats above. |

For JSON and NDJSON, nested objects become dotted column names (`address.city`); arrays are kept as JSON text. Numbers and `true`/`false` are read as text; `null` is an empty value.

For Excel, cells formatted as dates are read as dates (`2026-01-31`, or `2026-01-31T14:30:00` with a time). Other numbers are read as Excel stores them.

The step shows the first rows. Rows that cannot be read are counted and skipped.

Excel files have at most 1,048,576 rows. The browser keeps the workbook's text table in memory while it reads the sheet (about 130 MB for a 1 million row file where every row has a different email address). A `.xlsx` inside a `.zip` is unpacked into browser memory first. For very large files, CSV uses less memory.

The largest file allowed is set by the administrator (5 GB by default).

### 2. Map

- For each column, choose a contact field, or **Do not import**. Columns that are not imported never leave your browser.
- **Match existing contacts by**: the field that tells whether a row is an existing contact. Email by default. Any mapped text (with a size), whole number or GUID field can be used. Rows without a value in it are always skipped.
  - Email addresses are trimmed and lowercased, and must be valid. A row with an invalid email address in the match field is skipped.
  - Other match fields are trimmed; letter case is kept. Matching by a field other than email can be slow on large contact tables; ask a developer to [add an index](./Setup.md#index-for-a-custom-match-field).
- **Date format in the file**: year first (`2026-01-31`), day first (`31/01/2026`) or month first (`01/31/2026`). Year-first dates are always accepted. Rows with a date that cannot be read are skipped. Dates and times are stored as written in the file: there is no time zone conversion.
- Yes/no fields accept `1`, `true`, `yes` and `0`, `false`, `no`.
- Fields that point to another object (country, state, contact status, owner, …) take the object's ID, not its name. A row with an ID that does not exist is skipped.
- **Consent columns (optional)**: a **Consent given** column (yes = `y`, `yes`, `true`, `1`, `x`) and a **Consent date** column. See [Consent agreements](#consent-agreements).

Once the upload has started, the File, Map and Upload steps are locked. To use another file or another mapping, start a new import.

### 3. Upload

The browser reads the whole file, keeps the mapped columns, and uploads them in compressed parts. You see rows read, parts uploaded, speed and time left.

- **Skipped rows**: rows that cannot be read, rows without a match value, with an invalid email in the match field, a date that cannot be read, or a value that is too long. They are not uploaded. Select **Download skipped rows** for a CSV with the row number and reason (`row,reason,field,detail`). The list is built in your browser and is not sent to the server; it holds up to 100,000 rows.
- **Likely duplicates**: rows whose match value probably appeared earlier in the file (an estimate; about 1 in 100 can be counted by mistake). Select **Download likely duplicates** for the list of their row numbers, to fix the file. See [Duplicate rows](#duplicate-rows).

Keep the tab open. If you close it, the upload pauses. To continue, open the import and select the same file again: parts already uploaded are not sent again. A different file cannot continue the upload: select the file the import was started with, or cancel the import and start a new one.

**Save and check the file** on the Options step is available when the upload is complete.

### 4. Options

| Option | Choices (default first) |
| --- | --- |
| Rows that match no contact | Create a new contact / Skip the row |
| Rows that match an existing contact | Update the contact with the values from the file / Skip the row (never change existing contacts) |
| Empty values in the file | Keep the value the contact already has / Clear the value of the contact |
| Rows that match several contacts | Update the most recently changed contact / Update all matching contacts / Skip the row |
| Rows with an email address that is not valid (only when email is mapped but is not the match field) | Skip the row / Import the row without the email address |

When a contact is updated, every non-empty value in a mapped column replaces the contact's value. Empty values follow the **Empty values** option. Unmapped fields are not changed.

**Mark imported contacts**: write a label into a contact field, so you can find the contacts of this import later, for example in a contact group condition. Choose a text field that is not mapped (a developer can [add one](./Setup.md#contact-field-for-the-import-label)), enter the label (default `import-<date>-<file name>`), and choose whether only created contacts or also updated contacts get it. The field's current value is replaced.

**Row report**: by default the reports hold row numbers only. You can include the match value; the reports then hold personal data and are deleted sooner.

**Recipient list** and **Consent agreements**: see [Recipient lists](#recipient-lists) and [Consent agreements](#consent-agreements).

Select **Save and check the file** to start the review.

### 5. Review

Every row is compared with the existing contacts. Nothing is changed. You see:

- New contacts, contacts to change, rows with no changes, skipped rows.
- All counts, including recipient list, bounce and consent counts.
- Changes by contact field, and how many values would be cleared.
- **Examples of changes**: for each field, up to three contacts from the first rows of the file, with the row number, the match value (for example the email), the current value and the new value. Values that would be cleared come first, marked as a warning. These are examples, not all changes; the expected results report lists every row the import updates. The examples are personal data: they are stored with the uploaded data and deleted with it. Anyone who can import sees them, also without access to the Contacts application.
- **Download expected results**: what the import would do with each row and why. Use it to find rows that would be skipped and fix the file before you run. After the run, Results has the actual report (see [Row reports](#row-reports)).

The check is valid for 24 hours (the administrator can change this). If you run the import later, the file is checked again.

**Cancel import** on every step before Run (File, Map, Options, Review) deletes the whole import: the uploaded data and the check results. No contacts have been changed. You return to the list of imports. Once the upload is complete, **Start a new import** on Options and Review opens a new import and keeps this one in the list.

Select **Run import**. The confirmation shows what the import will do. Changes to contacts cannot be undone. If the import changes more than 1,000 existing contacts, you type `IMPORT` to confirm. If the import adds contacts to a recipient list or records consent, the confirmation includes a consent statement; see [Confirmation of consent](#confirmation-of-consent).

Only one import can run at a time. An import counts from the moment its run is confirmed until it is finished or cancelled, also while it is paused (tab closed). If another import has not finished, Review shows its file, who started it, when, how many rows are done, and whether it runs, is paused or has not started yet, and **Run import** is disabled. **Open the other import** opens it, so you (or its owner) can continue or cancel it. The page checks again every few seconds and enables **Run import** when the other import is finished or cancelled. Checking a file is never blocked.

If another import finished after your check, Review says so: that import may have changed the same contacts, so what this import does may differ from the counts shown. To get exact counts, change an option and save it, which checks the file again.

### 6. Run

The browser drives the import: it asks the server to process the next part again and again. You see progress, speed, time left and running counts.

- **Keep the tab open.** If you close it, or the computer sleeps, the import pauses. Open the import again and select **Continue**. Rows already processed are kept.
- **Pause** stops after the current step. The import shows as paused at once, also in the list of imports and for other users. **Continue** goes on.
- If the import runs in another tab or for another user, you see that. Select **Continue here** to take it over: the page waits until the import has made no progress there for 60 seconds (or was paused there with **Pause**), then continues here. **Pause** stops waiting. Reloading the page does not count as another tab.
- Short network or server problems are retried automatically.
- If another import was started and has not finished (for example a run confirmed by two users at almost the same time), the import waits before it starts. The page names the other import with a button to open it, and enables **Continue** when that import is finished or cancelled. An import that has started always continues, also after a pause.
- **Cancel import** stops after the rows being processed. Contacts created or changed so far stay as they are. The uploaded data is deleted. The import stays in the list with its results; **Delete** on Results or in the list removes it.

### 7. Results

- **Expected and imported**: the counts of the check next to the counts of the import.
- **Row reports**: **Download import report** (what the import did with each row) and **Download expected results (check)** (what the check expected).
- **Contact groups are not recalculated**: imported contacts are not added to contact groups with conditions automatically. Open the groups that should include them and recalculate them, or let their scheduled recalculation run. See [Recalculate contact groups](https://docs.kentico.com/documentation/business-users/digital-marketing/contact-groups#recalculate-contact-groups). Recipient lists are not affected.
- **Delete** removes the import, as in the list of imports (see [Recent imports](#recent-imports)).
- If the import was cancelled or failed, cached contact data was not refreshed. An administrator can clear the cache in the **System** application (**Overview** → **Clear cache**). See [Clear the cache](https://docs.kentico.com/documentation/developers-and-admins/development/caching#clear-the-cache).

## Recipient lists

Choose a recipient list to add the contacts of the import to it.

- Contacts that are not on the list are added as **subscribed**. They do **not** get a confirmation (double opt-in) email.
- Contacts without an email address are not added.
- This applies to every contact a row matches or creates, also when existing contacts are not updated.
- **Contacts who unsubscribed from this list**: keep them unsubscribed (default), or subscribe them again. Subscribe them again only if each of them agreed again after unsubscribing, and you have a record of it.
- **Email addresses that Xperience stopped sending to because emails bounced** (a hard bounce, or soft bounces at the limit): do not add the contacts to the list (default), or add them. Xperience does not send to these addresses either way. The importer never changes bounce records. The review and results count hard bounces, soft bounces at the limit, and soft bounces below the limit.

Only add contacts who agreed to receive the emails of this list, and keep a record of when and how each of them agreed. See [Data protection](https://docs.kentico.com/documentation/developers-and-admins/data-protection/gdpr-compliance) in the Xperience documentation.

## Consent agreements

Record in Xperience that the contacts already agreed to a consent somewhere else, for example on a signup form or at an event. The import records that consent; it does not ask the contacts for it.

- **Use a dedicated consent** whose text is what these contacts agreed to, named so you can tell where it came from (for example "Newsletter – webinar signup form, 2024–2026"). Do not use a consent of your website that these contacts never saw. See [Consent management](https://docs.kentico.com/documentation/developers-and-admins/data-protection/consent-management).
- **Finish the consent text before you import.** Each agreement is tied to the text the consent has at the time of the import.
- **Record consent agreements**: for every contact in the file, or only for rows where the **Consent given** column says yes (`y`, `yes`, `true`, `1`, `x`). Other values and empty values mean no.
- **Agreement date**: the time of the import, or the date from the **Consent date** column. Rows without a date get the time of the import. Rows with a date in the future get no agreement.
- Contacts who already agreed (not withdrawn) get no new agreement.
- **Contacts who withdrew this consent**: keep it withdrawn (default), or record a new agreement. Record a new agreement only if each of them agreed again after withdrawing. An agreement dated before the withdrawal does not count: the consent stays withdrawn.

The rules for email marketing and consent differ between countries and regions. If you are not sure which rules apply to you, ask the person responsible for data protection in your organization.

## Confirmation of consent

An import that adds contacts to a recipient list (including subscribing them again) or records consent agreements needs a confirmation:

- Only users with the **Confirm consent for imported contacts** permission can run it.
- The **Run import** confirmation shows a statement that documented consent exists for these contacts. Selecting **Run import** confirms it.
- The user, the time and the statement version are stored with the import, shown in its summary, written to the event log and passed to the [audit event](./Usage-Guide.md#audit-contact-imports).

## Duplicate rows

Clean data is expected: one row per contact (match value).

- Rows with the same match value are applied in file order: the last non-empty value wins.
- The check (dry run) can count too many new contacts for duplicates. The import itself creates each contact once.
- The Upload step estimates likely duplicates and lets you download their row numbers. To fix them, cancel the import, clean the file and start again.

## Row reports

Row reports are gzipped CSV files (`contact-import-analyze-report.csv.gz`, `contact-import-execute-report.csv.gz`), one line per row. Column names and codes are the same in every admin language.

| Column | Content |
| --- | --- |
| `row` | Row number in the file. CSV/TSV: data row (the header row is not counted, empty lines are). NDJSON: line number. JSON: position in the array. Excel: sheet row minus the header row. |
| `outcome` | `created`, `updated`, `unchanged`, `skipped` |
| `reason` | `newContactsNotCreated`, `existingContactsNotUpdated`, `multipleMatches`, `invalidEmail`, `invalidValue`, `duplicateKey`, `emailNotImported`, or empty |
| `field` | Contact field the reason is about (for example with `invalidValue`) |
| `list` | Only with a recipient list: `added`, `alreadySubscribed`, `keptUnsubscribed`, `resubscribed`, `skippedBounced`, `skippedNoEmail`; followed by `;bouncedHard` or `;bouncedSoft` when the address bounced |
| `consent` | Only with consent: `recorded`, `alreadyAgreed`, `keptRevoked`, `reAgreed`, `notGiven`, `skippedFutureDate` |
| `key` | Only when you chose to include it: the match value (personal data) |

When a row updates several contacts, the `list` and `consent` values of each contact are separated by `|`. Rows merged into a later row with the same match value (`duplicateKey`) have empty `list` and `consent` values.

Rows skipped in the browser during upload are not in these reports; they are in the **skipped rows** download of the Upload step.

## Recent imports

The **Imports** page lists the most recent imports (up to 200): file and size, when and by whom it was created, state, rows, new and changed contacts, recipient list and consent.

- **Continue** opens an unfinished import. **Results** opens an ended one.
- **Delete** removes an import from the list together with its uploaded data, check results and reports, after a confirmation. An unfinished import is cancelled first. An import that is being checked or run in another tab or by another user right now cannot be deleted: delete it after it ends, or once it is paused (**Pause** selected there, or no progress for 60 seconds). Contacts already imported stay as they are. A deleted import leaves only its event log entries and the [audit event](./Usage-Guide.md#audit-contact-imports).
- Reports are kept 7 days, or 24 hours when they include match values. Unfinished imports without activity for 7 days are cancelled and their files deleted. Ended imports leave the list after 90 days. The administrator can change these times.
