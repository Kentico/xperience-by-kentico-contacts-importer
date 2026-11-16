using System.Globalization;

using CMS.ContactManagement;
using CMS.DataEngine;
using CMS.Helpers;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Clears cached contact data after an import. The import writes with set-based SQL that bypasses the object API,
/// so nothing else invalidates the caches (PLAN §11, D28).
/// </summary>
internal interface IImportCacheInvalidator
{
    /// <summary>
    /// Runs once when an import completes (D36); not after a pause, cancellation or failure.
    /// </summary>
    public void InvalidateAfterImport(ImportJob job);
}

/// <summary>
/// Uses the built-in cache APIs, which also create the web farm tasks for the other application instances.
/// </summary>
internal sealed class ImportCacheInvalidator : IImportCacheInvalidator
{
    public void InvalidateAfterImport(ImportJob job)
    {
        CacheHelper.TouchKeys(GetCacheKeys(job));
        ContactInfo.TYPEINFO.InvalidateAllObjects();
        ProviderHelper.ClearHashtables(ContactInfo.OBJECT_TYPE, true);

        if (job.Options.Consent != ConsentMode.None)
        {
            // "Is agreed" results are cached per contact for 10 minutes and depend on per-contact keys only.
            CacheHelper.ClearCache("consentagreement|");
        }
    }

    /// <summary>
    /// Cache dependency keys to touch: contacts always; recipient list and consent keys when the job options use them
    /// (their writes arrive with recipient list and consent support).
    /// </summary>
    internal static IReadOnlyList<string> GetCacheKeys(ImportJob job)
    {
        var keys = new List<string> { $"{ContactInfo.OBJECT_TYPE}|all" };
        if (job.Options.RecipientListId is { } recipientListId)
        {
            keys.Add("om.contactgroupmembercontact|all");
            keys.Add("emaillibrary.emailsubscriptionconfirmation|all");
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"om.recipientlist|byid|{recipientListId}"));
        }

        if (job.Options.Consent != ConsentMode.None)
        {
            keys.Add("cms.consentagreement|all");
        }

        return keys;
    }
}
