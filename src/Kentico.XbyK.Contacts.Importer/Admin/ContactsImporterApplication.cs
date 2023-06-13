using CMS.Membership;
using Kentico.Xperience.Admin.Base;

namespace Kentico.Xperience.Contacts.Importer.Admin;

/// <summary>
/// The root application page for the Contacts Importer.
/// </summary>
[UIPermission(SystemPermissions.VIEW)]
internal class ContactsImporterApplication
{
    public const string IDENTIFIER = "Kentico.Xperience.Contacts.Importer";
}