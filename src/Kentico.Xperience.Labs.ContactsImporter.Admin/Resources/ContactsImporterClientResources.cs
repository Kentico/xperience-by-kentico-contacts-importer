using CMS.Localization;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

[assembly: RegisterLocalizationResource(typeof(ContactsImporterClientResources), LocalizationTarget.Client, "en-US")]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

/// <summary>
/// Marker of the client-side strings in <c>ContactsImporterClientResources.resx</c>: every text of the React client
/// (<c>Client/src</c>), read there with <c>useTranslation</c> from <c>react-i18next</c>. Registered for
/// <see cref="LocalizationTarget.Client"/>, so the admin UI sends them to the browser in the user's admin UI language.
/// </summary>
/// <remarks>
/// Keys use the <see cref="ContactsImporterResources.KEY_PREFIX"/> prefix. Placeholders use the i18next syntax
/// (<c>{{name}}</c>); counts that change the grammar use i18next plural keys (<c>_one</c>, <c>_other</c>, and the plural
/// forms a translation needs). <c>Client/src/shared/resources.test.ts</c> checks that every key the client uses is here,
/// that every key here is used, and that the placeholders match the call sites.
/// </remarks>
#pragma warning disable S2094 // Marker type: the registration needs a type next to the .resx file, and nothing else.
internal static class ContactsImporterClientResources
{
}
#pragma warning restore S2094
