namespace Kentico.Xperience.Contacts.Importer.Services;

public record ImportResult(bool Success, Guid ContactGuid, string? Message);

public interface IImportService
{
    /// <summary>
    /// Read & parse CSV from stream, persists mapped contact
    /// </summary>
    /// <param name="csvStream"></param>
    /// <param name="onResultCallbackAsync">Called when contact info is imported</param>
    /// <param name="context">Context values</param>
    /// <param name="onErrorCallbackAsync">called when item related exception occurs</param>
    /// <returns>Enumeration of imported contacts</returns>
    Task RunImport(Stream csvStream, ImportContext context, Func<List<ImportResult>, int, Task> onResultCallbackAsync, Func<Exception, Task> onErrorCallbackAsync);
}