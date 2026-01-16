using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer.Admin;

[assembly: CMS.RegisterModule(typeof(ContactImportAdminModule))]
namespace Kentico.Xperience.Contacts.Importer.Admin;

internal class ContactImportAdminModule : AdminModule
{
    public ContactImportAdminModule()
        : base(nameof(ContactImportAdminModule))
    {
    }


    protected override void OnInit()
    {
        base.OnInit();

        RegisterClientModule("kentico", "xperience-integrations-contacts-importer");
    }
}
