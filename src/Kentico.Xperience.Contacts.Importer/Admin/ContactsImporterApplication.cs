using CMS.Membership;

using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.UIPages;
using Kentico.Xperience.Contacts.Importer.Admin;

[assembly: UICategory(
   codeName: "Kentico.Xperience.Contacts.Import.Web.Admin.Category",
   name: "Contacts Import",
   icon: Icons.PersonalisationVariants,
   order: 100)]

[assembly: UIApplication(
    identifier: ContactsImporterApplication.IDENTIFIER,
    type: typeof(ContactsImporterApplication),
    slug: "contacts-importer",
    name: "Contacts Importer",
    category: BaseApplicationCategories.DIGITAL_MARKETING,
    icon: Icons.RectangleParagraph,
    templateName: TemplateNames.SECTION_LAYOUT)]

[assembly: UIPage(
   parentType: typeof(ContactsImporterApplication),
   name: "Upload-Delete",
   slug: "upload",
   uiPageType: typeof(ImportTemplate),
   templateName: ImportTemplate.TEMPLATE_NAME,
   order: 100)]

namespace Kentico.Xperience.Contacts.Importer.Admin;

/// <summary>
/// The root application page for the Contacts Importer.
/// </summary>
[UIPermission(SystemPermissions.VIEW)]
internal class ContactsImporterApplication : ApplicationPage
{
    public const string IDENTIFIER = "Kentico.Xperience.Contacts.Importer";
}
