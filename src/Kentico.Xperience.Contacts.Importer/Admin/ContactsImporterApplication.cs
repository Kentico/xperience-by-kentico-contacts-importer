using CMS.Membership;
using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Contacts.Importer;
using Kentico.Xperience.Contacts.Importer.Admin;

[assembly: UIApplication(
    identifier: ContactsImporterApplication.IDENTIFIER,
    type: typeof(ContactsImporterApplication),
    slug: "contacts-importer",
    name: "Contacts Importer",
    category: ContactImportAdminModule.CATEGORY,
    icon: Icons.RectangleParagraph,
    templateName: TemplateNames.SECTION_LAYOUT)]

namespace Kentico.Xperience.Contacts.Importer.Admin;

/// <summary>
/// The root application page for the Contacts Importer.
/// </summary>
[UIPermission(SystemPermissions.VIEW)]
internal class ContactsImporterApplication : ApplicationPage
{
    public const string IDENTIFIER = "Kentico.Xperience.Contacts.Importer";
}
