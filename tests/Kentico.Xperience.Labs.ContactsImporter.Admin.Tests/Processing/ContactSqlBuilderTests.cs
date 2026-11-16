using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

public class ContactSqlBuilderTests
{
    [TestCase("ContactEmail", "[ContactEmail]")]
    [TestCase("Odd]Name", "[Odd]]Name]")]
    [TestCase("x]; DROP TABLE OM_Contact; --", "[x]]; DROP TABLE OM_Contact; --]")]
    [TestCase("with space", "[with space]")]
    public void Identifiers_Are_Bracket_Quoted_And_Closing_Brackets_Doubled(string identifier, string expected) =>
        Assert.That(ContactSqlBuilder.QuoteIdentifier(identifier), Is.EqualTo(expected));

    [Test]
    public void Empty_Identifier_Is_Refused() => Assert.Multiple(() =>
                                                      {
                                                          Assert.That(() => ContactSqlBuilder.QuoteIdentifier(""), Throws.ArgumentException);
                                                          Assert.That(() => ContactSqlBuilder.QuoteIdentifier("a\0b"), Throws.ArgumentException);
                                                      });

    [Test]
    public void Lookup_Joins_The_Keys_Json_To_The_Key_Column_And_Selects_The_Other_Mapped_Fields()
    {
        string sql = ContactSqlBuilder.BuildLookupSql(ContactTable.Default, Context(Email, [FirstName, Email, "Age"]));

        Assert.That(sql, Is.EqualTo(
            "SELECT k.[i] AS [__i], c.[ContactID] AS [__id], c.[ContactLastModified] AS [__lm], c.[ContactFirstName] AS [f0], c.[Age] AS [f2]"
            + " FROM OPENJSON(@keys) WITH ([i] int '$.i', [k] nvarchar(450) '$.k') AS k"
            + " INNER JOIN [OM_Contact] AS c ON c.[ContactEmail] = k.[k]"));
    }

    [Test]
    public void Lookup_By_A_Number_Key_Declares_The_Key_With_Its_Type()
    {
        string sql = ContactSqlBuilder.BuildLookupSql(ContactTable.Default, Context("Big", ["Big", FirstName]));

        Assert.That(sql, Does.Contain("WITH ([i] int '$.i', [k] bigint '$.k') AS k INNER JOIN [OM_Contact] AS c ON c.[Big] = k.[k]"));
    }

    [Test]
    public void Keys_Json_Carries_Positions_And_Typed_Keys() => Assert.Multiple(() =>
                                                                     {
                                                                         Assert.That(
                                                                             ContactSqlBuilder.BuildKeysJson(Context(Email, [Email]).KeyField, ["a@x.test", "o'brien\"@x.test"]),
                                                                             Is.EqualTo("[{\"i\":0,\"k\":\"a@x.test\"},{\"i\":1,\"k\":\"o\\u0027brien\\u0022@x.test\"}]"));
                                                                         Assert.That(ContactSqlBuilder.BuildKeysJson(Context("Age", ["Age"]).KeyField, [7, 8]), Is.EqualTo("[{\"i\":0,\"k\":7},{\"i\":1,\"k\":8}]"));
                                                                     });

    [Test]
    public void Update_Applies_The_Merge_Rule_Per_Mapped_Field_Except_The_Key()
    {
        string sql = ContactSqlBuilder.BuildUpdateSql(ContactTable.Default, Context(Email, [Email, FirstName, "Total"]));

        Assert.That(sql, Is.EqualTo(
            "UPDATE c SET "
            + "c.[ContactFirstName] = CASE WHEN v.[a1] = 1 THEN c.[ContactFirstName] WHEN @clearEmpty = 1 THEN v.[v1] ELSE COALESCE(v.[v1], c.[ContactFirstName]) END, "
            + "c.[Total] = CASE WHEN v.[a2] = 1 THEN c.[Total] WHEN @clearEmpty = 1 THEN v.[v2] ELSE COALESCE(v.[v2], c.[Total]) END, "
            + "c.[ContactLastModified] = @now"
            + " FROM [OM_Contact] AS c"
            + " INNER JOIN OPENJSON(@rows) WITH ([id] int '$.id', [v1] nvarchar(100) '$.v1', [a1] bit '$.a1', [v2] decimal(10,2) '$.v2', [a2] bit '$.a2') AS v ON c.[ContactID] = v.[id]"));
    }

    [Test]
    public void Update_Stamps_The_Source_Field_Only_When_Updated_Contacts_Are_Stamped()
    {
        var createdOnly = Context(Email, [Email, FirstName], new ImportOptions { SourceField = Source, SourceValue = "x" });
        var createdAndUpdated = Context(Email, [Email, FirstName, Source], new ImportOptions { SourceField = Source, SourceValue = "x", SourceStamp = SourceStampBehavior.CreatedAndUpdated });

        string stamped = ContactSqlBuilder.BuildUpdateSql(ContactTable.Default, createdAndUpdated);

        Assert.Multiple(() =>
        {
            Assert.That(ContactSqlBuilder.BuildUpdateSql(ContactTable.Default, createdOnly), Does.Not.Contain("@source"));
            Assert.That(stamped, Does.Contain("c.[ContactSource] = @source, c.[ContactLastModified] = @now"));
            Assert.That(stamped, Does.Not.Contain("v.[v2]"), "The mapped value of the stamped field is not written by an update.");
        });
    }

    [Test]
    public void Sql_Text_Contains_Identifiers_From_The_Table_And_Class_Definition_Only_Quoted()
    {
        var table = new ContactTable("Odd]Table", "Id]Column", "Modified Column");
        var fields = new[] { new ContactFieldDefinition("Key]Field", "Key", "text", 50), new ContactFieldDefinition("Value Field", "Value", "text", 50) };
        var job = TestData.Job(null, "Key]Field", "Key]Field", "Value Field");
        Assert.That(ImportProcessingContext.TryCreate(job, fields, out var context, out string? error), Is.True, error);

        string lookup = ContactSqlBuilder.BuildLookupSql(table, context!);
        string update = ContactSqlBuilder.BuildUpdateSql(table, context!);

        Assert.Multiple(() =>
        {
            Assert.That(lookup, Does.Contain("INNER JOIN [Odd]]Table] AS c ON c.[Key]]Field] = k.[k]"));
            Assert.That(lookup, Does.Contain("c.[Id]]Column] AS [__id], c.[Modified Column] AS [__lm], c.[Value Field] AS [f1]"));
            Assert.That(update, Does.Contain("c.[Value Field] = CASE").And.Contain("c.[Modified Column] = @now FROM [Odd]]Table] AS c").And.Contain("ON c.[Id]]Column] = v.[id]"));
        });
    }

    [Test]
    public void Update_Json_Has_Values_Leaves_Out_Empty_Ones_And_Flags_Absent_Ones()
    {
        var context = Context(Email, [Email, FirstName, LastName, "Age", "Active"]);
        ContactUpdate[] updates =
        [
            new(5, [FieldValueState.Value, FieldValueState.Value, FieldValueState.Empty, FieldValueState.Absent, FieldValueState.Value], ["a@x.test", "Ann", null, null, true]),
            new(6, [FieldValueState.Value, FieldValueState.Empty, FieldValueState.Empty, FieldValueState.Value, FieldValueState.Empty], ["b@x.test", null, null, 30, null]),
        ];

        Assert.That(
            ContactSqlBuilder.BuildUpdateJson(context, updates),
            Is.EqualTo("[{\"id\":5,\"v1\":\"Ann\",\"a3\":1,\"v4\":1},{\"id\":6,\"v3\":30}]"));
    }

    [Test]
    public void Contact_Data_Never_Reaches_The_Sql_Text()
    {
        var context = Context(Email, [Email, FirstName]);
        string sql = ContactSqlBuilder.BuildLookupSql(ContactTable.Default, context) + ContactSqlBuilder.BuildUpdateSql(ContactTable.Default, context) + ContactSqlBuilder.BuildEquivalentKeysSql();

        Assert.Multiple(() =>
        {
            Assert.That(System.Text.RegularExpressions.Regex.Matches(sql, "'[^']*'").Select(m => m.Value), Is.All.StartsWith("'$."), "Only JSON paths are quoted literals.");
            Assert.That(ContactSqlBuilder.BuildEquivalentKeysSql(), Does.Contain("ON a.[k] = b.[k] AND b.[i] < a.[i] GROUP BY a.[i]"));
        });
    }
}
