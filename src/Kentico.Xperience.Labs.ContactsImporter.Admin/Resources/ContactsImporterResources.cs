using CMS.Localization;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

[assembly: RegisterLocalizationResource(typeof(ContactsImporterResources), LocalizationTarget.Server, "en-US")]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

/// <summary>
/// Marker of the server-side strings in <c>ContactsImporterResources.resx</c>: messages the UI pages send to the
/// browser. Registered for <see cref="LocalizationTarget.Server"/>, so the strings resolve through
/// <c>ILocalizationService</c> in the user's admin UI language. The client's own texts are in
/// <see cref="ContactsImporterClientResources"/>.
/// </summary>
/// <remarks>
/// Placeholders use the .NET syntax (<c>{0}</c>). Every key is a constant of <c>ImportTextKeys</c> and every message
/// with arguments has a factory in <c>ImportMessages</c>; <c>ImportResourcesTests</c> checks both against this file.
/// </remarks>
internal static class ContactsImporterResources
{
    /// <summary>Prefix of every resource key of the importer.</summary>
    public const string KEY_PREFIX = "contactsimporter.";
}
