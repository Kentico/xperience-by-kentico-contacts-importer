using CMS.Localization;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

[assembly: RegisterLocalizationResource(typeof(ContactsImporterResources), "en-US")]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

/// <summary>
/// Marker of the server-side strings in <c>ContactsImporterResources.resx</c>. Registered with Xperience
/// localization, so the strings resolve through <c>ILocalizationService</c> and can be translated.
/// </summary>
internal static class ContactsImporterResources
{
    /// <summary>Prefix of every resource key of the importer.</summary>
    public const string KEY_PREFIX = "contactsimporter.";
}
