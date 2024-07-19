using CMS.Base;
using CMS.Localization;

using Kentico.Xperience.Contacts.Importer.Resources;

[assembly: RegisterLocalizationResource(typeof(ContactsImporterResources), SystemContext.SYSTEM_CULTURE_NAME)]

namespace Kentico.Xperience.Contacts.Importer.Resources;

public class ContactsImporterResources
{
    public ContactsImporterResources()
    {
    }
}
