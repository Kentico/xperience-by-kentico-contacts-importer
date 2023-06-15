namespace Kentico.Xperience.Contacts.Importer.Models;

public class ContactImportModel
{
    public int LineIndex { get; set; }
    public Guid ContactGuid { get; set; }
    public string Email { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
}