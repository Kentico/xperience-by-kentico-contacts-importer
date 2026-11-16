namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Processing behaviors chosen by the marketer (PLAN §8). Defaults match the plan table.
/// Enum values are serialized as camelCase strings.
/// </summary>
internal sealed record ImportOptions
{
    public NewContactsBehavior NewContacts { get; init; } = NewContactsBehavior.Create;

    public ExistingContactsBehavior ExistingContacts { get; init; } = ExistingContactsBehavior.Update;

    public EmptyValuesBehavior EmptyValues { get; init; } = EmptyValuesBehavior.Ignore;

    public MultipleMatchesBehavior MultipleMatches { get; init; } = MultipleMatchesBehavior.UpdateNewest;

    /// <summary>
    /// Applies only when the email field is mapped and is not the dedupe key.
    /// </summary>
    public InvalidEmailBehavior InvalidEmail { get; init; } = InvalidEmailBehavior.SkipRow;

    /// <summary>
    /// Recipient list (contact group ID) contacts are added to as confirmed. Null = no list changes.
    /// </summary>
    public int? RecipientListId { get; init; }

    public UnsubscribedBehavior Unsubscribed { get; init; } = UnsubscribedBehavior.Keep;

    public BouncedAddressBehavior BouncedAddress { get; init; } = BouncedAddressBehavior.AddAndReport;

    public ConsentMode Consent { get; init; } = ConsentMode.None;

    /// <summary>
    /// Consent agreements are recorded for. Required when <see cref="Consent"/> is not <see cref="ConsentMode.None"/>.
    /// </summary>
    public int? ConsentId { get; init; }

    public RevokedConsentBehavior RevokedConsent { get; init; } = RevokedConsentBehavior.Keep;

    /// <summary>
    /// Contact field stamped with <see cref="SourceValue"/>. Null = no stamp.
    /// </summary>
    public string? SourceField { get; init; }

    public string? SourceValue { get; init; }

    public SourceStampBehavior SourceStamp { get; init; } = SourceStampBehavior.CreatedOnly;

    /// <summary>
    /// Row report includes the key column. The report then holds personal data and gets the short retention.
    /// </summary>
    public bool ReportIncludesKey { get; init; }
}

internal enum NewContactsBehavior
{
    Create,
    Skip,
}

internal enum ExistingContactsBehavior
{
    /// <summary>Apply the merge rule (PLAN §7).</summary>
    Update,
    Skip,
}

internal enum EmptyValuesBehavior
{
    Ignore,
    Clear,
}

internal enum MultipleMatchesBehavior
{
    UpdateNewest,
    UpdateAll,
    Skip,
}

internal enum InvalidEmailBehavior
{
    SkipRow,
    ImportWithoutEmail,
}

internal enum UnsubscribedBehavior
{
    Keep,
    Resubscribe,
    ReportOnly,
}

internal enum BouncedAddressBehavior
{
    AddAndReport,
    SkipAndReport,
}

internal enum ConsentMode
{
    None,
    Blanket,
    BlanketWithColumnOverride,
}

internal enum RevokedConsentBehavior
{
    Keep,
    ReAgree,
}

internal enum SourceStampBehavior
{
    CreatedOnly,
    CreatedAndUpdated,
}
