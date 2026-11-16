using CMS.Membership;

using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.UIPages;
using Kentico.Xperience.Labs.ContactsImporter.Admin;

[assembly: UIApplication(
    identifier: ContactsImporterApplicationPage.IDENTIFIER,
    type: typeof(ContactsImporterApplicationPage),
    slug: ContactsImporterApplicationPage.SLUG,
    name: ContactsImporterApplicationPage.NAME,
    category: BaseApplicationCategories.DIGITAL_MARKETING,
    icon: Icons.ArrowUpLine,
    templateName: TemplateNames.SECTION_LAYOUT)]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// "Contacts Importer (Labs)" admin application. Assign the View permission to roles that may see imports and
/// <see cref="ContactsImporterPermissions.IMPORT"/> to roles that may create and run them.
/// </summary>
[UIPermission(SystemPermissions.VIEW)]
[UIPermission(ContactsImporterPermissions.IMPORT, ContactsImporterPermissions.IMPORT_DISPLAY_NAME)]
[UIPermission(ContactsImporterPermissions.CONFIRM_CONSENT, ContactsImporterPermissions.CONFIRM_CONSENT_DISPLAY_NAME)]
internal sealed class ContactsImporterApplicationPage : ApplicationPage
{
    public const string IDENTIFIER = "Kentico.Xperience.Labs.ContactsImporter.Application";
    public const string SLUG = "contacts-importer";
    public const string NAME = "Contacts Importer (Labs)";
}
