namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Counts of a contact import, in <see cref="ContactImportEventData.Counts"/>. For an analysis (dry run) the counts
/// are the expected values: what the import would do. For an import that failed or was cancelled, the counts cover
/// the rows processed until then.
/// </summary>
/// <remarks>
/// <para>
/// Every processed row ends in exactly one of: <see cref="Created"/>, <see cref="Updated"/>, <see cref="Unchanged"/>,
/// the <c>Skipped*</c> counts, or <see cref="DuplicateRows"/>. So their sum equals <see cref="Rows"/>.
/// </para>
/// <para>
/// The <c>List*</c>, <c>Bounced*</c> and <c>Consent*</c> counts are contacts, not rows. With a recipient list, every
/// imported or matched contact is in exactly one <c>List*</c> count, and those with an email address in at most one
/// <c>Bounced*</c> count; with consent, every such contact is in exactly one <c>Consent*</c> count.
/// </para>
/// </remarks>
public sealed class ContactImportCounts
{
    /// <summary>Rows processed.</summary>
    public long Rows { get; init; }

    /// <summary>Keys without a matching contact for which a contact is created.</summary>
    public long Created { get; init; }

    /// <summary>Keys with a matching contact where at least one field changes.</summary>
    public long Updated { get; init; }

    /// <summary>Keys with a matching contact where no field changes.</summary>
    public long Unchanged { get; init; }

    /// <summary>Contacts updated; more than <see cref="Updated"/> when all contacts of a key with several matches are updated.</summary>
    public long ContactsUpdated { get; init; }

    /// <summary>Keys without a matching contact, skipped because new contacts are not created.</summary>
    public long SkippedNew { get; init; }

    /// <summary>Keys with a matching contact, skipped because existing contacts are not updated.</summary>
    public long SkippedExisting { get; init; }

    /// <summary>Keys with several matching contacts, skipped because of the multiple matches option.</summary>
    public long SkippedMultipleMatches { get; init; }

    /// <summary>Rows skipped because of an invalid email address (the email is not the key).</summary>
    public long SkippedInvalidEmail { get; init; }

    /// <summary>Rows skipped because a value does not fit the data type of its field.</summary>
    public long SkippedInvalidValue { get; init; }

    /// <summary>Rows merged into a later row with the same key.</summary>
    public long DuplicateRows { get; init; }

    /// <summary>Keys with several matching contacts, whatever the multiple matches option did with them.</summary>
    public long MultipleMatches { get; init; }

    /// <summary>Rows with an invalid email address that were imported without the email address.</summary>
    public long ImportedWithoutEmail { get; init; }

    /// <summary>Contacts added to the recipient list as subscribed.</summary>
    public long ListAdded { get; init; }

    /// <summary>Contacts that already are subscribed members of the recipient list.</summary>
    public long ListAlreadySubscribed { get; init; }

    /// <summary>Contacts that unsubscribed from the recipient list and were left that way.</summary>
    public long ListKeptUnsubscribed { get; init; }

    /// <summary>Unsubscribed contacts that were subscribed again.</summary>
    public long ListResubscribed { get; init; }

    /// <summary>Contacts not added or resubscribed because their address bounced and such addresses are skipped.</summary>
    public long ListSkippedBounced { get; init; }

    /// <summary>Contacts not added to the recipient list because they have no email address.</summary>
    public long ListSkippedNoEmail { get; init; }

    /// <summary>Contacts considered for the recipient list whose address has a hard bounce.</summary>
    public long BouncedHard { get; init; }

    /// <summary>Contacts considered for the recipient list whose address reached the soft bounce limit (no hard bounce).</summary>
    public long BouncedSoftOverLimit { get; init; }

    /// <summary>Contacts considered for the recipient list whose address has soft bounces below the limit.</summary>
    public long BouncedSoftUnderLimit { get; init; }

    /// <summary>Contacts that got a consent agreement.</summary>
    public long ConsentRecorded { get; init; }

    /// <summary>Contacts that already agreed to the consent (not revoked); nothing recorded.</summary>
    public long ConsentAlreadyAgreed { get; init; }

    /// <summary>Contacts that revoked the consent and were left that way.</summary>
    public long ConsentKeptRevoked { get; init; }

    /// <summary>Contacts that had revoked the consent and got a new agreement.</summary>
    public long ConsentReAgreed { get; init; }

    /// <summary>Contacts whose rows do not say that consent was given (consent from a file column).</summary>
    public long ConsentNotGiven { get; init; }

    /// <summary>Contacts without an agreement because the consent date of the row is in the future.</summary>
    public long ConsentSkippedFutureDate { get; init; }

    /// <summary>Per contact field: how many contacts get a changed or a cleared value. Ordered by field name.</summary>
    public IReadOnlyList<ContactImportFieldChange> FieldChanges { get; init; } = [];

    internal static ContactImportCounts From(ImportAggregates aggregates) => new()
    {
        Rows = aggregates.Rows,
        Created = aggregates.Created,
        Updated = aggregates.Updated,
        Unchanged = aggregates.Unchanged,
        ContactsUpdated = aggregates.ContactsUpdated,
        SkippedNew = aggregates.SkippedNew,
        SkippedExisting = aggregates.SkippedExisting,
        SkippedMultipleMatches = aggregates.SkippedMultipleMatches,
        SkippedInvalidEmail = aggregates.SkippedInvalidEmail,
        SkippedInvalidValue = aggregates.SkippedInvalidValue,
        DuplicateRows = aggregates.DuplicateRows,
        MultipleMatches = aggregates.MultipleMatches,
        ImportedWithoutEmail = aggregates.ImportedWithoutEmail,
        ListAdded = aggregates.ListAdded,
        ListAlreadySubscribed = aggregates.ListAlreadySubscribed,
        ListKeptUnsubscribed = aggregates.ListKeptUnsubscribed,
        ListResubscribed = aggregates.ListResubscribed,
        ListSkippedBounced = aggregates.ListSkippedBounced,
        ListSkippedNoEmail = aggregates.ListSkippedNoEmail,
        BouncedHard = aggregates.BouncedHard,
        BouncedSoftOverLimit = aggregates.BouncedSoftOverLimit,
        BouncedSoftUnderLimit = aggregates.BouncedSoftUnderLimit,
        ConsentRecorded = aggregates.ConsentRecorded,
        ConsentAlreadyAgreed = aggregates.ConsentAlreadyAgreed,
        ConsentKeptRevoked = aggregates.ConsentKeptRevoked,
        ConsentReAgreed = aggregates.ConsentReAgreed,
        ConsentNotGiven = aggregates.ConsentNotGiven,
        ConsentSkippedFutureDate = aggregates.ConsentSkippedFutureDate,
        FieldChanges = [.. aggregates.Fields.Select(f => new ContactImportFieldChange { FieldName = f.Field, Changed = f.Changed, Cleared = f.Cleared })],
    };
}

/// <summary>
/// How many contacts of a contact import get a changed or a cleared value of one contact field.
/// </summary>
public sealed class ContactImportFieldChange
{
    /// <summary>Name of the contact field, for example <c>ContactFirstName</c>.</summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>Contacts whose value of the field changes, including the cleared ones.</summary>
    public long Changed { get; init; }

    /// <summary>Contacts whose value of the field is cleared (part of <see cref="Changed"/>).</summary>
    public long Cleared { get; init; }
}
