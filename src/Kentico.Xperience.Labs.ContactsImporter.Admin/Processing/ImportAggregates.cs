namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Names of the counts kept in <see cref="ImportJobStatus.Aggregates"/>. Counts only, never contact data.
/// </summary>
/// <remarks>
/// <para>
/// Every processed row ends in exactly one of: <see cref="Created"/>, <see cref="Updated"/>, <see cref="Unchanged"/>,
/// the <c>skipped*</c> counts, or <see cref="DuplicateRows"/>. So their sum equals <see cref="Rows"/>.
/// </para>
/// <para>
/// The <c>list*</c>, <c>bounced*</c> and <c>consent*</c> counts are contacts, not rows: the contacts that rows
/// resolved to (created, updated, unchanged, and existing contacts that are not updated). With a recipient list,
/// every such contact is in exactly one <c>list*</c> count, and those with an email address in at most one
/// <c>bounced*</c> count; with consent, every such contact is in exactly one <c>consent*</c> count.
/// </para>
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

    /// <summary>Contacts added to the recipient list as subscribed (membership and approved confirmation).</summary>
    public const string ListAdded = "listAdded";

    /// <summary>Contacts that already are subscribed members of the recipient list.</summary>
    public const string ListAlreadySubscribed = "listAlreadySubscribed";

    /// <summary>Contacts that unsubscribed from the recipient list (or are members without a confirmation) and were left that way.</summary>
    public const string ListKeptUnsubscribed = "listKeptUnsubscribed";

    /// <summary>Unsubscribed contacts that got a new approved confirmation.</summary>
    public const string ListResubscribed = "listResubscribed";

    /// <summary>Contacts not added or resubscribed because their address is bounced and such addresses are skipped.</summary>
    public const string ListSkippedBounced = "listSkippedBounced";

    /// <summary>Contacts not added to the recipient list because they have no email address.</summary>
    public const string ListSkippedNoEmail = "listSkippedNoEmail";

    /// <summary>Contacts considered for the recipient list whose address has a hard bounce.</summary>
    public const string BouncedHard = "bouncedHard";

    /// <summary>Contacts considered for the recipient list whose address reached the soft bounce limit (no hard bounce).</summary>
    public const string BouncedSoftOverLimit = "bouncedSoftOverLimit";

    /// <summary>Contacts considered for the recipient list whose address has soft bounces below the limit; Xperience still sends to it.</summary>
    public const string BouncedSoftUnderLimit = "bouncedSoftUnderLimit";

    /// <summary>Contacts that got a consent agreement.</summary>
    public const string ConsentRecorded = "consentRecorded";

    /// <summary>Contacts whose newest agreement with the consent is not revoked; nothing written.</summary>
    public const string ConsentAlreadyAgreed = "consentAlreadyAgreed";

    /// <summary>Contacts that revoked the consent and were left that way (also when the consent date of the row is not after the revocation).</summary>
    public const string ConsentKeptRevoked = "consentKeptRevoked";

    /// <summary>Contacts that had revoked the consent and got a new agreement.</summary>
    public const string ConsentReAgreed = "consentReAgreed";

    /// <summary>Contacts whose rows do not say that consent was given (consent from a file column).</summary>
    public const string ConsentNotGiven = "consentNotGiven";

    /// <summary>Contacts without an agreement because the consent date of the row is in the future.</summary>
    public const string ConsentSkippedFutureDate = "consentSkippedFutureDate";

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

    public long ListAdded { get; init; }

    public long ListAlreadySubscribed { get; init; }

    public long ListKeptUnsubscribed { get; init; }

    public long ListResubscribed { get; init; }

    public long ListSkippedBounced { get; init; }

    public long ListSkippedNoEmail { get; init; }

    public long BouncedHard { get; init; }

    public long BouncedSoftOverLimit { get; init; }

    public long BouncedSoftUnderLimit { get; init; }

    public long ConsentRecorded { get; init; }

    public long ConsentAlreadyAgreed { get; init; }

    public long ConsentKeptRevoked { get; init; }

    public long ConsentReAgreed { get; init; }

    public long ConsentNotGiven { get; init; }

    public long ConsentSkippedFutureDate { get; init; }

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
            ListAdded = counts.GetValueOrDefault(ImportAggregateKeys.ListAdded),
            ListAlreadySubscribed = counts.GetValueOrDefault(ImportAggregateKeys.ListAlreadySubscribed),
            ListKeptUnsubscribed = counts.GetValueOrDefault(ImportAggregateKeys.ListKeptUnsubscribed),
            ListResubscribed = counts.GetValueOrDefault(ImportAggregateKeys.ListResubscribed),
            ListSkippedBounced = counts.GetValueOrDefault(ImportAggregateKeys.ListSkippedBounced),
            ListSkippedNoEmail = counts.GetValueOrDefault(ImportAggregateKeys.ListSkippedNoEmail),
            BouncedHard = counts.GetValueOrDefault(ImportAggregateKeys.BouncedHard),
            BouncedSoftOverLimit = counts.GetValueOrDefault(ImportAggregateKeys.BouncedSoftOverLimit),
            BouncedSoftUnderLimit = counts.GetValueOrDefault(ImportAggregateKeys.BouncedSoftUnderLimit),
            ConsentRecorded = counts.GetValueOrDefault(ImportAggregateKeys.ConsentRecorded),
            ConsentAlreadyAgreed = counts.GetValueOrDefault(ImportAggregateKeys.ConsentAlreadyAgreed),
            ConsentKeptRevoked = counts.GetValueOrDefault(ImportAggregateKeys.ConsentKeptRevoked),
            ConsentReAgreed = counts.GetValueOrDefault(ImportAggregateKeys.ConsentReAgreed),
            ConsentNotGiven = counts.GetValueOrDefault(ImportAggregateKeys.ConsentNotGiven),
            ConsentSkippedFutureDate = counts.GetValueOrDefault(ImportAggregateKeys.ConsentSkippedFutureDate),
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

    /// <summary>
    /// Adds the counts of <paramref name="other"/> to these counts.
    /// </summary>
    public void Add(ImportCounts other)
    {
        foreach (var (key, count) in other.counts)
        {
            Add(key, count);
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
