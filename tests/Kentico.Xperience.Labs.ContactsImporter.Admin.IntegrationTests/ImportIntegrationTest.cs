namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// Base of the import tests: every test starts with an empty contact table.
/// </summary>
[Category(IntegrationDatabase.Category)]
public abstract class ImportIntegrationTest
{
    protected const string Email = "ContactEmail";
    protected const string FirstName = "ContactFirstName";
    protected const string LastName = "ContactLastName";
    protected const string Code = IntegrationDatabase.CodeField;

    protected static readonly DateTime Old = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

    [SetUp]
    public void DeleteContacts() => IntegrationDatabase.DeleteContacts();

    protected static int ContactCount() => IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [OM_Contact]");

    protected static List<Dictionary<string, object?>> Contacts(string where = "1 = 1", params (string Name, object? Value)[] parameters) =>
        IntegrationDatabase.Query($"SELECT * FROM [OM_Contact] WHERE {where} ORDER BY [ContactID]", parameters);

    protected static Dictionary<string, object?> ContactByEmail(string email) =>
        Contacts("[ContactEmail] = @email", ("@email", email)).Single();

    protected static Dictionary<string, object?> ContactById(int contactId) =>
        Contacts("[ContactID] = @id", ("@id", contactId)).Single();

    /// <summary>
    /// Inserts an existing contact with plain SQL and returns its ID. Last modified defaults to <see cref="Old"/>.
    /// </summary>
    protected static int Seed(string? email, string? firstName = null, string? lastName = null, string? code = null, DateTime? lastModified = null) =>
        IntegrationDatabase.Scalar<int>(
            $"""
            INSERT INTO [OM_Contact] ([ContactEmail], [ContactFirstName], [ContactLastName], [{Code}], [ContactGUID], [ContactCreated], [ContactLastModified])
            VALUES (@email, @firstName, @lastName, @code, NEWID(), @created, @lastModified);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            ("@email", email),
            ("@firstName", firstName),
            ("@lastName", lastName),
            ("@code", code),
            ("@created", Old),
            ("@lastModified", lastModified ?? Old));
}
