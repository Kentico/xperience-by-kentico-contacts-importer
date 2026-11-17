namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Processing options of a contact import, in <see cref="ContactImportEventData.Options"/>. The recipient list,
/// consent and source field are in <see cref="ContactImportEventData"/> itself.
/// </summary>
public sealed class ContactImportOptions
{
    /// <summary>
    /// What happens to file rows without a matching contact.
    /// </summary>
    public ContactImportNewContacts NewContacts { get; init; }

    /// <summary>
    /// What happens to file rows with a matching contact.
    /// </summary>
    public ContactImportExistingContacts ExistingContacts { get; init; }

    /// <summary>
    /// What empty values in the file do to the fields of existing contacts.
    /// </summary>
    public ContactImportEmptyValues EmptyValues { get; init; }

    /// <summary>
    /// What happens to file rows that match several contacts.
    /// </summary>
    public ContactImportMultipleMatches MultipleMatches { get; init; }

    /// <summary>
    /// What happens to file rows with an invalid email address, when the email is mapped and is not the key.
    /// </summary>
    public ContactImportInvalidEmail InvalidEmail { get; init; }

    /// <summary>
    /// What happens to contacts that unsubscribed from the recipient list.
    /// </summary>
    public ContactImportUnsubscribed Unsubscribed { get; init; }

    /// <summary>
    /// What happens to contacts with a bounced email address when they are added to the recipient list.
    /// </summary>
    public ContactImportBouncedAddress BouncedAddress { get; init; }

    /// <summary>
    /// How consent agreements are recorded.
    /// </summary>
    public ContactImportConsent Consent { get; init; }

    /// <summary>
    /// What happens to contacts that revoked the consent.
    /// </summary>
    public ContactImportRevokedConsent RevokedConsent { get; init; }

    /// <summary>
    /// Which contacts are stamped with <see cref="SourceValue"/>.
    /// </summary>
    public ContactImportSourceStamp SourceStamp { get; init; }

    /// <summary>
    /// Value stamped into <see cref="ContactImportEventData.ImportSourceFieldName"/>, or <c>null</c> when none is set.
    /// </summary>
    public string? SourceValue { get; init; }

    /// <summary>
    /// <c>true</c> when the row report includes the key column (the report then holds personal data).
    /// </summary>
    public bool ReportIncludesKey { get; init; }

    internal static ContactImportOptions From(ImportOptions options) => new()
    {
        NewContacts = ContactImportEventMapping.Map(options.NewContacts),
        ExistingContacts = ContactImportEventMapping.Map(options.ExistingContacts),
        EmptyValues = ContactImportEventMapping.Map(options.EmptyValues),
        MultipleMatches = ContactImportEventMapping.Map(options.MultipleMatches),
        InvalidEmail = ContactImportEventMapping.Map(options.InvalidEmail),
        Unsubscribed = ContactImportEventMapping.Map(options.Unsubscribed),
        BouncedAddress = ContactImportEventMapping.Map(options.BouncedAddress),
        Consent = ContactImportEventMapping.Map(options.Consent),
        RevokedConsent = ContactImportEventMapping.Map(options.RevokedConsent),
        SourceStamp = ContactImportEventMapping.Map(options.SourceStamp),
        SourceValue = options.SourceValue,
        ReportIncludesKey = options.ReportIncludesKey,
    };
}

/// <summary>
/// What happens to file rows without a matching contact.
/// </summary>
public enum ContactImportNewContacts
{
    /// <summary>A contact is created.</summary>
    Create,

    /// <summary>The row is skipped.</summary>
    Skip,
}

/// <summary>
/// What happens to file rows with a matching contact.
/// </summary>
public enum ContactImportExistingContacts
{
    /// <summary>The contact is updated with the values of the row.</summary>
    Update,

    /// <summary>The row is skipped.</summary>
    Skip,
}

/// <summary>
/// What empty values in the file do to the fields of existing contacts.
/// </summary>
public enum ContactImportEmptyValues
{
    /// <summary>The field keeps its value.</summary>
    Ignore,

    /// <summary>The field is cleared.</summary>
    Clear,
}

/// <summary>
/// What happens to file rows that match several contacts.
/// </summary>
public enum ContactImportMultipleMatches
{
    /// <summary>The most recently modified contact is updated.</summary>
    UpdateNewest,

    /// <summary>All matching contacts are updated.</summary>
    UpdateAll,

    /// <summary>The row is skipped.</summary>
    Skip,
}

/// <summary>
/// What happens to file rows with an invalid email address, when the email is mapped and is not the key.
/// </summary>
public enum ContactImportInvalidEmail
{
    /// <summary>The row is skipped.</summary>
    SkipRow,

    /// <summary>The row is imported without the email address.</summary>
    ImportWithoutEmail,
}

/// <summary>
/// What happens to contacts that unsubscribed from the recipient list.
/// </summary>
public enum ContactImportUnsubscribed
{
    /// <summary>The contact stays unsubscribed.</summary>
    Keep,

    /// <summary>The contact gets a new approved subscription confirmation.</summary>
    Resubscribe,
}

/// <summary>
/// What happens to contacts with a bounced email address when they are added to the recipient list.
/// </summary>
public enum ContactImportBouncedAddress
{
    /// <summary>The contact is added and reported.</summary>
    AddAndReport,

    /// <summary>The contact is not added and is reported.</summary>
    SkipAndReport,
}

/// <summary>
/// How consent agreements are recorded.
/// </summary>
public enum ContactImportConsent
{
    /// <summary>No agreements are recorded.</summary>
    None,

    /// <summary>Every imported contact gets an agreement.</summary>
    Blanket,

    /// <summary>Every imported contact gets an agreement unless file columns say that consent was not given.</summary>
    BlanketWithColumnOverride,
}

/// <summary>
/// What happens to contacts that revoked the consent.
/// </summary>
public enum ContactImportRevokedConsent
{
    /// <summary>The consent stays revoked.</summary>
    Keep,

    /// <summary>The contact gets a new agreement.</summary>
    ReAgree,
}

/// <summary>
/// Which contacts are stamped with the import source value.
/// </summary>
public enum ContactImportSourceStamp
{
    /// <summary>Only created contacts.</summary>
    CreatedOnly,

    /// <summary>Created and updated contacts.</summary>
    CreatedAndUpdated,
}

/// <summary>
/// Maps the internal import options to the public values of <see cref="AfterContactImportEvent"/>. Every internal
/// value must have a public one: a new internal value without one throws (and fails a unit test).
/// </summary>
internal static class ContactImportEventMapping
{
    public static ContactImportNewContacts Map(NewContactsBehavior value) => value switch
    {
        NewContactsBehavior.Create => ContactImportNewContacts.Create,
        NewContactsBehavior.Skip => ContactImportNewContacts.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportExistingContacts Map(ExistingContactsBehavior value) => value switch
    {
        ExistingContactsBehavior.Update => ContactImportExistingContacts.Update,
        ExistingContactsBehavior.Skip => ContactImportExistingContacts.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportEmptyValues Map(EmptyValuesBehavior value) => value switch
    {
        EmptyValuesBehavior.Ignore => ContactImportEmptyValues.Ignore,
        EmptyValuesBehavior.Clear => ContactImportEmptyValues.Clear,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportMultipleMatches Map(MultipleMatchesBehavior value) => value switch
    {
        MultipleMatchesBehavior.UpdateNewest => ContactImportMultipleMatches.UpdateNewest,
        MultipleMatchesBehavior.UpdateAll => ContactImportMultipleMatches.UpdateAll,
        MultipleMatchesBehavior.Skip => ContactImportMultipleMatches.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportInvalidEmail Map(InvalidEmailBehavior value) => value switch
    {
        InvalidEmailBehavior.SkipRow => ContactImportInvalidEmail.SkipRow,
        InvalidEmailBehavior.ImportWithoutEmail => ContactImportInvalidEmail.ImportWithoutEmail,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportUnsubscribed Map(UnsubscribedBehavior value) => value switch
    {
        UnsubscribedBehavior.Keep => ContactImportUnsubscribed.Keep,
        UnsubscribedBehavior.Resubscribe => ContactImportUnsubscribed.Resubscribe,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportBouncedAddress Map(BouncedAddressBehavior value) => value switch
    {
        BouncedAddressBehavior.AddAndReport => ContactImportBouncedAddress.AddAndReport,
        BouncedAddressBehavior.SkipAndReport => ContactImportBouncedAddress.SkipAndReport,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportConsent Map(ConsentMode value) => value switch
    {
        ConsentMode.None => ContactImportConsent.None,
        ConsentMode.Blanket => ContactImportConsent.Blanket,
        ConsentMode.BlanketWithColumnOverride => ContactImportConsent.BlanketWithColumnOverride,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportRevokedConsent Map(RevokedConsentBehavior value) => value switch
    {
        RevokedConsentBehavior.Keep => ContactImportRevokedConsent.Keep,
        RevokedConsentBehavior.ReAgree => ContactImportRevokedConsent.ReAgree,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static ContactImportSourceStamp Map(SourceStampBehavior value) => value switch
    {
        SourceStampBehavior.CreatedOnly => ContactImportSourceStamp.CreatedOnly,
        SourceStampBehavior.CreatedAndUpdated => ContactImportSourceStamp.CreatedAndUpdated,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
