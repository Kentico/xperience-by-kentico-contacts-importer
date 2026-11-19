using CMS.ContactManagement;
using CMS.DataEngine;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

public class SchemaAndRepositoryTests : ImportIntegrationTest
{
    [Test]
    public void Schema_Provider_Returns_System_And_Custom_Fields_Of_The_Real_Contact_Class()
    {
        var fields = new ContactFieldSchemaProvider().GetMappableFields().ToDictionary(f => f.Name);

        Assert.Multiple(() =>
        {
            Assert.That(fields[Email], Is.EqualTo(new ContactFieldDefinition(Email, fields[Email].Caption, "text", 254)));
            Assert.That(fields[FirstName].DataType, Is.EqualTo("text"));
            Assert.That(fields[FirstName].MaxLength, Is.EqualTo(100));
            Assert.That(fields["ContactNotes"].DataType, Is.EqualTo("longtext"));
            Assert.That(fields["ContactNotes"].MaxLength, Is.Null);
            Assert.That(fields["ContactBirthday"].DataType, Is.EqualTo("datetime"));
            Assert.That(fields[Email].Caption, Is.Not.Empty);

            Assert.That(fields[Code], Is.EqualTo(new ContactFieldDefinition(Code, Code, "text", 100)));
            Assert.That(fields[IntegrationDatabase.NumberField].DataType, Is.EqualTo("integer"));
            Assert.That(fields[IntegrationDatabase.AmountField], Is.EqualTo(new ContactFieldDefinition(IntegrationDatabase.AmountField, IntegrationDatabase.AmountField, "decimal", 10, 2)));
            Assert.That(fields[IntegrationDatabase.GuidField].DataType, Is.EqualTo("guid"));

            Assert.That(fields.Keys, Has.None.AnyOf(ContactFieldSchemaProvider.ExcludedFields));
        });
    }

    [Test]
    public void Every_Mappable_Field_Is_A_Column_Of_The_Contact_Table_With_A_Type_The_Importer_Knows()
    {
        var columns = IntegrationDatabase.Query("SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'OM_Contact'")
            .ToDictionary(c => (string)c["COLUMN_NAME"]!, StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            foreach (var field in new ContactFieldSchemaProvider().GetMappableFields())
            {
                Assert.That(columns, Does.ContainKey(field.Name));
                Assert.That(ImportField.GetKind(field.DataType), Is.Not.Null, $"{field.Name} has data type '{field.DataType}'.");
                if (field.IsText && field.MaxLength is { } maxLength)
                {
                    Assert.That(columns[field.Name]["CHARACTER_MAXIMUM_LENGTH"], Is.EqualTo(maxLength), field.Name);
                }
            }
        });
    }

    [Test]
    public void A_Job_Mapping_Every_Mappable_Field_Builds_Valid_Lookup_And_Update_Sql()
    {
        // Guards the raw SQL against schema changes of new Xperience versions: all identifiers and declared types must be accepted.
        int id = Seed("all@fields.test", "Old");
        var fields = new ContactFieldSchemaProvider().GetMappableFields();
        var job = TestData.Job(keyField: Email, targets: [.. fields.Select(f => f.Name)]);
        Assert.That(ImportProcessingContext.TryCreate(job, fields, out var context, out var error), Is.True, error?.ToString());

        var repository = new ContactImportRepository(Provider<ContactInfo>.Instance);
        var matches = repository.FindMatches(context!, ["all@fields.test"]);

        var states = new FieldValueState[fields.Count];
        Array.Fill(states, FieldValueState.Empty);
        object?[] values = new object?[fields.Count];
        states[context!.FieldIndexes[FirstName]] = FieldValueState.Value;
        values[context.FieldIndexes[FirstName]] = "New";
        repository.Write(context, [], [new ContactUpdate(id, states, values)], DateTime.Now);

        Assert.Multiple(() =>
        {
            Assert.That(matches.Select(m => m.ContactId), Is.EqualTo([id]));
            Assert.That(matches[0].Values[context.FieldIndexes[FirstName]], Is.EqualTo("Old"));
            Assert.That(ContactById(id)[FirstName], Is.EqualTo("New"));
            Assert.That(ContactById(id)[Email], Is.EqualTo("all@fields.test"));
        });
    }

    [Test]
    public void Creates_And_Updates_Of_A_Batch_Are_One_Transaction()
    {
        int id = Seed("old@transaction.test", "Old");
        // The repository is called directly, without the reference check of the import: an unknown status ID makes the
        // update of the batch fail on its foreign key.
        var fields = new ContactFieldSchemaProvider().GetMappableFields();
        var job = TestData.Job(null, Email, Email, FirstName, "ContactStatusID");
        Assert.That(ImportProcessingContext.TryCreate(job, fields, out var context, out var error), Is.True, error?.ToString());
        var repository = new ContactImportRepository(Provider<ContactInfo>.Instance);

        var create = new ContactCreate(["new@transaction.test", "New", null]);
        var update = new ContactUpdate(id, [FieldValueState.Absent, FieldValueState.Value, FieldValueState.Value], [null, "Changed", 987654]);

        Assert.That(() => repository.Write(context!, [create], [update], DateTime.Now), Throws.Exception.With.InnerException.InstanceOf<SqlException>());
        Assert.Multiple(() =>
        {
            Assert.That(ContactCount(), Is.EqualTo(1), "The bulk insert is rolled back with the failed update.");
            Assert.That(ContactById(id)[FirstName], Is.EqualTo("Old"));
        });
    }

    [Test]
    public void Lookup_Uses_The_Email_Index()
    {
        // The key lookup must stay cheap on large tables: the system index on the email column has to exist.
        int indexes = IntegrationDatabase.Scalar<int>(
            """
            SELECT COUNT(*) FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('OM_Contact') AND c.name = 'ContactEmail'
            """);

        Assert.That(indexes, Is.GreaterThan(0));
    }

    [Test]
    public void Job_Store_Works_Over_CmsIo()
    {
        string root = Path.Combine(Path.GetTempPath(), "contacts-importer-tests", "cmsio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ImportJobStore(new CmsIoImportFileSystem(), Options.Create(new ContactsImporterOptions { StoragePath = root }));
            var job = TestData.Job();
            store.CreateJob(job);
            store.WriteChunk(job.JobId, new ImportChunkInfo { Index = 0, Sha256 = "00", Rows = 1, FirstRow = 1, LastRow = 1 }, [1, 2, 3]);
            store.WriteReport(job.JobId, "report-analyze-000000000001.csv.gz", [4, 5]);
            store.WriteReport(job.JobId, "report-analyze-000000000001.csv.gz", [6, 7, 8]);

            using var chunk = store.OpenChunk(job.JobId, 0);
            using var report = store.OpenReport(job.JobId, "report-analyze-000000000001.csv.gz");
            Assert.Multiple(() =>
            {
                Assert.That(store.ListJobs(), Is.EqualTo(new[] { job.JobId }));
                Assert.That(store.ReadJob(job.JobId), Is.Not.Null);
                Assert.That(store.ReadStatus(job.JobId)!.State, Is.EqualTo(ImportJobState.Configuring));
                Assert.That(chunk!.Length, Is.EqualTo(3));
                Assert.That(report!.Length, Is.EqualTo(3));
                Assert.That(store.ListReports(job.JobId), Is.EqualTo(new[] { "report-analyze-000000000001.csv.gz" }));
                Assert.That(() => store.WriteReport(job.JobId, "report-../x", [1]), Throws.ArgumentException);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
