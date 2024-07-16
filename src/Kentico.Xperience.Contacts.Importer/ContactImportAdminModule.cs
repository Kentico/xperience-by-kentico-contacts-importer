using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer;

[assembly: CMS.RegisterModule(typeof(ContactImportAdminModule))]
[assembly: UICategory(
   codeName: "Kentico.Xperience.Contacts.Import.Web.Admin.Category",
   name: "Contacts Import",
   icon: Icons.PersonalisationVariants,
   order: 100)]

namespace Kentico.Xperience.Contacts.Importer;

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
