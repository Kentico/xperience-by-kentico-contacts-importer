using CMS.ContactManagement;
using CMS.FormEngine;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// The rules that decide which contact fields can be mapping targets and which of them reference another object.
/// The result for the real contact class is checked by the integration tests.
/// </summary>
internal class ContactFieldSchemaProviderTests
{
    private static readonly IReadOnlySet<string> ownedColumns = ContactFieldSchemaProvider.GetOwnedColumns(ContactInfo.TYPEINFO);
    private static readonly IReadOnlyDictionary<string, string> dependencies = ContactFieldSchemaProvider.GetDependencies(ContactInfo.TYPEINFO);
    private static readonly Dictionary<string, ContactFieldReference> noForeignKeys = [];

    private static FormFieldInfo Field(string name, string dataType = "text", bool system = false, bool primaryKey = false, string? referenceTo = null) =>
        new() { Name = name, DataType = dataType, System = system, PrimaryKey = primaryKey, ReferenceToObjectType = referenceTo };

    private static (string TableName, string IdColumn)? Resolve(string objectType) => objectType.ToLowerInvariant() switch
    {
        "cms.country" => ("CMS_Country", "CountryID"),
        "cms.user" => ("CMS_User", "UserID"),
        _ => null,
    };

    [Test]
    public void Importer_Owns_The_Identity_And_Timestamp_Columns_Only() =>
        Assert.That(ownedColumns, Is.EquivalentTo(new[] { "ContactID", "ContactGUID", "ContactCreated", "ContactLastModified" }));

    [Test]
    public void Contact_Type_Declares_Its_References() => Assert.Multiple(() =>
    {
        Assert.That(dependencies.Keys, Is.EquivalentTo(new[] { "ContactStateID", "ContactCountryID", "ContactStatusID", "ContactOwnerUserID" }));
        Assert.That(dependencies["contactcountryid"], Is.EqualTo("cms.country").IgnoreCase);
        Assert.That(dependencies["ContactOwnerUserID"], Is.EqualTo("cms.user").IgnoreCase);
    });

    [TestCase("ContactEmail")]
    [TestCase("ContactFirstName")]
    [TestCase("ContactNotes")]
    [TestCase("ContactGender")]
    [TestCase("ContactCampaign")]
    [TestCase("ContactMonitored")]
    [TestCase("ContactStatusID")]
    [TestCase("ContactStateID")]
    [TestCase("ContactCountryID")]
    [TestCase("ContactOwnerUserID")]
    [TestCase("ContactBounces")]
    [TestCase("ContactSalesForceLeadID")]
    [TestCase("ContactSalesForceLeadReplicationDisabled")]
    [TestCase("ContactSalesForceLeadReplicationDateTime")]
    [TestCase("ContactSalesForceLeadReplicationSuspensionDateTime")]
    [TestCase("ContactSalesForceLeadReplicationRequired")]
    public void System_Fields_Are_Mappable(string name) =>
        Assert.That(ContactFieldSchemaProvider.IsMappable(Field(name, system: true), ownedColumns), Is.True);

    [TestCase("ContactID")]
    [TestCase("ContactGUID")]
    [TestCase("ContactCreated")]
    [TestCase("ContactLastModified")]
    [TestCase("contactlastMODIFIED")]
    public void Fields_The_Importer_Owns_Are_Not_Mappable(string name) =>
        Assert.That(ContactFieldSchemaProvider.IsMappable(Field(name, system: true), ownedColumns), Is.False);

    [Test]
    public void Custom_Fields_Are_Mappable_Except_A_Primary_Key() => Assert.Multiple(() =>
    {
        Assert.That(ContactFieldSchemaProvider.IsMappable(Field("CustomerCode"), ownedColumns), Is.True);
        Assert.That(ContactFieldSchemaProvider.IsMappable(Field("CustomerNumber", "integer"), ownedColumns), Is.True);
        Assert.That(ContactFieldSchemaProvider.IsMappable(Field("AccountManagerID", "integer", referenceTo: "cms.user"), ownedColumns), Is.True);
        Assert.That(ContactFieldSchemaProvider.IsMappable(Field("CustomKey", "integer", primaryKey: true), ownedColumns), Is.False);
    });

    [Test]
    public void Plain_Field_Is_Not_A_Reference() =>
        Assert.That(ContactFieldSchemaProvider.GetReference(Field("ContactGender", "integer", system: true), dependencies, noForeignKeys, Resolve), Is.Null);

    [Test]
    public void Dependency_Of_The_Contact_Type_Is_A_Reference_To_The_Table_Of_Its_Object_Type()
    {
        var reference = ContactFieldSchemaProvider.GetReference(Field("ContactCountryID", "integer", system: true), dependencies, noForeignKeys, Resolve);

        Assert.Multiple(() =>
        {
            Assert.That(reference!.ObjectType, Is.EqualTo("cms.country").IgnoreCase);
            Assert.That((reference.TableName, reference.ColumnName, reference.SchemaName), Is.EqualTo(("CMS_Country", "CountryID", (string?)null)));
            Assert.That(reference.CanBeChecked, Is.True);
        });
    }

    [Test]
    public void Reference_Setting_Of_A_Custom_Field_Is_A_Reference()
    {
        var reference = ContactFieldSchemaProvider.GetReference(Field("AccountManagerID", "integer", referenceTo: "cms.user"), dependencies, noForeignKeys, Resolve);

        Assert.That(reference, Is.EqualTo(new ContactFieldReference("cms.user", "CMS_User", "UserID")));
    }

    [Test]
    public void Foreign_Key_Decides_The_Checked_Table_And_Marks_Fields_The_Class_Does_Not_Describe()
    {
        var foreignKeys = new Dictionary<string, ContactFieldReference>(StringComparer.OrdinalIgnoreCase)
        {
            ["ContactCountryID"] = new(null, "Custom_Country", "ID", "crm"),
            ["CustomRegionID"] = new(null, "Custom_Region", "RegionID", "dbo"),
            ["CustomRegionCode"] = new(null, "Custom_Region", "RegionCode", "dbo"),
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                ContactFieldSchemaProvider.GetReference(Field("contactcountryid", "integer", system: true), dependencies, foreignKeys, Resolve),
                Is.EqualTo(new ContactFieldReference(dependencies["ContactCountryID"], "Custom_Country", "ID", "crm")),
                "The foreign key is what the database enforces.");
            Assert.That(
                ContactFieldSchemaProvider.GetReference(Field("CustomRegionID", "integer"), dependencies, foreignKeys, Resolve),
                Is.EqualTo(new ContactFieldReference(null, "Custom_Region", "RegionID", "dbo")));
            Assert.That(
                ContactFieldSchemaProvider.GetReference(Field("CustomRegionCode"), dependencies, foreignKeys, Resolve)!.CanBeChecked,
                Is.True,
                "A foreign key is checked for every data type.");
        });
    }

    [Test]
    public void Reference_That_Cannot_Be_Resolved_Is_Flagged_But_Not_Checked() => Assert.Multiple(() =>
    {
        var unknownType = ContactFieldSchemaProvider.GetReference(Field("PersonaID", "integer", referenceTo: "custom.persona"), dependencies, noForeignKeys, Resolve);
        var byCodeName = ContactFieldSchemaProvider.GetReference(Field("CountryCode", "text", referenceTo: "cms.country"), dependencies, noForeignKeys, Resolve);

        Assert.That(unknownType, Is.EqualTo(new ContactFieldReference("custom.persona")));
        Assert.That(unknownType!.CanBeChecked, Is.False);
        Assert.That(byCodeName, Is.EqualTo(new ContactFieldReference("cms.country")), "Without a foreign key only whole number fields hold IDs.");
    });

    [Test]
    public void Definition_Tells_Whether_The_Field_Is_A_Reference() => Assert.Multiple(() =>
    {
        Assert.That(new ContactFieldDefinition("ContactEmail", "Email", "text", 254).IsReference, Is.False);
        Assert.That(new ContactFieldDefinition("ContactCountryID", "Country", "integer", null, null, new ContactFieldReference("cms.country")).IsReference, Is.True);
        Assert.That(new ContactFieldDefinition("ContactCountryID", "Country", "integer", null, null, new ContactFieldReference("cms.country")).CanBeKey, Is.True, "The key rule looks at the data type only.");
    });

    [Test]
    public void Foreign_Key_Query_Is_Parameterized_And_Limited_To_Enabled_Single_Column_Keys() =>
        Assert.That(ContactFieldSchemaProvider.ForeignKeysSql, Does.Contain("OBJECT_ID(@table)").And.Contain("[is_disabled] = 0").And.Contain(") = 1"));
}
