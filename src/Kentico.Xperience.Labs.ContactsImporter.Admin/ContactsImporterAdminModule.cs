using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Labs.ContactsImporter.Admin;

[assembly: CMS.RegisterModule(typeof(ContactsImporterAdminModule))]

// Adds a new application category
[assembly: UICategory(ContactsImporterAdminModule.CUSTOM_CATEGORY, "Contacts Importer", Icons.CustomElement, 100)]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

internal class ContactsImporterAdminModule : AdminModule
{
    public const string CUSTOM_CATEGORY = "Kentico.Xperience.Labs.ContactsImporter.Admin.Category";

    public ContactsImporterAdminModule()
        : base("Kentico.Xperience.Labs.ContactsImporter.Admin")
    {
    }

    protected override void OnInit()
    {
        base.OnInit();

        // Makes the module accessible to the admin UI
        // Must match AdminOrgName/ProjectName in the .csproj and orgName/projectName in Client/webpack.config.js
        RegisterClientModule("kentico", "xperience-labs-contacts-importer-admin");
    }
}
