using CMS.ContactManagement;

namespace Kentico.Xperience.Contacts.Importer.Services.ImportService;


/// <summary>
/// Represents the result of a single contact import function.
/// </summary>
/// <param name="Success"><c>True</c> if the import succeeded.</param>
/// <param name="ContactGuid">The<see cref="ContactInfo.ContactGUID"/> of the contact processed.</param>
/// <param name="Message">An error message, if unsuccessful.</param>
public record ImportResult(bool Success, Guid ContactGuid, string? Message);


/// <summary>
/// Contains methods for importing contacts from a CSV file.
/// </summary>
public interface IImportService
{
    /// <summary>
    /// Read &amp; parse CSV from stream, persists mapped contact.
    /// </summary>
    /// <param name="csvStream">Readable stream from CSV file.</param>
    /// <param name="context">Context values.</param>
    /// <param name="onResultCallbackAsync">Called when contact info is imported.</param>
    /// <param name="onErrorCallbackAsync">Called when item related exception occurs.</param>
    public Task RunImport(
        Stream csvStream,
        ImportContext context,
        Func<List<ImportResult>, int, Task> onResultCallbackAsync,
        Func<Exception, Task> onErrorCallbackAsync);
}
