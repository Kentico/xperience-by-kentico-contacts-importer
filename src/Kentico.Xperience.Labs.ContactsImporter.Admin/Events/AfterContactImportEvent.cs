using CMS.Base;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Raised after the analysis (dry run) of a contact import finished, and after an import completed, failed or was
/// cancelled. Use it to keep an audit trail of contact imports.
/// </summary>
/// <remarks>
/// <para>
/// Register a handler with <c>services.AddEventHandler&lt;AfterContactImportEvent, THandler&gt;()</c>, where the handler
/// implements <see cref="IAsyncEventHandler{TEvent}"/>.
/// </para>
/// <para>
/// The event is raised after the outcome is stored, by the request that caused it, which waits for all handlers:
/// keep handlers short. A handler exception is logged and does not change the import or stop other handlers.
/// </para>
/// <para>
/// Delivery is best effort: the event is not raised again when the application stops between storing the outcome
/// and calling the handlers, and in a web farm an outcome can, rarely, be reported by two instances. The data holds
/// counts and settings only, never contact data.
/// </para>
/// </remarks>
public sealed class AfterContactImportEvent : AsyncEvent<ContactImportEventData>
{
    /// <summary>
    /// Creates an event with empty data. Use it only in tests.
    /// </summary>
    public AfterContactImportEvent()
    {
    }

    /// <summary>
    /// Creates an event with the specified data.
    /// </summary>
    public AfterContactImportEvent(ContactImportEventData data)
        : base(data)
    {
    }
}

/// <summary>
/// What a contact import reached when <see cref="AfterContactImportEvent"/> is raised.
/// </summary>
public enum ContactImportStatus
{
    /// <summary>The analysis (dry run) finished. Nothing was written to the database.</summary>
    Analyzed,

    /// <summary>The import processed all rows.</summary>
    Completed,

    /// <summary>The analysis or the import stopped because of an error. Batches written before the error stay.</summary>
    Failed,

    /// <summary>The import was cancelled or abandoned. Batches written before that stay.</summary>
    Cancelled,
}

/// <summary>
/// Data of the <see cref="AfterContactImportEvent"/>.
/// </summary>
public sealed class ContactImportEventData
{
    /// <summary>
    /// Identifier of the import.
    /// </summary>
    public Guid JobID { get; init; }

    /// <summary>
    /// Identifier of the administration user whose request finished the analysis, finished the import or cancelled
    /// it. When that user is not known (for example an abandoned import that is cleaned up), the user who created
    /// the import.
    /// </summary>
    public int UserID { get; init; }

    /// <summary>
    /// Identifier of the administration user who created the import.
    /// </summary>
    public int CreatedByUserID { get; init; }

    /// <summary>
    /// Date and time the event was raised, in the server local time.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Name of the imported file. The file itself never reaches the server.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Size of the imported file in bytes.
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// <c>true</c> when the event is about the analysis (dry run), or about an import that ended before it started
    /// to write: nothing was written to the database. <c>false</c> when the import wrote, or may have written, data.
    /// </summary>
    public bool IsDryRun { get; init; }

    /// <summary>
    /// What the import reached.
    /// </summary>
    public ContactImportStatus Status { get; init; }

    /// <summary>
    /// Processing options of the import, for example what happens to new contacts or to unsubscribed contacts.
    /// </summary>
    public ContactImportOptions Options { get; init; } = new();

    /// <summary>
    /// Counts of the analysis or of the import, for example rows, created and updated contacts, contacts added to the
    /// recipient list. For an analysis the counts are the expected values: what the import would do.
    /// </summary>
    public ContactImportCounts Counts { get; init; } = new();

    /// <summary>
    /// Identifier of the recipient list contacts are added to, or <c>null</c> when the import changes no recipient list.
    /// </summary>
    public int? RecipientListID { get; init; }

    /// <summary>
    /// Identifier of the consent agreements are recorded for, or <c>null</c> when the import records no agreements.
    /// </summary>
    public int? ConsentID { get; init; }

    /// <summary>
    /// Hash of the consent text when the event was raised (the version of the text the agreements refer to), or
    /// <c>null</c> when the import records no agreements or the consent no longer exists.
    /// </summary>
    public string? ConsentHash { get; init; }

    /// <summary>
    /// Identifier of the administration user who confirmed the import and with it the consent statement, or
    /// <c>null</c> when the import is not confirmed with a statement.
    /// </summary>
    public int? AttestationUserID { get; init; }

    /// <summary>
    /// Date and time (UTC) of that confirmation.
    /// </summary>
    public DateTime? AttestationTimeUtc { get; init; }

    /// <summary>
    /// Version of the consent statement the user confirmed.
    /// </summary>
    public string? AttestationStatementVersion { get; init; }

    /// <summary>
    /// Name of the contact field stamped with the import source value, or <c>null</c> when contacts are not stamped.
    /// The value is <see cref="ContactImportOptions.SourceValue"/> of <see cref="Options"/>.
    /// </summary>
    public string? ImportSourceFieldName { get; init; }
}
