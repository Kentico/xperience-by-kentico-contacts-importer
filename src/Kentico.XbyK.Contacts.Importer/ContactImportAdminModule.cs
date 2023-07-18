using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer;

[assembly: CMS.AssemblyDiscoverable]
[assembly: CMS.RegisterModule(typeof(ContactImportAdminModule))]
[assembly: UICategory(ContactImportAdminModule.CUSTOM_CATEGORY, "Contacts import", Icons.PersonalisationVariants, 100)]

namespace Kentico.Xperience.Contacts.Importer
{
    internal class ContactImportAdminModule : AdminModule
    {
        public const string CUSTOM_CATEGORY = "community.web.admin.category";

        public ContactImportAdminModule()
            : base(nameof(ContactImportAdminModule))
        {
        }

        protected override void OnInit()
        {
            base.OnInit();

            RegisterClientModule("community", "web-admin");
        }
    }
}
