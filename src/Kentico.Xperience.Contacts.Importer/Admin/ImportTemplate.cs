using CMS.ContactManagement;
using CMS.DataEngine;

using Kentico.Xperience.Admin.Base;

namespace Kentico.Xperience.Contacts.Importer.Admin;

/// <summary>
/// UI page for contact import.
/// </summary>
internal class ImportTemplate(IInfoProvider<ContactGroupInfo> contactGroupInfoProvider) : Page<ImportTemplateClientProperties>
{
    public const string TEMPLATE_NAME = "@kentico/xperience-integrations-contacts-importer/ImportLayout";


    public override Task<ImportTemplateClientProperties> ConfigureTemplateProperties(ImportTemplateClientProperties properties)
    {
        var contactGroups = contactGroupInfoProvider.Get()
            .Columns(nameof(ContactGroupInfo.ContactGroupGUID), nameof(ContactGroupInfo.ContactGroupDisplayName), nameof(ContactGroupInfo.ContactGroupIsRecipientList))
            .ToList();

        properties.ContactGroups = contactGroups
            .Where(group => !group.ContactGroupIsRecipientList)
            .Select(x => new ContactGroupSimplified(x.ContactGroupGUID, x.ContactGroupDisplayName))
            .ToList();

        properties.RecipientLists = contactGroups
            .Where(group => group.ContactGroupIsRecipientList)
            .Select(x => new ContactGroupSimplified(x.ContactGroupGUID, x.ContactGroupDisplayName))
            .ToList();

        return Task.FromResult(properties);
    }
}


/// <summary>
/// Represents an Xperience by Kentico contact group.
/// </summary>
/// <param name="Guid">The <see cref="ContactGroupInfo.ContactGroupGUID"/>.</param>
/// <param name="DisplayName">The <see cref="ContactGroupInfo.ContactGroupDisplayName"/>.</param>
public record ContactGroupSimplified(Guid Guid, string DisplayName);


/// <summary>
/// Client template properties for contact import page.
/// </summary>
internal class ImportTemplateClientProperties : TemplateClientProperties
{
    /// <summary>
    /// List of contact groups available for selection during the import process.
    /// </summary>
    public List<ContactGroupSimplified> ContactGroups { get; set; } = [];

    /// <summary>
    /// List of recipient lists available for selection during the import process.
    /// </summary>
    public List<ContactGroupSimplified> RecipientLists { get; set; } = [];
}
