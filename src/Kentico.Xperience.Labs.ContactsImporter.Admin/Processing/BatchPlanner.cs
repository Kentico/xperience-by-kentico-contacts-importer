using System.Globalization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// What happens (analysis: would happen) to a row.
/// </summary>
internal enum ImportRowOutcome
{
    Created,
    Updated,
    Unchanged,
    Skipped,
}

/// <summary>
/// Reason codes of the row report. Codes only, never values from the file.
/// </summary>
internal static class ImportRowReasons
{
    /// <summary>No contact has the key and new contacts are not created.</summary>
    public const string NewContactsNotCreated = "newContactsNotCreated";

    /// <summary>A contact has the key and existing contacts are not updated.</summary>
    public const string ExistingContactsNotUpdated = "existingContactsNotUpdated";

    /// <summary>Several contacts have the key and such keys are skipped.</summary>
    public const string MultipleMatches = "multipleMatches";

    /// <summary>The email is not valid and such rows are skipped.</summary>
    public const string InvalidEmail = "invalidEmail";

    /// <summary>
    /// A value does not fit the data type of its contact field, or refers to an object (country, contact status,
    /// user, ...) that does not exist; see the field column.
    /// </summary>
    public const string InvalidValue = "invalidValue";

    /// <summary>A later row of the same batch has the same key; the rows were merged and applied once.</summary>
    public const string DuplicateKey = "duplicateKey";

    /// <summary>The email is not valid; the row was processed without it.</summary>
    public const string EmailNotImported = "emailNotImported";
}

/// <param name="Row">Source file row number.</param>
/// <param name="Outcome">Outcome of the row.</param>
/// <param name="Reason">One of <see cref="ImportRowReasons"/>, or <c>null</c>.</param>
/// <param name="Field">Contact field the reason is about, when there is one.</param>
/// <param name="Key">Dedupe key of the row. Personal data: only written to the report when the job asks for it.</param>
/// <param name="List">Recipient list outcome of the row's contact (<see cref="ImportAudienceOutcomes"/>), or <c>null</c>.</param>
/// <param name="Consent">Consent outcome of the row's contact (<see cref="ImportAudienceOutcomes"/>), or <c>null</c>.</param>
internal readonly record struct ImportRowResult(long Row, ImportRowOutcome Outcome, string? Reason, string? Field, string Key, string? List = null, string? Consent = null);

internal enum FieldValueState : byte
{
    /// <summary>The row says nothing about the field; the contact keeps its value.</summary>
    Absent,

    /// <summary>The row has an empty value; kept or cleared per the "empty values" option.</summary>
    Empty,

    /// <summary>The row has a value.</summary>
    Value,
}

/// <summary>
/// A row that passed conversion, with typed values by field index.
/// </summary>
internal sealed record PreparedRow(int Position, long Row, string Key, FieldValueState[] States, object?[] Values, bool EmailDropped, ImportRecordConsent? Consent = null);

/// <summary>
/// Rows of a batch that share a dedupe key, in file order.
/// </summary>
/// <param name="KeyText">Text identifying the key within the batch.</param>
/// <param name="KeyValue">Typed key value used for the contact lookup.</param>
/// <param name="Rows">Rows with this key.</param>
internal sealed record KeyGroup(string KeyText, object KeyValue, List<PreparedRow> Rows);

/// <summary>
/// A batch after value conversion and collapsing of duplicate keys; input of the contact lookup.
/// </summary>
internal sealed class PreparedBatch(int recordCount)
{
    /// <summary>Row results by record position; filled for rows skipped during preparation.</summary>
    public ImportRowResult?[] Results { get; } = new ImportRowResult?[recordCount];

    public List<KeyGroup> Groups { get; } = [];

    public ImportCounts Counts { get; } = new();

    /// <summary>
    /// Two text keys of the batch differ only in ways a database collation may ignore (letter case, accents, width).
    /// The database then decides which keys are equal (<see cref="IContactImportRepository.FindEquivalentKeys"/>).
    /// </summary>
    public bool HasSimilarKeys { get; set; }
}

/// <summary>
/// An existing contact matching the key of a group.
/// </summary>
/// <param name="GroupIndex">Index of the group in <see cref="PreparedBatch.Groups"/>.</param>
/// <param name="ContactId">Contact ID.</param>
/// <param name="LastModified">When the contact was last modified.</param>
/// <param name="Values">Current database values by field index (the key field is not loaded).</param>
internal sealed record ContactMatch(int GroupIndex, int ContactId, DateTime LastModified, object?[] Values);

/// <param name="Values">Typed values by field index; <c>null</c> = not set.</param>
internal sealed record ContactCreate(object?[] Values);

/// <param name="ContactId">Contact to update.</param>
/// <param name="States">State of each field by field index.</param>
/// <param name="Values">Typed values by field index (for <see cref="FieldValueState.Value"/>).</param>
internal sealed record ContactUpdate(int ContactId, FieldValueState[] States, object?[] Values);

/// <summary>
/// Decisions for one batch: contacts to create and update, the outcome of every row, and the counts.
/// </summary>
internal sealed record BatchPlan(IReadOnlyList<ContactCreate> Creates, IReadOnlyList<ContactUpdate> Updates, IReadOnlyList<ImportRowResult> Rows, ImportCounts Counts)
{
    /// <summary>
    /// Contacts the rows resolve to, for the recipient list and consent behaviors: created, updated and unchanged
    /// contacts and existing contacts that are not updated. Empty when the job has no recipient list and no consent.
    /// </summary>
    public IReadOnlyList<AudienceTarget> Targets { get; init; } = [];

    /// <summary>
    /// Examples of changed and cleared values of updated contacts (<see cref="ImportValueSampleSet"/> rules within the
    /// batch); empty unless the planner collects them.
    /// </summary>
    public IReadOnlyList<ImportValueSample> Samples { get; init; } = [];
}

/// <summary>
/// Tells which of the values of a reference field do not exist in the referenced table: positions in
/// <paramref name="values"/>. Fields that share the referenced table and column are asked about once, through
/// <paramref name="field"/>, the first of them.
/// </summary>
internal delegate IReadOnlyList<int> MissingReferenceLookup(ImportField field, IReadOnlyList<object> values);

/// <summary>
/// Pure decision logic of a batch (PLAN §6-§8): value conversion, the invalid email option, collapsing duplicate keys
/// with the merge rule, and classification of each key as create / update / unchanged / skipped.
/// The same code runs for the analysis and for the import; only the writes differ.
/// </summary>
/// <param name="context">Job being processed.</param>
/// <param name="collectSamples">Collect examples of changed and cleared values (<see cref="BatchPlan.Samples"/>); the analysis does.</param>
internal sealed class BatchPlanner(ImportProcessingContext context, bool collectSamples = false)
{
    private static readonly StringComparer similarKeyComparer = CultureInfo.InvariantCulture.CompareInfo.GetStringComparer(
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth | CompareOptions.IgnoreSymbols);

    /// <summary>
    /// Converts the values of every record, applies the invalid email option, skips rows that refer to objects that
    /// do not exist, and groups the remaining rows by key in file order.
    /// </summary>
    /// <param name="records">Records of the batch.</param>
    /// <param name="findMissingReferences">
    /// Existence check for the values of reference fields (<see cref="ImportProcessingContext.ReferenceGroups"/>);
    /// called once per referenced table that has values in the batch. <c>null</c> = references are not checked.
    /// </param>
    public PreparedBatch Prepare(IReadOnlyList<ImportRecord> records, MissingReferenceLookup? findMissingReferences = null)
    {
        var batch = new PreparedBatch(records.Count);
        var groupIndexes = new Dictionary<string, int>(records.Count, StringComparer.Ordinal);
        var similarKeys = context.KeyField.Kind == ImportValueKind.Text ? new HashSet<string>(records.Count, similarKeyComparer) : null;

        var rows = new PreparedRow?[records.Count];
        for (int position = 0; position < records.Count; position++)
        {
            batch.Counts.Add(ImportAggregateKeys.Rows);

            rows[position] = PrepareRow(position, records[position], out var skipped);
            if (rows[position] is null)
            {
                batch.Results[position] = skipped;
                batch.Counts.Add(skipped.Reason == ImportRowReasons.InvalidEmail ? ImportAggregateKeys.SkippedInvalidEmail : ImportAggregateKeys.SkippedInvalidValue);
            }
        }

        if (findMissingReferences is not null)
        {
            SkipMissingReferences(batch, rows, findMissingReferences);
        }

        foreach (var row in rows)
        {
            if (row is null)
            {
                continue;
            }

            if (row.EmailDropped)
            {
                batch.Counts.Add(ImportAggregateKeys.ImportedWithoutEmail);
            }

            object keyValue = row.Values[context.KeyIndex]!;
            string keyText = FieldValueConverter.ToKeyText(keyValue);
            if (groupIndexes.TryGetValue(keyText, out int groupIndex))
            {
                batch.Groups[groupIndex].Rows.Add(row);
            }
            else
            {
                groupIndexes.Add(keyText, batch.Groups.Count);
                batch.Groups.Add(new KeyGroup(keyText, keyValue, [row]));
                if (similarKeys is not null && !similarKeys.Add(keyText))
                {
                    batch.HasSimilarKeys = true;
                }
            }
        }

        return batch;
    }

    /// <summary>
    /// Decides what happens to each key of the batch.
    /// </summary>
    /// <param name="batch">Prepared batch.</param>
    /// <param name="matches">Existing contacts matching the keys of <see cref="PreparedBatch.Groups"/>.</param>
    /// <param name="equivalentKeys">
    /// Pairs of group indexes whose keys the database treats as equal (for example under a case-insensitive collation).
    /// </param>
    public BatchPlan Classify(PreparedBatch batch, IReadOnlyList<ContactMatch> matches, IReadOnlyList<(int Index, int EquivalentIndex)>? equivalentKeys = null)
    {
        int groupCount = batch.Groups.Count;
        var groups = new GroupUnion(groupCount);
        foreach (var (index, equivalentIndex) in equivalentKeys ?? [])
        {
            if (index >= 0 && index < groupCount && equivalentIndex >= 0 && equivalentIndex < groupCount)
            {
                groups.Union(index, equivalentIndex);
            }
        }

        // Keys that match the same contact are the same key for the database: handle their rows together,
        // otherwise one batch would update the contact twice.
        var contactGroups = new Dictionary<int, int>();
        foreach (var match in matches)
        {
            if (contactGroups.TryGetValue(match.ContactId, out int otherGroup))
            {
                groups.Union(match.GroupIndex, otherGroup);
            }
            else
            {
                contactGroups.Add(match.ContactId, match.GroupIndex);
            }
        }

        var rowsByRoot = new List<PreparedRow>?[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            (rowsByRoot[groups.Find(i)] ??= []).AddRange(batch.Groups[i].Rows);
        }

        var matchesByRoot = new Dictionary<int, ContactMatch>?[groupCount];
        foreach (var match in matches)
        {
            (matchesByRoot[groups.Find(match.GroupIndex)] ??= []).TryAdd(match.ContactId, match);
        }

        var creates = new List<ContactCreate>();
        var updates = new List<ContactUpdate>();
        var targets = new List<AudienceTarget>();
        var samples = collectSamples ? new ImportValueSampleSet() : null;
        for (int root = 0; root < groupCount; root++)
        {
            if (rowsByRoot[root] is not { } rows)
            {
                continue;
            }

            rows.Sort((x, y) => x.Position.CompareTo(y.Position));
            ClassifyGroup(batch, rows, matchesByRoot[root]?.Values.ToList() ?? [], creates, updates, targets, samples);
        }

        return new BatchPlan(creates, updates, [.. batch.Results.Select(r => r!.Value)], batch.Counts)
        {
            Targets = targets,
            Samples = samples?.ToList() ?? [],
        };
    }

    /// <summary>
    /// Skips the rows whose value of a reference field does not exist in the referenced table. Such a value can never
    /// be written (a foreign key would refuse the whole batch), so the row is skipped as a whole, like a row with a
    /// value of the wrong type, before duplicate keys are merged. Empty values are not checked: they keep or clear
    /// the field like any other empty value.
    /// </summary>
    private void SkipMissingReferences(PreparedBatch batch, PreparedRow?[] rows, MissingReferenceLookup findMissingReferences)
    {
        foreach (var fields in context.ReferenceGroups)
        {
            var values = new List<object>();
            var known = new HashSet<object>();
            foreach (var row in rows)
            {
                if (row is null)
                {
                    continue;
                }

                values.AddRange(fields
                    .Where(field => row.States[field.Index] == FieldValueState.Value)
                    .Select(field => row.Values[field.Index]!)
                    .Where(known.Add));
            }

            if (values.Count == 0)
            {
                continue;
            }

            var missing = new HashSet<object>();
            foreach (int index in findMissingReferences(fields[0], values))
            {
                if (index >= 0 && index < values.Count)
                {
                    missing.Add(values[index]);
                }
            }

            if (missing.Count == 0)
            {
                continue;
            }

            for (int position = 0; position < rows.Length; position++)
            {
                if (rows[position] is not { } row)
                {
                    continue;
                }

                var field = fields.FirstOrDefault(f => row.States[f.Index] == FieldValueState.Value && missing.Contains(row.Values[f.Index]!));
                if (field is not null)
                {
                    batch.Results[position] = new ImportRowResult(row.Row, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, field.Name, row.Key);
                    batch.Counts.Add(ImportAggregateKeys.SkippedInvalidValue);
                    rows[position] = null;
                }
            }
        }
    }

    private PreparedRow? PrepareRow(int position, ImportRecord record, out ImportRowResult skipped)
    {
        skipped = default;
        int fieldCount = context.Fields.Count;
        var states = new FieldValueState[fieldCount];
        object?[] values = new object?[fieldCount];
        bool emailDropped = false;

        for (int i = 0; i < fieldCount; i++)
        {
            string? raw = i < record.Values.Length ? record.Values[i] : null;
            if (raw is null)
            {
                continue;
            }

            if (raw.Length == 0)
            {
                states[i] = FieldValueState.Empty;
                continue;
            }

            var field = context.Fields[i];
            if (i == context.EmailIndex)
            {
                // The browser flags invalid emails, but the server decides: it never trusts the flag.
                raw = KeyNormalizer.ToLowerCase(raw);
                if (!KeyNormalizer.IsValidEmail(raw))
                {
                    if (context.Options.InvalidEmail == InvalidEmailBehavior.SkipRow)
                    {
                        skipped = new ImportRowResult(record.Row, ImportRowOutcome.Skipped, ImportRowReasons.InvalidEmail, field.Name, record.Key);
                        return null;
                    }

                    emailDropped = true;
                    continue;
                }
            }

            if (!FieldValueConverter.TryConvert(field, raw, out object value))
            {
                skipped = new ImportRowResult(record.Row, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, field.Name, record.Key);
                return null;
            }

            states[i] = FieldValueState.Value;
            values[i] = value;
        }

        if (states[context.KeyIndex] != FieldValueState.Value)
        {
            // Cannot happen for data that passed upload validation; never import a row without its key.
            skipped = new ImportRowResult(record.Row, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, context.KeyField.Name, record.Key);
            return null;
        }

        if (context.KeyField.Kind == ImportValueKind.Text)
        {
            // The normalized key (trimmed, lowercased email) is what is looked up and what a new contact gets.
            values[context.KeyIndex] = record.Key;
        }

        return new PreparedRow(position, record.Row, record.Key, states, values, emailDropped, record.Consent);
    }

    private void ClassifyGroup(PreparedBatch batch, List<PreparedRow> rows, List<ContactMatch> matches, List<ContactCreate> creates, List<ContactUpdate> updates, List<AudienceTarget> targets, ImportValueSampleSet? samples)
    {
        var counts = batch.Counts;
        var (states, values) = MergeRows(rows);
        ImportRowOutcome outcome;
        string? reason = null;

        if (matches.Count == 0)
        {
            if (context.Options.NewContacts == NewContactsBehavior.Create)
            {
                creates.Add(new ContactCreate(values));
                counts.Add(ImportAggregateKeys.Created);
                outcome = ImportRowOutcome.Created;
                AddTargets(targets, rows, states, values, [], creates.Count - 1, fieldsWritten: true);
            }
            else
            {
                counts.Add(ImportAggregateKeys.SkippedNew);
                outcome = ImportRowOutcome.Skipped;
                reason = ImportRowReasons.NewContactsNotCreated;
            }
        }
        else
        {
            if (matches.Count > 1)
            {
                counts.Add(ImportAggregateKeys.MultipleMatches);
            }

            // The recipient list and consent behaviors do not depend on whether fields are updated: they apply to
            // the contacts the multiple matches option chooses, and to none when that option skips the key.
            bool multipleSkipped = matches.Count > 1 && context.Options.MultipleMatches == MultipleMatchesBehavior.Skip;
            if (context.Options.ExistingContacts == ExistingContactsBehavior.Skip)
            {
                counts.Add(ImportAggregateKeys.SkippedExisting);
                outcome = ImportRowOutcome.Skipped;
                reason = ImportRowReasons.ExistingContactsNotUpdated;
                if (!multipleSkipped)
                {
                    AddTargets(targets, rows, states, values, SelectTargets(matches), -1, fieldsWritten: false);
                }
            }
            else if (multipleSkipped)
            {
                counts.Add(ImportAggregateKeys.SkippedMultipleMatches);
                outcome = ImportRowOutcome.Skipped;
                reason = ImportRowReasons.MultipleMatches;
            }
            else
            {
                outcome = PlanUpdates(counts, states, values, matches, updates, rows[^1], samples);
                AddTargets(targets, rows, states, values, SelectTargets(matches), -1, fieldsWritten: true);
            }
        }

        counts.Add(ImportAggregateKeys.DuplicateRows, rows.Count - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool isLast = i == rows.Count - 1;
            string? rowReason = reason
                ?? (isLast ? null : ImportRowReasons.DuplicateKey)
                ?? (row.EmailDropped ? ImportRowReasons.EmailNotImported : null);
            string? field = rowReason == ImportRowReasons.EmailNotImported ? KeyNormalizer.EmailFieldName : null;
            batch.Results[row.Position] = new ImportRowResult(row.Row, outcome, rowReason, field, row.Key);
        }
    }

    /// <summary>
    /// The matching contacts the multiple matches option chooses: the only one, all, or the newest.
    /// </summary>
    private List<ContactMatch> SelectTargets(List<ContactMatch> matches) =>
        matches.Count == 1 || context.Options.MultipleMatches == MultipleMatchesBehavior.UpdateAll
            ? [.. matches.OrderBy(m => m.ContactId)]
            : [matches.OrderByDescending(m => m.LastModified).ThenByDescending(m => m.ContactId).First()];

    /// <summary>
    /// Adds the contacts a key resolves to as targets of the recipient list and consent behaviors.
    /// </summary>
    /// <param name="targets">List to add to.</param>
    /// <param name="rows">Rows with the key, in file order.</param>
    /// <param name="states">Merged field states of the rows.</param>
    /// <param name="values">Merged field values of the rows.</param>
    /// <param name="matches">Chosen existing contacts; empty for a contact that is created.</param>
    /// <param name="createIndex">Index of the created contact in the creates of the batch, or -1.</param>
    /// <param name="fieldsWritten">The batch writes the mapped fields of the contacts.</param>
    private void AddTargets(List<AudienceTarget> targets, List<PreparedRow> rows, FieldValueState[] states, object?[] values, List<ContactMatch> matches, int createIndex, bool fieldsWritten)
    {
        if (!context.HasAudience)
        {
            return;
        }

        var last = rows[^1];
        ImportRecordConsent?[] consents = context.RecordsConsent && context.ConsentFromColumns ? [.. rows.Select(row => row.Consent)] : [];

        // The email the contact has after the batch, when the batch decides it. A new contact has exactly the mapped
        // email. An existing contact keeps its email unless the email field is written with a value, or cleared.
        int emailIndex = context.EmailFieldIndex;
        bool hasPlannedEmail = createIndex >= 0;
        string? plannedEmail = null;
        if (emailIndex >= 0 && emailIndex != context.KeyIndex && fieldsWritten)
        {
            hasPlannedEmail |= states[emailIndex] == FieldValueState.Value || (states[emailIndex] == FieldValueState.Empty && context.ClearEmptyValues);
        }

        if (hasPlannedEmail && emailIndex >= 0 && states[emailIndex] == FieldValueState.Value)
        {
            plannedEmail = values[emailIndex] as string;
        }

        if (createIndex >= 0)
        {
            targets.Add(new AudienceTarget(null, createIndex, last.Row, last.Position, true, plannedEmail, consents));

            return;
        }

        foreach (var match in matches)
        {
            targets.Add(new AudienceTarget(match.ContactId, -1, last.Row, last.Position, hasPlannedEmail, plannedEmail, consents));
        }
    }

    /// <summary>
    /// Applies the rows of one key in file order (merge rule, PLAN §7): a value replaces what earlier rows said;
    /// an empty value replaces it only when empty values clear fields.
    /// </summary>
    private (FieldValueState[] States, object?[] Values) MergeRows(List<PreparedRow> rows)
    {
        if (rows.Count == 1)
        {
            return (rows[0].States, rows[0].Values);
        }

        int fieldCount = context.Fields.Count;
        var states = new FieldValueState[fieldCount];
        object?[] values = new object?[fieldCount];
        foreach (var row in rows)
        {
            for (int i = 0; i < fieldCount; i++)
            {
                var state = row.States[i];
                if (state == FieldValueState.Value)
                {
                    states[i] = FieldValueState.Value;
                    values[i] = row.Values[i];
                }
                else if (state == FieldValueState.Empty && (context.ClearEmptyValues || states[i] == FieldValueState.Absent))
                {
                    states[i] = FieldValueState.Empty;
                    values[i] = null;
                }
            }
        }

        return (states, values);
    }

    private ImportRowOutcome PlanUpdates(ImportCounts counts, FieldValueState[] states, object?[] values, List<ContactMatch> matches, List<ContactUpdate> updates, PreparedRow lastRow, ImportValueSampleSet? samples)
    {
        bool anyChanged = false;
        foreach (var target in SelectTargets(matches))
        {
            bool changed = false;
            for (int i = 0; i < states.Length; i++)
            {
                var field = context.Fields[i];
                if (i == context.KeyIndex || (context.StampUpdatedContacts && string.Equals(field.Name, context.SourceField, StringComparison.Ordinal)))
                {
                    continue;
                }

                object? current = i < target.Values.Length ? target.Values[i] : null;
                if (states[i] == FieldValueState.Value && !FieldValueConverter.AreEqual(field, current, values[i]!))
                {
                    changed = true;
                    counts.Add(ImportAggregateKeys.FieldChangedPrefix + field.Name);
                    AddSample(samples, field, lastRow, current, values[i], cleared: false);
                }
                else if (states[i] == FieldValueState.Empty && context.ClearEmptyValues && !FieldValueConverter.IsEmpty(current))
                {
                    changed = true;
                    counts.Add(ImportAggregateKeys.FieldChangedPrefix + field.Name);
                    counts.Add(ImportAggregateKeys.FieldClearedPrefix + field.Name);
                    AddSample(samples, field, lastRow, current, null, cleared: true);
                }
            }

            if (changed)
            {
                anyChanged = true;
                updates.Add(new ContactUpdate(target.ContactId, states, values));
                counts.Add(ImportAggregateKeys.ContactsUpdated);
            }
        }

        counts.Add(anyChanged ? ImportAggregateKeys.Updated : ImportAggregateKeys.Unchanged);

        return anyChanged ? ImportRowOutcome.Updated : ImportRowOutcome.Unchanged;
    }

    /// <summary>
    /// Adds an example of a changed or cleared value unless the batch already has
    /// <see cref="ImportValueSamples.PerField"/> of the field and kind from lower rows. With several matching contacts
    /// (update all) a row has one example: the contact with the lowest ID.
    /// </summary>
    private static void AddSample(ImportValueSampleSet? samples, ImportField field, PreparedRow row, object? current, object? value, bool cleared)
    {
        if (samples is null || !samples.Wants(field.Name, cleared, row.Row))
        {
            return;
        }

        samples.Add(new ImportValueSample
        {
            Field = field.Name,
            Cleared = cleared,
            Row = row.Row,
            Key = ImportValueSamples.Truncate(row.Key),
            Current = ImportValueSamples.Format(field, current),
            New = cleared ? null : ImportValueSamples.Format(field, value),
        });
    }

    /// <summary>
    /// Union-find over group indexes; the smallest index of a set is its root.
    /// </summary>
    private sealed class GroupUnion(int count)
    {
        private readonly int[] parents = [.. Enumerable.Range(0, count)];

        public int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        public void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot != secondRoot)
            {
                parents[Math.Max(firstRoot, secondRoot)] = Math.Min(firstRoot, secondRoot);
            }
        }
    }
}
