using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer;
using Kentico.Xperience.Contacts.Importer.UIPages.CustomTemplate;
using CMS.ContactManagement;

[assembly: UIApplication(
   identifier: ImportTemplate.IDENTIFIER,
   type: typeof(ImportTemplate),
   slug: "ImportTemplate",
   name: "Upload file",
   category: ContactImportAdminModule.CATEGORY,
   icon: Icons.Clock,
   templateName: ImportTemplate.TEMPLATE_NAME)]

namespace Kentico.Xperience.Contacts.Importer.UIPages.CustomTemplate
{
    internal class ImportTemplate : Page<CustomLayoutProperties>
    {
        public const string IDENTIFIER = "Kentico.Xperience.Contacts.Import.Web.Admin.ImportTemplate";
        public const string TEMPLATE_NAME = "@kentico-xperience-contacts-import/web-admin/ImportLayout";

        private readonly IContactGroupInfoProvider _contactGroupInfoProvider;

        public ImportTemplate(IContactGroupInfoProvider contactGroupInfoProvider)
        {
            _contactGroupInfoProvider = contactGroupInfoProvider;
        }

        public override async Task<CustomLayoutProperties> ConfigureTemplateProperties(CustomLayoutProperties properties)
        {
            properties.Label = "Insert CSV file";

            var contactGroups = await _contactGroupInfoProvider.Get()
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
        public List<ContactGroupSimplified> ContactGroups { get; set; } = new();
    }
}
