using System.Globalization;
using System.Resources;

using CMS.Core;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Localized server-side strings of the importer (<c>Resources/ContactsImporterResources.resx</c>).
/// </summary>
internal interface IImportTexts
{
    /// <summary>
    /// Returns the string for <paramref name="key"/>, formatted with <paramref name="arguments"/>.
    /// </summary>
    public string Get(string key, params object[] arguments);
}

internal static class ImportTextKeys
{
    private const string PREFIX = ContactsImporterResources.KEY_PREFIX + "error.";

    public const string INVALID_JOB_ID = PREFIX + "invalidjobid";
    public const string JOB_NOT_FOUND = PREFIX + "jobnotfound";
    public const string JOB_UNREADABLE = PREFIX + "jobunreadable";
    public const string INVALID_REQUEST = PREFIX + "invalidrequest";
    public const string INVALID_STATE = PREFIX + "invalidstate";
    public const string INVALID_CONFIG = PREFIX + "invalidconfig";
    public const string JOB_ID_MISMATCH = PREFIX + "jobidmismatch";
    public const string TRUNCATE_INDEX = PREFIX + "truncateindex";
    public const string BUSY = PREFIX + "busy";
    public const string SOURCE_FIELD_MAPPED = PREFIX + "sourcefieldmapped";
    public const string ANALYSIS_EXPIRED = PREFIX + "analysisexpired";
    public const string NO_REPORT = PREFIX + "noreport";
}

/// <summary>
/// Resolves strings through Xperience localization, in the UI culture of the current user.
/// </summary>
internal sealed class LocalizedImportTexts(ILocalizationService localizationService) : IImportTexts
{
    public string Get(string key, params object[] arguments) =>
        ImportTextFormatter.Format(localizationService.GetString(key), arguments);
}

/// <summary>
/// Reads the strings straight from the embedded resource. For code that runs without an initialized Xperience application.
/// </summary>
internal sealed class ResourceImportTexts : IImportTexts
{
    private static readonly ResourceManager resources = new(typeof(ContactsImporterResources));

    public string Get(string key, params object[] arguments) =>
        ImportTextFormatter.Format(resources.GetString(key, CultureInfo.InvariantCulture) ?? key, arguments);
}

internal static class ImportTextFormatter
{
    public static string Format(string text, object[] arguments) =>
        arguments.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, arguments);
}
