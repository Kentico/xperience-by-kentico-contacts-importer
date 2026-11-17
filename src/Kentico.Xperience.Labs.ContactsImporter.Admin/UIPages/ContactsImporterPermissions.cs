namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Custom permissions of the "Contacts Importer (Labs)" application. Opening the application needs the built-in
/// View permission. Names are stored in role assignments, so do not change them.
/// </summary>
public static class ContactsImporterPermissions
{
    private const string PREFIX = "ContactsImporter.";

    /// <summary>
    /// Create, upload, continue, cancel and delete imports. A user with this permission can work with every
    /// import, not only the ones they created.
    /// </summary>
    public const string IMPORT = PREFIX + "Import";

    /// <summary>"Import contacts" in the user's admin UI language (localization expression).</summary>
    public const string IMPORT_DISPLAY_NAME = "{$" + ImportTextKeys.PERMISSION_IMPORT + "$}";

    /// <summary>
    /// Run imports that add contacts to recipient lists, resubscribe them or record consent agreements.
    /// </summary>
    public const string CONFIRM_CONSENT = PREFIX + "ConfirmConsent";

    /// <summary>"Confirm consent for imported contacts" in the user's admin UI language (localization expression).</summary>
    public const string CONFIRM_CONSENT_DISPLAY_NAME = "{$" + ImportTextKeys.PERMISSION_CONFIRM_CONSENT + "$}";
}
