using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer;
using Kentico.Xperience.Contacts.Importer.UIPages.CustomTemplate;

[assembly: UIApplication("Community.ImportTemplate", typeof(ImportTemplate), "ImportTemplate", "Upload file", ContactImportAdminModule.CUSTOM_CATEGORY, Icons.Clock, "@community/web-admin/CustomLayout")]

namespace Kentico.Xperience.Contacts.Importer.UIPages.CustomTemplate
{
    using CMS.ContactManagement;

    internal class ImportTemplate : Page<CustomLayoutProperties>
    {
        private readonly IContactGroupInfoProvider _contactGroupInfoProvider;

        public ImportTemplate(IContactGroupInfoProvider contactGroupInfoProvider)
        {
            _contactGroupInfoProvider = contactGroupInfoProvider;
        }

        public override Task<CustomLayoutProperties> ConfigureTemplateProperties(CustomLayoutProperties properties)
        {
            properties.Label = "Insert CSV file";
            properties.ContactGroups = _contactGroupInfoProvider.Get()
                .Columns(nameof(ContactGroupInfo.ContactGroupGUID), nameof(ContactGroupInfo.ContactGroupDisplayName))
                .Select(x => new ContactGroupSimplified(x.ContactGroupGUID, x.ContactGroupDisplayName))
                .ToList();
            return Task.FromResult(properties);
        }
    }

    public record ContactGroupSimplified(Guid Guid, string DisplayName);

    class CustomLayoutProperties : TemplateClientProperties
    {
        public string Label { get; set; }
        public List<ContactGroupSimplified> ContactGroups { get; set; }
    }
}