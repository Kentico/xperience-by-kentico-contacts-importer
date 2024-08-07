﻿using CMS.ContactManagement;
using CMS.DataEngine;

using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer.Admin;

[assembly: UIPage(
   parentType: typeof(ContactsImporterApplication),
   name: "Upload",
   slug: "upload",
   uiPageType: typeof(ImportTemplate),
   icon: Icons.PersonalisationVariants,
   templateName: ImportTemplate.TEMPLATE_NAME,
   order: 100)]

namespace Kentico.Xperience.Contacts.Importer.Admin;

internal class ImportTemplate(IInfoProvider<ContactGroupInfo> contactGroupInfoProvider) : Page<CustomLayoutProperties>
{
    public const string IDENTIFIER = "Kentico.Xperience.Contacts.Import.Web.Admin.ImportTemplate";
    public const string TEMPLATE_NAME = "@kentico/xperience-integrations-contacts-importer/ImportLayout";

    private readonly IInfoProvider<ContactGroupInfo> contactGroupInfoProvider = contactGroupInfoProvider;

    public override async Task<CustomLayoutProperties> ConfigureTemplateProperties(CustomLayoutProperties properties)
    {
        properties.Label = "Upload Contacts";

        var contactGroups = await contactGroupInfoProvider.Get()
            .Columns(nameof(ContactGroupInfo.ContactGroupGUID), nameof(ContactGroupInfo.ContactGroupDisplayName))
            .GetEnumerableTypedResultAsync();

        properties.ContactGroups = contactGroups
            .Select(x => new ContactGroupSimplified(x.ContactGroupGUID, x.ContactGroupDisplayName))
            .ToList();

        return properties;
    }
}

public record ContactGroupSimplified(Guid Guid, string DisplayName);

internal class CustomLayoutProperties : TemplateClientProperties
{
    public string Label { get; set; } = "";
    public List<ContactGroupSimplified> ContactGroups { get; set; } = [];
}
