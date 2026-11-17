using CMS.ContactManagement;
using CMS.DataEngine;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// Imports into the fields that reference other objects (country, state, contact status, owner, a custom user
/// reference) and into the system fields that became mappable again, against the real tables and foreign keys.
/// </summary>
public class ReferenceFieldsTests : ImportIntegrationTest
{
    private const string Country = "ContactCountryID";
    private const string State = "ContactStateID";
    private const string Status = "ContactStatusID";
    private const string Owner = "ContactOwnerUserID";
    private const string StatusNamePrefix = "ImporterTestStatus";

    // No table of the test database has rows with IDs this high.
    private const string MissingId = "987654321";

    private int countryId;
    private int otherCountryId;
    private int stateId;
    private int userId;

    /// <summary>
    /// The real repository with a point where a test can run code right before the write of a batch.
    /// </summary>
    private sealed class RepositoryWithWriteHook : IContactImportRepository
    {
        private readonly ContactImportRepository inner = new(Provider<ContactInfo>.Instance);

        public Action? BeforeWrite { get; set; }

        public int Writes { get; private set; }

        public IReadOnlyList<ContactMatch> FindMatches(ImportProcessingContext context, IReadOnlyList<object> keys) => inner.FindMatches(context, keys);

        public IReadOnlyList<(int Index, int EquivalentIndex)> FindEquivalentKeys(IReadOnlyList<string> keys) => inner.FindEquivalentKeys(keys);

        public IReadOnlyList<int> FindMissingReferences(ImportField field, IReadOnlyList<object> values) => inner.FindMissingReferences(field, values);

        public IReadOnlyList<AudienceState> FindAudienceStates(ImportProcessingContext context, IReadOnlyList<AudienceTarget> targets, IReadOnlyList<int>? createdContactIds = null, bool usePlannedEmails = true) =>
            inner.FindAudienceStates(context, targets, createdContactIds, usePlannedEmails);

        public AudienceResult? Write(ImportProcessingContext context, IReadOnlyList<ContactCreate> creates, IReadOnlyList<ContactUpdate> updates, DateTime now, AudienceWrite? audience = null)
        {
            Writes++;
            var hook = BeforeWrite;
            BeforeWrite = null;
            hook?.Invoke();

            return inner.Write(context, creates, updates, now, audience);
        }

        public IDisposable? TryLockJob(Guid jobId, TimeSpan timeout) => inner.TryLockJob(jobId, timeout);
    }

    [SetUp]
    public void ReadReferencedRows()
    {
        DeleteTestStatuses();
        var countries = IntegrationDatabase.Query("SELECT TOP 2 [CountryID] FROM [CMS_Country] ORDER BY [CountryID]");
        countryId = (int)countries[0]["CountryID"]!;
        otherCountryId = (int)countries[1]["CountryID"]!;
        stateId = IntegrationDatabase.Scalar<int>("SELECT TOP 1 [StateID] FROM [CMS_State] ORDER BY [StateID]");
        userId = IntegrationDatabase.Scalar<int>("SELECT TOP 1 [UserID] FROM [CMS_User] ORDER BY [UserID]");
    }

    [TearDown]
    public void DeleteTestStatuses()
    {
        // Contacts first: they reference the statuses.
        IntegrationDatabase.DeleteContacts();
        IntegrationDatabase.Execute($"DELETE FROM [OM_ContactStatus] WHERE [ContactStatusName] LIKE '{StatusNamePrefix}%'");
    }

    private static int CreateStatus(string suffix) =>
        IntegrationDatabase.Scalar<int>(
            """
            INSERT INTO [OM_ContactStatus] ([ContactStatusName], [ContactStatusDisplayName]) VALUES (@name, @name);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            ("@name", StatusNamePrefix + suffix));

    private static void DeleteStatus(int statusId) => IntegrationDatabase.Execute("DELETE FROM [OM_ContactStatus] WHERE [ContactStatusID] = @id", ("@id", statusId));

    private static string Text(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertAddsUp(ImportAggregates totals) =>
        Assert.That(totals.Created + totals.Updated + totals.Unchanged + totals.Skipped + totals.DuplicateRows, Is.EqualTo(totals.Rows), "Every row has exactly one outcome.");

    [Test]
    public void Creates_And_Updates_Contacts_With_Valid_References()
    {
        int statusId = CreateStatus("A");
        int otherStatusId = CreateStatus("B");
        int existing = Seed("old@reference.test", "Old");
        IntegrationDatabase.Execute("UPDATE [OM_Contact] SET [ContactCountryID] = @country, [ContactStatusID] = @status WHERE [ContactID] = @id", ("@country", otherCountryId), ("@status", otherStatusId), ("@id", existing));
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, [Email, FirstName, Country, State, Status, Owner, IntegrationDatabase.UserField],
        [
            ["new@reference.test", "New", Text(countryId), Text(stateId), Text(statusId), Text(userId), Text(userId)],
            ["old@reference.test", "", Text(countryId), "", Text(statusId), Text(userId), ""],
            ["plain@reference.test", "Plain", "", "", "", "", null],
        ]);

        var import = harness.Import(jobId);

        var created = ContactByEmail("new@reference.test");
        var updated = ContactById(existing);
        var plain = ContactByEmail("plain@reference.test");
        Assert.Multiple(() =>
        {
            Assert.That(import.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(import.Aggregates.Created, Is.EqualTo(2));
            Assert.That(import.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(import.Aggregates.Skipped, Is.Zero);
            AssertAddsUp(import.Aggregates);

            Assert.That(created[Country], Is.EqualTo(countryId));
            Assert.That(created[State], Is.EqualTo(stateId));
            Assert.That(created[Status], Is.EqualTo(statusId));
            Assert.That(created[Owner], Is.EqualTo(userId));
            Assert.That(created[IntegrationDatabase.UserField], Is.EqualTo(userId));

            Assert.That(updated[FirstName], Is.EqualTo("Old"), "An empty value keeps the field.");
            Assert.That(updated[Country], Is.EqualTo(countryId));
            Assert.That(updated[Status], Is.EqualTo(statusId));
            Assert.That(updated[Owner], Is.EqualTo(userId));
            Assert.That(updated[State], Is.Null);

            Assert.That(plain[Country], Is.Null);
            Assert.That(plain[Status], Is.Null);
            Assert.That(plain[Owner], Is.Null);
        });
    }

    [Test]
    public void Empty_Reference_Clears_The_Field_When_Empty_Values_Clear()
    {
        int statusId = CreateStatus("A");
        int existing = Seed("old@reference.test", "Old");
        IntegrationDatabase.Execute("UPDATE [OM_Contact] SET [ContactCountryID] = @country, [ContactStatusID] = @status WHERE [ContactID] = @id", ("@country", countryId), ("@status", statusId), ("@id", existing));
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, [Email, Country, Status], [["old@reference.test", "", null]], new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear });

        var import = harness.Import(jobId);

        var updated = ContactById(existing);
        Assert.Multiple(() =>
        {
            Assert.That(import.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(updated[Country], Is.Null);
            Assert.That(updated[Status], Is.EqualTo(statusId), "The row says nothing about the status.");
        });
    }

    [Test]
    public void Rows_With_References_That_Do_Not_Exist_Are_Skipped_And_The_Job_Completes()
    {
        int statusId = CreateStatus("A");
        int existing = Seed("old@reference.test", "Old");
        using var harness = new ImportHarness(options => options.BatchSize = 4);
        var jobId = harness.Upload(Email, [Email, FirstName, Country, State, Status, Owner, IntegrationDatabase.UserField],
        [
            ["good@reference.test", "Good", Text(countryId), "", Text(statusId), "", ""],
            ["country@reference.test", "Bad country", MissingId, "", Text(statusId), "", ""],
            ["status@reference.test", "Bad status", Text(countryId), "", MissingId, "", ""],
            ["old@reference.test", "Changed", "", "", "", MissingId, ""],
            ["state@reference.test", "Bad state", "", MissingId, "", "", ""],
            ["user@reference.test", "Bad custom user", "", "", "", "", MissingId],
            ["zero@reference.test", "Zero", "0", "", "", "", ""],
            ["fine@reference.test", "Fine", "", "", "", "", Text(userId)],
        ]);

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        Assert.That(analysis.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), analysis.Error?.ToString());
        int contactsAfterAnalysis = ContactCount();
        Assert.That(harness.Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
        var import = harness.Run(jobId, ImportPhase.Execute);

        string[] analyzeReport = harness.ReadReport(jobId, ImportPhase.Analyze);
        string[] executeReport = harness.ReadReport(jobId, ImportPhase.Execute);
        Assert.Multiple(() =>
        {
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), import.Error?.ToString());
            Assert.That(import.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(contactsAfterAnalysis, Is.EqualTo(1));
            Assert.That(import.Aggregates.Rows, Is.EqualTo(8));
            Assert.That(import.Aggregates.Created, Is.EqualTo(2));
            Assert.That(import.Aggregates.Updated, Is.Zero);
            Assert.That(import.Aggregates.SkippedInvalidValue, Is.EqualTo(6));
            Assert.That(import.Aggregates.Skipped, Is.EqualTo(6));
            AssertAddsUp(import.Aggregates);
            Assert.That((analysis.Aggregates.Rows, analysis.Aggregates.Created, analysis.Aggregates.Updated, analysis.Aggregates.SkippedInvalidValue),
                Is.EqualTo((import.Aggregates.Rows, import.Aggregates.Created, import.Aggregates.Updated, import.Aggregates.SkippedInvalidValue)), "The dry run counts what the import does.");
            Assert.That(executeReport, Is.EqualTo(analyzeReport));
            Assert.That(executeReport, Is.SupersetOf(new[]
            {
                $"2,skipped,invalidValue,{Country}",
                $"3,skipped,invalidValue,{Status}",
                $"4,skipped,invalidValue,{Owner}",
                $"5,skipped,invalidValue,{State}",
                $"6,skipped,invalidValue,{IntegrationDatabase.UserField}",
                $"7,skipped,invalidValue,{Country}",
            }));

            Assert.That(Contacts().Select(c => c[Email]), Is.EquivalentTo(new[] { "old@reference.test", "good@reference.test", "fine@reference.test" }));
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("Old"), "Nothing of a skipped row is imported.");
            Assert.That(ContactByEmail("good@reference.test")[Status], Is.EqualTo(statusId));
            Assert.That(ContactByEmail("fine@reference.test")[IntegrationDatabase.UserField], Is.EqualTo(userId));
            Assert.That(string.Join('\n', harness.Log), Does.Not.Contain("CONTACT_IMPORT_FAILURE").And.Not.Contain("CONTACT_IMPORT_RETRY").And.Not.Contain("reference.test"));
        });
    }

    [Test]
    public void System_Fields_That_Xperience_Maintains_Are_Written_On_Create_And_Update()
    {
        int existing = Seed("old@system.test", "Old");
        using var harness = new ImportHarness();
        string[] targets =
        [
            Email, "ContactBounces", "ContactSalesForceLeadID", "ContactSalesForceLeadReplicationDisabled", "ContactSalesForceLeadReplicationDateTime",
            "ContactSalesForceLeadReplicationSuspensionDateTime", "ContactSalesForceLeadReplicationRequired", "ContactMonitored", "ContactCampaign", "ContactGender",
        ];
        var jobId = harness.Upload(Email, targets,
        [
            ["new@system.test", "3", "00Q5g00000ABCDEF", "true", "2025-03-04T05:06:07", "2025-04-05T06:07:08", "false", "true", "spring", "2"],
            ["old@system.test", "7", "00Q5g00000ZYXWVU", "false", "2024-01-02T03:04:05", "", "true", "false", "autumn", "1"],
        ]);

        var import = harness.Import(jobId);

        var created = ContactByEmail("new@system.test");
        var updated = ContactById(existing);
        Assert.Multiple(() =>
        {
            Assert.That(import.Aggregates.Created, Is.EqualTo(1));
            Assert.That(import.Aggregates.Updated, Is.EqualTo(1));

            Assert.That(created["ContactBounces"], Is.EqualTo(3));
            Assert.That(created["ContactSalesForceLeadID"], Is.EqualTo("00Q5g00000ABCDEF"));
            Assert.That(created["ContactSalesForceLeadReplicationDisabled"], Is.EqualTo(true));
            Assert.That(created["ContactSalesForceLeadReplicationDateTime"], Is.EqualTo(new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified)));
            Assert.That(created["ContactSalesForceLeadReplicationSuspensionDateTime"], Is.EqualTo(new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Unspecified)));
            Assert.That(created["ContactSalesForceLeadReplicationRequired"], Is.EqualTo(false));
            Assert.That(created["ContactMonitored"], Is.EqualTo(true));
            Assert.That(created["ContactCampaign"], Is.EqualTo("spring"));
            Assert.That(created["ContactGender"], Is.EqualTo(2));

            Assert.That(updated["ContactBounces"], Is.EqualTo(7));
            Assert.That(updated["ContactSalesForceLeadID"], Is.EqualTo("00Q5g00000ZYXWVU"));
            Assert.That(updated["ContactSalesForceLeadReplicationDisabled"], Is.EqualTo(false));
            Assert.That(updated["ContactSalesForceLeadReplicationDateTime"], Is.EqualTo(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)));
            Assert.That(updated["ContactSalesForceLeadReplicationSuspensionDateTime"], Is.Null);
            Assert.That(updated["ContactSalesForceLeadReplicationRequired"], Is.EqualTo(true));
            Assert.That(updated["ContactMonitored"], Is.EqualTo(false));
            Assert.That(updated["ContactCampaign"], Is.EqualTo("autumn"));
            Assert.That(updated["ContactGender"], Is.EqualTo(1));
            Assert.That(updated[FirstName], Is.EqualTo("Old"));
        });
    }

    [Test]
    public void Job_Mapping_Every_Mappable_Field_With_Values_Imports()
    {
        // Every field of the class with a value of its type, in one job: create, then update with other values.
        int statusId = CreateStatus("A");
        var fields = new ContactFieldSchemaProvider().GetMappableFields();
        string[] targets = [.. fields.Select(f => f.Name)];

        string?[] Row(int variant) => [.. fields.Select(f => f.Name switch
        {
            Email => "all@fields.test",
            Country => Text(variant == 0 ? countryId : otherCountryId),
            State => Text(stateId),
            Status => Text(statusId),
            Owner or IntegrationDatabase.UserField => Text(userId),
            _ => f.DataType.ToLowerInvariant() switch
            {
                "text" or "longtext" => "v" + variant,
                "integer" or "longinteger" => Text(variant + 1),
                "decimal" or "double" => variant + ".5",
                "datetime" => $"2025-01-0{variant + 1}T10:00:00",
                "date" => $"2025-01-0{variant + 1}",
                "boolean" => variant == 0 ? "true" : "false",
                "guid" => variant == 0 ? "6f9619ff-8b86-d011-b42d-00c04fc964ff" : "7f9619ff-8b86-d011-b42d-00c04fc964ff",
                _ => throw new InvalidOperationException($"No test value for data type '{f.DataType}' of {f.Name}."),
            },
        })];

        using var harness = new ImportHarness();
        var created = harness.Import(harness.Upload(Email, targets, [Row(0)]));
        var afterCreate = ContactByEmail("all@fields.test");
        var updated = harness.Import(harness.Upload(Email, targets, [Row(1)]));
        var afterUpdate = ContactByEmail("all@fields.test");

        Assert.Multiple(() =>
        {
            Assert.That(created.Aggregates.Created, Is.EqualTo(1));
            Assert.That(updated.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(ContactCount(), Is.EqualTo(1));
            foreach (string target in targets)
            {
                Assert.That(afterCreate[target], Is.Not.Null, $"{target} after the create.");
                Assert.That(afterUpdate[target], Is.Not.Null, $"{target} after the update.");
            }

            Assert.That(afterCreate[Country], Is.EqualTo(countryId));
            Assert.That(afterUpdate[Country], Is.EqualTo(otherCountryId));
            Assert.That(afterUpdate["ContactBounces"], Is.EqualTo(2));
            Assert.That(updated.Aggregates.Fields.Select(change => change.Field), Is.SupersetOf(new[] { Country, "ContactBounces", "ContactSalesForceLeadID", "ContactMonitored" }));
        });
    }

    [Test]
    public void Referenced_Object_Deleted_Between_Check_And_Write_Skips_Its_Rows_Instead_Of_Failing_The_Job()
    {
        int statusId = CreateStatus("A");
        int keptStatusId = CreateStatus("B");
        int existing = Seed("old@race.test", "Old");
        var repository = new RepositoryWithWriteHook
        {
            // After the batch passed the reference check, before it is written: the real foreign key refuses the insert.
            BeforeWrite = () => DeleteStatus(statusId),
        };
        using var harness = new ImportHarness(repository: repository);
        var jobId = harness.Upload(Email, [Email, FirstName, Status],
        [
            ["a@race.test", "A", Text(statusId)],
            ["old@race.test", "Changed", ""],
            ["b@race.test", "B", Text(keptStatusId)],
            ["c@race.test", "C", Text(statusId)],
        ]);

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        Assert.That(analysis.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), analysis.Error?.ToString());
        Assert.That(harness.Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
        var import = harness.Run(jobId, ImportPhase.Execute);

        string[] report = harness.ReadReport(jobId, ImportPhase.Execute);
        Assert.Multiple(() =>
        {
            Assert.That(analysis.Aggregates.Created, Is.EqualTo(3), "The status existed during the analysis.");
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), import.Error?.ToString());
            Assert.That(import.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(repository.Writes, Is.EqualTo(2), "The refused write and its repetition.");
            Assert.That(import.Aggregates.Created, Is.EqualTo(1));
            Assert.That(import.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(import.Aggregates.SkippedInvalidValue, Is.EqualTo(2));
            AssertAddsUp(import.Aggregates);
            Assert.That(report, Is.SupersetOf(new[] { $"1,skipped,invalidValue,{Status}", $"4,skipped,invalidValue,{Status}" }));
            Assert.That(Contacts().Select(c => c[Email]), Is.EquivalentTo(new[] { "old@race.test", "b@race.test" }), "The refused write left nothing behind.");
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("Changed"));
            Assert.That(ContactByEmail("b@race.test")[Status], Is.EqualTo(keptStatusId));
            Assert.That(string.Join('\n', harness.Log), Does.Contain("CONTACT_IMPORT_RETRY").And.Contain("SQL error 547").And.Not.Contain("CONTACT_IMPORT_FAILURE").And.Not.Contain("race.test"));
        });
    }

    [Test]
    public void Missing_References_Query_Returns_The_Positions_Of_Values_Without_A_Row()
    {
        var fields = new ContactFieldSchemaProvider().GetMappableFields();
        var job = Tests.TestData.Job(null, Email, Email, Country, IntegrationDatabase.UserField);
        Assert.That(ImportProcessingContext.TryCreate(job, fields, out var context, out var error), Is.True, error?.ToString());
        var repository = new ContactImportRepository(Provider<ContactInfo>.Instance);

        var missingCountries = repository.FindMissingReferences(context!.Fields[1], [countryId, 987654321, otherCountryId, -5, 0]);
        var missingUsers = repository.FindMissingReferences(context.Fields[2], [987654321, userId]);

        Assert.Multiple(() =>
        {
            Assert.That(context.ReferenceGroups, Has.Count.EqualTo(2));
            Assert.That(missingCountries, Is.EquivalentTo(new[] { 1, 3, 4 }));
            Assert.That(missingUsers, Is.EqualTo(new[] { 0 }));
            Assert.That(repository.FindMissingReferences(context.Fields[1], []), Is.Empty);
        });
    }
}
