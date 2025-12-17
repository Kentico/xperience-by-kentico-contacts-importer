namespace Kentico.Xperience.Contacts.Importer.Services.ImportService;

/// <summary>
/// User-defined import variables.
/// </summary>
/// <param name="AssignToContactGroupGuid">The contact group GUID to assign contacts to, or <c>null</c> if no assignment is
/// requested.</param>
/// /// <param name="AssignToRecipientListGuid">The recipient list GUID to assign contacts to, or <c>null</c> if no assignment is
/// requested.</param>
/// <param name="BatchSize">The import process batch size.</param>
/// <param name="Delimiter">The CSV delimiter character.</param>
/// <param name="ImportKind">The <see cref="Services.ImportService.ImportKind"/> to perform.</param>
public record ImportContext(Guid? AssignToContactGroupGuid, Guid? AssignToRecipientListGuid, int BatchSize, string Delimiter, string ImportKind);


/// <summary>
/// String enumeration of supported import kind.
/// </summary>
public static class ImportKind
{
    /// <summary>
    /// The import process will insert contacts, skipping existing contacts matched by GUID.
    /// </summary>
    public const string InsertAndSkipExisting = "insert";


    /// <summary>
    /// The import process will delete contacts matched by GUID.
    /// </summary>
    public const string Delete = "delete";
}
