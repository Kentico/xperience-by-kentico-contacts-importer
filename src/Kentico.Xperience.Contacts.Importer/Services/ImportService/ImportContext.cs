namespace Kentico.Xperience.Contacts.Importer.Services;

/// <summary>
/// 
/// </summary>
/// <param name="AssignToContactGroupGuid">null if no assignment is requested</param>
/// <param name="BatchSize"></param>
/// <param name="Delimiter"></param>
/// <param name="ImportKind"><see cref="Services.ImportKind"/></param>
public record ImportContext(Guid? AssignToContactGroupGuid, int BatchSize, string Delimiter, string ImportKind);

/// <summary>
/// String enumeration of supported import kind
/// </summary>
public static class ImportKind
{
    /// <summary>
    /// import mechanism will perform insert and will all existing contacts
    /// </summary>
    public const string InsertAndSkipExisting = "insert";
    /// <summary>
    /// import mechanism will perform delete operation
    /// </summary>
    public const string Delete = "delete";
}
