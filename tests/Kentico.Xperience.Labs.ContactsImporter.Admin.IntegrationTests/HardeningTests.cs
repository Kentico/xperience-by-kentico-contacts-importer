using CMS.Helpers;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// Checks against the real contact class, database and CMS.IO: which fields can be mapped, what Xperience's bulk
/// insert does to the cache, the status files over CMS.IO, and a configuration problem found before the import writes.
/// </summary>
public class HardeningTests : ImportIntegrationTest
{
    /// <summary>
    /// Mappable system fields of the contact class of the supported Xperience version. A new Xperience version that
    /// adds a contact field fails this test on purpose: check that an import can write it.
    /// </summary>
    private static readonly string[] mappableSystemFields =
    [
        "ContactFirstName", "ContactMiddleName", "ContactLastName", "ContactBirthday", "ContactGender", "ContactCompanyName",
        "ContactJobTitle", "ContactMonitored", "ContactCampaign", "ContactAddress1", "ContactCity", "ContactZIP",
        "ContactMobilePhone", "ContactBusinessPhone", "ContactEmail", "ContactNotes",
        "ContactStateID", "ContactCountryID", "ContactStatusID", "ContactOwnerUserID", "ContactBounces",
        "ContactSalesForceLeadID", "ContactSalesForceLeadReplicationDisabled", "ContactSalesForceLeadReplicationDateTime",
        "ContactSalesForceLeadReplicationSuspensionDateTime", "ContactSalesForceLeadReplicationRequired",
    ];

    /// <summary>
    /// The only fields of the contact class an import never writes from a file: the importer sets them itself.
    /// </summary>
    private static readonly string[] ownedFields = ["ContactID", "ContactGUID", "ContactCreated", "ContactLastModified"];

    private static readonly string[] customFields =
    [
        IntegrationDatabase.UserField,
        IntegrationDatabase.CodeField, IntegrationDatabase.NumberField, IntegrationDatabase.BigNumberField, IntegrationDatabase.AmountField,
        IntegrationDatabase.ScoreField, IntegrationDatabase.DateField, IntegrationDatabase.FlagField, IntegrationDatabase.GuidField, IntegrationDatabase.SourceField,
    ];

    private sealed class NoCacheInvalidation : IImportCacheInvalidator
    {
        public void InvalidateAfterImport(ImportJob job)
        {
            // Leaves the cache alone, so a test sees what the writes themselves do to it.
        }
    }

    private static string?[][] Rows(int count, string firstName = "Name") =>
        [.. Enumerable.Range(1, count).Select(i => new string?[] { $"user{i}@hardening.test", $"{firstName} {i}" })];

    private static string CacheItemDependingOn(string dependencyKey)
    {
        string key = "contactsimporter|integrationtest|" + Guid.NewGuid().ToString("N");
        CacheHelper.Add(key, "cached", CacheHelper.GetCacheDependency(dependencyKey), DateTimeOffset.Now.AddHours(1), TimeSpan.Zero);
        Assert.That(CacheHelper.TryGetItem(key, out string? _), Is.True, "The item must be cached before the import.");

        return key;
    }

    private static bool IsCached(string key) => CacheHelper.TryGetItem(key, out string? _);

    [Test]
    public void Mappable_Fields_Of_The_Real_Contact_Class_Are_All_Fields_Except_The_Ones_The_Importer_Owns()
    {
        var fields = new ContactFieldSchemaProvider().GetMappableFields();
        string[] names = [.. fields.Select(f => f.Name)];
        TestContext.Out.WriteLine("Mappable contact fields: " + string.Join(", ", fields.Select(f => $"{f.Name} ({f.DataType}{(f.CanBeKey ? ", key" : "")})")));

        Assert.Multiple(() =>
        {
            Assert.That(names.Except(customFields), Is.EquivalentTo(mappableSystemFields));
            Assert.That(names, Is.SupersetOf(customFields));
            Assert.That(fields.Where(f => !f.IsSupported).Select(f => f.Name), Is.Empty, "Every listed field of the default class has a type the import can write.");
            Assert.That(fields.Where(f => f.CanBeKey).Select(f => f.Name), Does.Contain(Email).And.Contain(Code).And.Contain(IntegrationDatabase.NumberField).And.Contain(IntegrationDatabase.GuidField));
            Assert.That(fields.Where(f => f.CanBeKey).Select(f => f.Name), Has.None.AnyOf("ContactNotes", "ContactBirthday", "ContactMonitored", IntegrationDatabase.AmountField, IntegrationDatabase.DateField));
            Assert.That(names, Has.None.AnyOf(ownedFields));
        });
    }

    [Test]
    public void Every_Column_Of_The_Contact_Table_Is_Mappable_Or_Owned_By_The_Importer_And_None_Is_Unwritable()
    {
        // Independent of the class definition the provider reads: the columns of the table itself.
        var columns = IntegrationDatabase.Query(
            """
            SELECT c.name AS ColumnName, CAST(CASE WHEN c.is_identity = 1 OR c.is_computed = 1 OR t.name IN ('timestamp', 'rowversion') THEN 1 ELSE 0 END AS bit) AS Unwritable
            FROM sys.columns c INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('OM_Contact')
            """);
        string[] all = [.. columns.Select(row => (string)row["ColumnName"]!)];
        string[] unwritable = [.. columns.Where(row => (bool)row["Unwritable"]!).Select(row => (string)row["ColumnName"]!)];
        string[] mappable = [.. new ContactFieldSchemaProvider().GetMappableFields().Select(f => f.Name)];

        Assert.Multiple(() =>
        {
            Assert.That(unwritable, Is.EqualTo(new[] { "ContactID" }), "Identity, computed and rowversion columns cannot be written.");
            Assert.That(mappable.Intersect(unwritable, StringComparer.OrdinalIgnoreCase), Is.Empty);
            Assert.That(all.Except(mappable, StringComparer.OrdinalIgnoreCase), Is.EquivalentTo(ownedFields));
            Assert.That(mappable.Except(all, StringComparer.OrdinalIgnoreCase), Is.Empty);
        });
    }

    [Test]
    public void Reference_Fields_Are_The_Foreign_Keys_Of_The_Table_And_The_References_Of_The_Class()
    {
        // Independent of the provider's own query: every foreign key of the table with its target.
        var foreignKeys = IntegrationDatabase.Query(
            """
            SELECT pc.name AS ColumnName, OBJECT_NAME(fkc.referenced_object_id) AS TableName, rc.name AS ReferencedColumn
            FROM sys.foreign_key_columns fkc
            INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fkc.parent_object_id = OBJECT_ID('OM_Contact')
            """).ToDictionary(row => (string)row["ColumnName"]!, row => ((string)row["TableName"]!, (string)row["ReferencedColumn"]!));
        var fields = new ContactFieldSchemaProvider().GetMappableFields();
        var references = fields.Where(f => f.IsReference).ToDictionary(f => f.Name, f => f.Reference!);
        TestContext.Out.WriteLine("Reference fields: " + string.Join(", ", references.Select(r => $"{r.Key} -> {r.Value.ObjectType ?? "?"} [{r.Value.SchemaName}].[{r.Value.TableName}].[{r.Value.ColumnName}]")));

        Assert.Multiple(() =>
        {
            Assert.That(foreignKeys, Is.EquivalentTo(new Dictionary<string, (string, string)>
            {
                ["ContactStateID"] = ("CMS_State", "StateID"),
                ["ContactCountryID"] = ("CMS_Country", "CountryID"),
                ["ContactStatusID"] = ("OM_ContactStatus", "ContactStatusID"),
                ["ContactOwnerUserID"] = ("CMS_User", "UserID"),
            }), "The foreign keys of the contact table of the supported Xperience version.");
            Assert.That(references.Keys, Is.EquivalentTo(foreignKeys.Keys.Append(IntegrationDatabase.UserField)));
            foreach (var (column, target) in foreignKeys)
            {
                Assert.That((references[column].TableName, references[column].ColumnName), Is.EqualTo(target), column);
                Assert.That(references[column].SchemaName, Is.EqualTo("dbo"), column);
                Assert.That(references[column].ObjectType, Is.Not.Null.And.Not.Empty, $"{column}: the contact type declares the dependency.");
            }

            // No foreign key: resolved from the object type of the field settings through the Xperience type information.
            Assert.That(references[IntegrationDatabase.UserField], Is.EqualTo(new ContactFieldReference(CMS.Membership.UserInfo.OBJECT_TYPE, "CMS_User", "UserID")));
            Assert.That(references["ContactCountryID"].ObjectType, Is.EqualTo("cms.country").IgnoreCase);
            Assert.That(references["ContactStatusID"].ObjectType, Is.EqualTo("om.contactstatus").IgnoreCase);
            Assert.That(references.Values.Select(r => r.CanBeChecked), Is.All.True);
        });
    }

    [Test]
    public void Job_Mapping_Reference_And_System_Fields_Is_Accepted_By_The_Validator_And_The_Upload()
    {
        var fields = new ContactFieldSchemaProvider().GetMappableFields();
        var job = TestData.Job(null, Email, Email, "ContactStatusID", "ContactCountryID", "ContactBounces", "ContactSalesForceLeadID") with
        {
            File = new ImportFileDescriptor { Name = "contacts.csv", Size = 10, Fingerprint = "0a" },
        };
        var owned = TestData.Job(null, Email, Email, "ContactGUID") with { File = job.File };

        var errors = ImportJobConfigValidator.Validate(job, fields, new ContactsImporterOptions());
        bool uploadable = ChunkValidationContext.TryCreate(job, fields, out _, out string? uploadError);
        var ownedErrors = ImportJobConfigValidator.Validate(owned, fields, new ContactsImporterOptions());

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(uploadable, Is.True, uploadError);
            Assert.That(ownedErrors, Is.EqualTo(new[] { ImportMessages.FieldNotImportable("ContactGUID") }));
        });
    }

    [Test]
    public void Bulk_Insert_Of_New_Contacts_Touches_The_Contact_All_Key_With_Every_Batch()
    {
        // What Xperience's BulkInsert does on its own, without the importer's cache clearing: every batch that creates
        // contacts touches "om.contact|all" right away, also in an import that is later cancelled.
        using var harness = new ImportHarness(
            options =>
            {
                // One batch per call.
                options.BatchSize = 2;
                options.ProcessingTimeBudgetSeconds = 0;
            },
            cacheInvalidator: new NoCacheInvalidation());
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(4));
        string cached = CacheItemDependingOn("om.contact|all");

        harness.Run(jobId, ImportPhase.Analyze);
        bool cachedAfterAnalysis = IsCached(cached);
        harness.Processor.Confirm(jobId);
        var first = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        bool cachedAfterFirstBatch = IsCached(cached);
        string cachedAgain = CacheItemDependingOn("om.contact|all");
        var cancel = harness.Processor.Cancel(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(cachedAfterAnalysis, Is.True, "The analysis writes nothing.");
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(first.Aggregates.Created, Is.EqualTo(2));
            Assert.That(cachedAfterFirstBatch, Is.False, "BulkInsert touched om.contact|all while the import was running.");
            Assert.That(cancel.Outcome, Is.EqualTo(ImportCancelOutcome.Cancelled));
            Assert.That(IsCached(cachedAgain), Is.True, "Cancelling touches nothing.");
            Assert.That(ContactCount(), Is.EqualTo(2));
        });
    }

    [Test]
    public void Cache_Items_That_Depend_On_One_Contact_Only_Are_Not_Dropped_By_An_Import()
    {
        // The documented limit of clearing by type keys (PLAN §13): neither BulkInsert nor the cache clearing after the
        // import touches per-contact keys.
        int existing = Seed("user1@hardening.test", "Old");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(3));
        string byId = CacheItemDependingOn($"om.contact|byid|{existing}");
        string all = CacheItemDependingOn("om.contact|all");

        var import = harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(import.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(import.Aggregates.Created, Is.EqualTo(2));
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("Name 1"));
            Assert.That(IsCached(all), Is.False);
            Assert.That(IsCached(byId), Is.True);
        });
    }

    [Test]
    public void Configuration_Problem_Found_When_The_Analysis_Starts_Keeps_The_Upload_For_A_Corrected_Run()
    {
        // The source field is a number field: the engine refuses the job before it reads a contact.
        var wrong = new ImportOptions { SourceField = IntegrationDatabase.NumberField, SourceValue = "import-1" };
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(3), wrong);

        var rejected = harness.Run(jobId, ImportPhase.Analyze);
        var status = harness.Store.ReadStatus(jobId)!;
        bool chunksKept = harness.Store.HasChunks(jobId);

        var job = harness.Store.ReadJob(jobId)!;
        harness.Store.WriteJob(job with { Options = wrong with { SourceField = IntegrationDatabase.SourceField } });
        var import = harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(rejected.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(status.ErrorMessage, Is.EqualTo(ImportMessages.SourceFieldNotText));
            Assert.That(chunksKept, Is.True);
            Assert.That(import.Aggregates.Created, Is.EqualTo(3));
            Assert.That(Contacts().Select(c => c[IntegrationDatabase.SourceField]), Is.All.EqualTo("import-1"));
            Assert.That(harness.Store.ReadStatus(jobId)!.Error, Is.Null);
        });
    }

    [Test]
    public void Status_Updates_And_Chunk_Sizes_Work_Over_CmsIo_For_Two_Stores_On_One_Folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "contacts-importer-tests", "cmsio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Options.Create(new ContactsImporterOptions { StoragePath = root });
            var instanceA = new ImportJobStore(new CmsIoImportFileSystem(), settings);
            var instanceB = new ImportJobStore(new CmsIoImportFileSystem(), settings);
            var job = TestData.Job();
            instanceA.CreateJob(job);
            instanceA.WriteChunk(job.JobId, new ImportChunkInfo { Index = 0, Sha256 = "00", Rows = 1, FirstRow = 1, LastRow = 1 }, new byte[7]);
            instanceA.WriteChunk(job.JobId, new ImportChunkInfo { Index = 1, Sha256 = "00", Rows = 1, FirstRow = 2, LastRow = 2 }, new byte[5]);

            var running = instanceA.UpdateStatus(job.JobId, s => s with { State = ImportJobState.Running, DriverToken = "a" })!;
            var checkpoint = instanceB.UpdateStatus(job.JobId, s => s with { Checkpoint = s.Checkpoint with { RowOffset = 1 } })!;
            byte[] stale = File.ReadAllBytes(Path.Combine(root, job.JobId.ToString("D"), ImportJobStore.StatusFileName));
            var cancelled = instanceB.UpdateStatus(job.JobId, s => ImportJobStateMachine.Transition(s, ImportJobState.Cancelled, DateTime.UtcNow))!;
            File.WriteAllBytes(Path.Combine(root, job.JobId.ToString("D"), ImportJobStore.StatusFileName), stale);
            var afterStaleWrite = instanceA.ReadStatus(job.JobId)!;
            var refused = instanceA.UpdateStatus(job.JobId, s => s.State == ImportJobState.Running ? s with { Checkpoint = s.Checkpoint with { RowOffset = 2 } } : s)!;

            Assert.Multiple(() =>
            {
                Assert.That(running.Version, Is.EqualTo(1));
                Assert.That(checkpoint.Version, Is.EqualTo(2));
                Assert.That(checkpoint.DriverToken, Is.EqualTo("a"));
                Assert.That(cancelled.Version, Is.EqualTo(3));
                Assert.That(File.Exists(Path.Combine(root, job.JobId.ToString("D"), ImportJobStore.FinalStatusFileName)), Is.True);
                Assert.That(afterStaleWrite.State, Is.EqualTo(ImportJobState.Cancelled));
                Assert.That(refused.State, Is.EqualTo(ImportJobState.Cancelled));
                Assert.That(refused.Checkpoint.RowOffset, Is.EqualTo(1));
                Assert.That(instanceB.ReadStatus(job.JobId)!.State, Is.EqualTo(ImportJobState.Cancelled));
                Assert.That(instanceB.GetChunksSize(job.JobId), Is.EqualTo(12));
                Assert.That(instanceB.GetChunksSize(job.JobId, exceptIndex: 0), Is.EqualTo(5));
                Assert.That(instanceB.ListReports(job.JobId), Is.Empty);
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
