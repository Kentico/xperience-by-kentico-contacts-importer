namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// The application-wide lock around "check the other imports, then confirm or start a Run" (PLAN D60): one unfinished
/// Run at a time, also when two users click Run at the same moment on different application instances.
/// </summary>
internal interface IImportRunLock
{
    /// <summary>
    /// Takes the lock, waiting up to <paramref name="timeout"/>. <c>null</c> when another request still holds it.
    /// Dispose releases it. Throws when the database cannot be reached.
    /// </summary>
    public IDisposable? TryEnter(TimeSpan timeout);
}

/// <summary><see cref="IImportRunLock"/> as a database application lock (<c>sp_getapplock</c>).</summary>
internal sealed class ImportRunLock : IImportRunLock
{
    /// <summary>Name of the database application lock.</summary>
    internal const string Resource = "Kentico.Xperience.Labs.ContactsImporter:runs";

    public IDisposable? TryEnter(TimeSpan timeout) => ContactImportRepository.TryAppLock(Resource, timeout);
}
