namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Names of the counts kept in <see cref="ImportJobStatus.Aggregates"/>. Counts only, never contact data.
/// </summary>
/// <remarks>
/// Every processed row ends in exactly one of: <see cref="Created"/>, <see cref="Updated"/>, <see cref="Unchanged"/>,
/// the <c>skipped*</c> counts, or <see cref="DuplicateRows"/>. So their sum equals <see cref="Rows"/>.
/// </remarks>
internal static class ImportAggregateKeys
{
    /// <summary>Rows processed.</summary>
    public const string Rows = "rows";

    /// <summary>Keys without a matching contact that are (analysis: would be) created.</summary>
    public const string Created = "created";

    /// <summary>Keys with a matching contact where at least one field changes.</summary>
    public const string Updated = "updated";

    /// <summary>Keys with a matching contact where no field changes.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>Contacts updated; more than <see cref="Updated"/> when all contacts of a key with several matches are updated.</summary>
    public const string ContactsUpdated = "contactsUpdated";

    /// <summary>Keys without a matching contact, skipped because new contacts are not created.</summary>
    public const string SkippedNew = "skippedNew";

    /// <summary>Keys with a matching contact, skipped because existing contacts are not updated.</summary>
    public const string SkippedExisting = "skippedExisting";

    /// <summary>Keys with several matching contacts, skipped because of the multiple matches option.</summary>
    public const string SkippedMultipleMatches = "skippedMultipleMatches";

    /// <summary>Rows skipped because of an invalid email (email is not the key).</summary>
    public const string SkippedInvalidEmail = "skippedInvalidEmail";

    /// <summary>Rows skipped because a value does not fit the data type of its field.</summary>
    public const string SkippedInvalidValue = "skippedInvalidValue";

    /// <summary>Rows merged into a later row with the same key in the same batch.</summary>
    public const string DuplicateRows = "duplicateRows";

    /// <summary>Keys with several matching contacts (whatever the option did with them).</summary>
    public const string MultipleMatches = "multipleMatches";

    /// <summary>Rows with an invalid email that were processed without the email value.</summary>
    public const string ImportedWithoutEmail = "importedWithoutEmail";

    /// <summary>Prefix of per-field counts of contacts whose value of the field changes: <c>fieldChanged:ContactFirstName</c>.</summary>
    public const string FieldChangedPrefix = "fieldChanged:";

    /// <summary>Prefix of per-field counts of contacts whose value of the field is cleared (part of the changed count).</summary>
    public const string FieldClearedPrefix = "fieldCleared:";
}

/// <summary>
/// Typed view of <see cref="ImportJobStatus.Aggregates"/> for progress and results.
/// </summary>
internal sealed record ImportAggregates
{
    public long Rows { get; init; }

    public long Created { get; init; }

    public long Updated { get; init; }

    public long Unchanged { get; init; }

    public long ContactsUpdated { get; init; }

    public long SkippedNew { get; init; }

    public long SkippedExisting { get; init; }

    public long SkippedMultipleMatches { get; init; }

    public long SkippedInvalidEmail { get; init; }

    public long SkippedInvalidValue { get; init; }

    public long DuplicateRows { get; init; }

    public long MultipleMatches { get; init; }

    public long ImportedWithoutEmail { get; init; }

    /// <summary>All skipped rows and keys.</summary>
    public long Skipped => SkippedNew + SkippedExisting + SkippedMultipleMatches + SkippedInvalidEmail + SkippedInvalidValue;

    /// <summary>Field change counts of updated contacts, ordered by field name.</summary>
    public IReadOnlyList<ImportFieldChangeStat> Fields { get; init; } = [];

    public static ImportAggregates From(IReadOnlyDictionary<string, long> counts)
    {
        var fields = new SortedDictionary<string, (long Changed, long Cleared)>(StringComparer.Ordinal);
        foreach (var (key, count) in counts)
        {
            if (key.StartsWith(ImportAggregateKeys.FieldChangedPrefix, StringComparison.Ordinal))
            {
                string field = key[ImportAggregateKeys.FieldChangedPrefix.Length..];
                fields[field] = (count, fields.GetValueOrDefault(field).Cleared);
            }
            else if (key.StartsWith(ImportAggregateKeys.FieldClearedPrefix, StringComparison.Ordinal))
            {
                string field = key[ImportAggregateKeys.FieldClearedPrefix.Length..];
                fields[field] = (fields.GetValueOrDefault(field).Changed, count);
            }
        }

        return new ImportAggregates
        {
            Rows = counts.GetValueOrDefault(ImportAggregateKeys.Rows),
            Created = counts.GetValueOrDefault(ImportAggregateKeys.Created),
            Updated = counts.GetValueOrDefault(ImportAggregateKeys.Updated),
            Unchanged = counts.GetValueOrDefault(ImportAggregateKeys.Unchanged),
            ContactsUpdated = counts.GetValueOrDefault(ImportAggregateKeys.ContactsUpdated),
            SkippedNew = counts.GetValueOrDefault(ImportAggregateKeys.SkippedNew),
            SkippedExisting = counts.GetValueOrDefault(ImportAggregateKeys.SkippedExisting),
            SkippedMultipleMatches = counts.GetValueOrDefault(ImportAggregateKeys.SkippedMultipleMatches),
            SkippedInvalidEmail = counts.GetValueOrDefault(ImportAggregateKeys.SkippedInvalidEmail),
            SkippedInvalidValue = counts.GetValueOrDefault(ImportAggregateKeys.SkippedInvalidValue),
            DuplicateRows = counts.GetValueOrDefault(ImportAggregateKeys.DuplicateRows),
            MultipleMatches = counts.GetValueOrDefault(ImportAggregateKeys.MultipleMatches),
            ImportedWithoutEmail = counts.GetValueOrDefault(ImportAggregateKeys.ImportedWithoutEmail),
            Fields = [.. fields.Select(f => new ImportFieldChangeStat(f.Key, f.Value.Changed, f.Value.Cleared))],
        };
    }
}

/// <param name="Field">Contact field name.</param>
/// <param name="Changed">Contacts whose value of the field changes (including cleared).</param>
/// <param name="Cleared">Contacts whose value of the field is cleared.</param>
internal sealed record ImportFieldChangeStat(string Field, long Changed, long Cleared);

/// <summary>
/// Mutable counts of one batch or one processing call, merged into the stored aggregates at each checkpoint.
/// </summary>
internal sealed class ImportCounts
{
    private readonly Dictionary<string, long> counts = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Values => counts;

    public void Add(string key, long count = 1)
    {
        if (count != 0)
        {
            counts[key] = counts.GetValueOrDefault(key) + count;
        }
    }

    public long Get(string key) => counts.GetValueOrDefault(key);

    /// <summary>
    /// Returns <paramref name="current"/> plus these counts as a new dictionary.
    /// </summary>
    public Dictionary<string, long> AddTo(IReadOnlyDictionary<string, long> current)
    {
        var sum = new Dictionary<string, long>(current, StringComparer.Ordinal);
        foreach (var (key, count) in counts)
        {
            sum[key] = sum.GetValueOrDefault(key) + count;
        }

        return sum;
    }
}
