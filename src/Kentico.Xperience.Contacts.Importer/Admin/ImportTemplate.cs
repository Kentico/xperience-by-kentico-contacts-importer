using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer.Admin;
using CMS.ContactManagement;

[assembly: UIPage(
   parentType: typeof(ContactsImporterApplication),
   name: "Importer",
   slug: "import",
   uiPageType: typeof(ImportTemplate),
   icon: Icons.PersonalisationVariants,
   templateName: ImportTemplate.TEMPLATE_NAME,
   order: 100)]

namespace Kentico.Xperience.Contacts.Importer.Admin
{
    internal class ImportTemplate : Page<CustomLayoutProperties>
    {
        public const string IDENTIFIER = "Kentico.Xperience.Contacts.Import.Web.Admin.ImportTemplate";
        public const string TEMPLATE_NAME = "@kentico-xperience-contacts-import/web-admin/ImportLayout";

        private readonly IContactGroupInfoProvider contactGroupInfoProvider;

        public ImportTemplate(IContactGroupInfoProvider contactGroupInfoProvider) =>
            this.contactGroupInfoProvider = contactGroupInfoProvider;

        public override async Task<CustomLayoutProperties> ConfigureTemplateProperties(CustomLayoutProperties properties)
        {
            properties.Label = "Import Contacts";

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
        public List<ContactGroupSimplified> ContactGroups { get; set; } = new();
    }
}
