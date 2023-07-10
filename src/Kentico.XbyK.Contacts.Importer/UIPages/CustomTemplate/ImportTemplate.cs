using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer;
using Kentico.Xperience.Contacts.Importer.UIPages.CustomTemplate;

[assembly: UIApplication("Community.ImportTemplate", typeof(ImportTemplate), "ImportTemplate", "Upload file", ContactImportAdminModule.CUSTOM_CATEGORY, Icons.Clock, "@community/web-admin/CustomLayout")]

namespace Kentico.Xperience.Contacts.Importer.UIPages.CustomTemplate
{
    internal class ImportTemplate : Page<CustomLayoutProperties>
    {
        public override Task<CustomLayoutProperties> ConfigureTemplateProperties(CustomLayoutProperties properties)
        {
            properties.Label = "Insert CSV file";
            return Task.FromResult(properties);
        }
    }

    class CustomLayoutProperties : TemplateClientProperties
    {
        public string Label { get; set; }
    }
}
