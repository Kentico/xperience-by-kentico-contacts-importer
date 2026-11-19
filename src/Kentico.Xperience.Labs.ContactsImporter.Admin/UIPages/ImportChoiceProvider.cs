using CMS.ContactManagement;
using CMS.DataEngine;
using CMS.DataProtection;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Recipient lists and consents the Options step offers. Also what <c>SAVE_OPTIONS</c> checks a chosen ID against
/// and where the pages take the display names from.
/// </summary>
internal interface IImportChoiceProvider
{
    /// <summary>
    /// Existing recipient lists (contact groups that are recipient lists), ordered by display name.
    /// </summary>
    public IReadOnlyList<ImportChoiceDto> GetRecipientLists();

    /// <summary>
    /// Existing consents, ordered by display name.
    /// </summary>
    public IReadOnlyList<ImportChoiceDto> GetConsents();
}

internal sealed class ImportChoiceProvider(
    IInfoProvider<ContactGroupInfo> contactGroupProvider,
    IInfoProvider<ConsentInfo> consentProvider) : IImportChoiceProvider
{
    public IReadOnlyList<ImportChoiceDto> GetRecipientLists() =>
        [.. contactGroupProvider.Get()
            .WhereTrue(nameof(ContactGroupInfo.ContactGroupIsRecipientList))
            .Columns(nameof(ContactGroupInfo.ContactGroupID), nameof(ContactGroupInfo.ContactGroupDisplayName))
            .OrderBy(nameof(ContactGroupInfo.ContactGroupDisplayName), nameof(ContactGroupInfo.ContactGroupID))
            .GetEnumerableTypedResult()
            .Select(list => new ImportChoiceDto(list.ContactGroupID, list.ContactGroupDisplayName))];

    public IReadOnlyList<ImportChoiceDto> GetConsents() =>
        [.. consentProvider.Get()
            .Columns(nameof(ConsentInfo.ConsentID), nameof(ConsentInfo.ConsentDisplayName))
            .OrderBy(nameof(ConsentInfo.ConsentDisplayName), nameof(ConsentInfo.ConsentID))
            .GetEnumerableTypedResult()
            .Select(consent => new ImportChoiceDto(consent.ConsentID, consent.ConsentDisplayName))];
}
