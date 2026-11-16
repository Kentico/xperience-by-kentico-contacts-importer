namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

public class CreateAndUpdateTests : ImportIntegrationTest
{
    [Test]
    public void Creates_Contacts_With_System_Fields_And_Typed_Custom_Values()
    {
        using var harness = new ImportHarness();
        var before = DateTime.Now.AddSeconds(-2);
        var jobId = harness.Upload(
            Email,
            [
                Email, FirstName, "ContactBirthday", "ContactNotes", Code, IntegrationDatabase.NumberField, IntegrationDatabase.BigNumberField,
                IntegrationDatabase.AmountField, IntegrationDatabase.ScoreField, IntegrationDatabase.DateField, IntegrationDatabase.FlagField, IntegrationDatabase.GuidField,
            ],
            [
                ["ann@create.test", "Ann", "1990-05-17T08:30:00", "Line one", "C-1", "-42", "9000000000", "1234.565", "1.5e3", "2024-02-29", "yes", "6f9619ff-8b86-d011-b42d-00c04fc964ff"],
                ["bob@create.test", "", "", "", "", "", "", "", "", "", "", ""],
            ]);

        var progress = harness.Import(jobId);

        var ann = ContactByEmail("ann@create.test");
        var bob = ContactByEmail("bob@create.test");
        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.Rows, Is.EqualTo(2));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(2));
            Assert.That(progress.RowsProcessed, Is.EqualTo(2));
            Assert.That(progress.RowsTotal, Is.EqualTo(2));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Completed));

            Assert.That(ann[FirstName], Is.EqualTo("Ann"));
            Assert.That(ann["ContactBirthday"], Is.EqualTo(new DateTime(1990, 5, 17, 8, 30, 0, DateTimeKind.Unspecified)));
            Assert.That(ann["ContactNotes"], Is.EqualTo("Line one"));
            Assert.That(ann[Code], Is.EqualTo("C-1"));
            Assert.That(ann[IntegrationDatabase.NumberField], Is.EqualTo(-42));
            Assert.That(ann[IntegrationDatabase.BigNumberField], Is.EqualTo(9000000000L));
            Assert.That(ann[IntegrationDatabase.AmountField], Is.EqualTo(1234.57m));
            Assert.That(ann[IntegrationDatabase.ScoreField], Is.EqualTo(1500d));
            Assert.That(ann[IntegrationDatabase.DateField], Is.EqualTo(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Unspecified)));
            Assert.That(ann[IntegrationDatabase.FlagField], Is.EqualTo(true));
            Assert.That(ann[IntegrationDatabase.GuidField], Is.EqualTo(new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff")));

            // System fields the importer or BulkInsert must fill.
            Assert.That(ann["ContactGUID"], Is.Not.Null.And.Not.EqualTo(Guid.Empty));
            Assert.That((DateTime)ann["ContactCreated"]!, Is.GreaterThan(before));
            Assert.That((DateTime)ann["ContactLastModified"]!, Is.GreaterThan(before));

            Assert.That(bob[FirstName], Is.Null);
            Assert.That(bob[Code], Is.Null);
            Assert.That(bob[IntegrationDatabase.NumberField], Is.Null);
            Assert.That(bob[IntegrationDatabase.FlagField], Is.Null);
        });
    }

    [Test]
    public void Update_With_Ignore_Keeps_Database_Values_For_Empty_File_Values()
    {
        int id = Seed("ann@update.test", "Ann", "Old", "C-1");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, [Email, FirstName, LastName, Code],
        [
            ["ann@update.test", "", "New", ""],
        ]);

        var progress = harness.Import(jobId);

        var ann = ContactById(id);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ContactsUpdated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Created, Is.Zero);
            Assert.That(progress.Aggregates.Fields, Is.EqualTo(new[] { new ImportFieldChangeStat(LastName, 1, 0) }));
            Assert.That(ann[FirstName], Is.EqualTo("Ann"));
            Assert.That(ann[LastName], Is.EqualTo("New"));
            Assert.That(ann[Code], Is.EqualTo("C-1"));
            Assert.That((DateTime)ann["ContactLastModified"]!, Is.GreaterThan(Old));
            Assert.That(ann["ContactCreated"], Is.EqualTo(Old));
            Assert.That(ContactCount(), Is.EqualTo(1));
        });
    }

    [Test]
    public void Update_With_Clear_Sets_Database_Values_To_Null_For_Empty_File_Values()
    {
        int id = Seed("ann@clear.test", "Ann", "Old", "C-1");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(
            Email,
            [Email, FirstName, LastName, Code],
            [["ann@clear.test", "", "New", ""]],
            new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear });

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        var analyzed = ContactById(id);
        harness.Processor.Confirm(jobId);
        var progress = harness.Run(jobId, ImportPhase.Execute);

        var ann = ContactById(id);
        Assert.Multiple(() =>
        {
            Assert.That(analyzed[FirstName], Is.EqualTo("Ann"), "The analysis writes nothing.");
            Assert.That(analysis.Aggregates, Is.EqualTo(progress.Aggregates).Using<ImportAggregates>(SameCounts));
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Fields, Is.EqualTo(new[]
            {
                new ImportFieldChangeStat(FirstName, 1, 1),
                new ImportFieldChangeStat(LastName, 1, 0),
                new ImportFieldChangeStat(Code, 1, 1),
            }));
            Assert.That(ann[FirstName], Is.Null);
            Assert.That(ann[LastName], Is.EqualTo("New"));
            Assert.That(ann[Code], Is.Null);
            Assert.That(ann[Email], Is.EqualTo("ann@clear.test"));
        });
    }

    [Test]
    public void Typed_Values_Are_Updated_Through_OpenJson_And_Unchanged_On_A_Second_Import()
    {
        string[] targets =
        [
            Email, "ContactBirthday", IntegrationDatabase.NumberField, IntegrationDatabase.BigNumberField, IntegrationDatabase.AmountField,
            IntegrationDatabase.ScoreField, IntegrationDatabase.DateField, IntegrationDatabase.FlagField, IntegrationDatabase.GuidField,
        ];
        string?[][] rows = [["typed@update.test", "2001-12-31T23:59:59.1234567", "7", "-9000000000", "-0.005", "0.1", "1999-01-01", "0", "00000000-0000-0000-0000-00000000000a"]];
        int id = Seed("typed@update.test");

        using var harness = new ImportHarness();
        var first = harness.Import(harness.Upload(Email, targets, rows));
        var contact = ContactById(id);
        var modified = (DateTime)contact["ContactLastModified"]!;
        var second = harness.Import(harness.Upload(Email, targets, rows));

        Assert.Multiple(() =>
        {
            Assert.That(first.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(contact["ContactBirthday"], Is.EqualTo(new DateTime(2001, 12, 31, 23, 59, 59, DateTimeKind.Unspecified).AddTicks(1234567)));
            Assert.That(contact[IntegrationDatabase.NumberField], Is.EqualTo(7));
            Assert.That(contact[IntegrationDatabase.BigNumberField], Is.EqualTo(-9000000000L));
            Assert.That(contact[IntegrationDatabase.AmountField], Is.EqualTo(-0.01m));
            Assert.That(contact[IntegrationDatabase.ScoreField], Is.EqualTo(0.1d));
            Assert.That(contact[IntegrationDatabase.DateField], Is.EqualTo(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)));
            Assert.That(contact[IntegrationDatabase.FlagField], Is.EqualTo(false));
            Assert.That(contact[IntegrationDatabase.GuidField], Is.EqualTo(new Guid("00000000-0000-0000-0000-00000000000a")));

            Assert.That(second.Aggregates.Unchanged, Is.EqualTo(1));
            Assert.That(second.Aggregates.Updated, Is.Zero);
            Assert.That(ContactById(id)["ContactLastModified"], Is.EqualTo(modified), "An unchanged contact is not touched.");
        });
    }

    [Test]
    public void New_Contacts_Skip_And_Existing_Contacts_Skip_Leave_The_Database_Alone()
    {
        int id = Seed("old@options.test", "Old");
        using var harness = new ImportHarness();
        string?[][] rows = [["old@options.test", "Changed"], ["new@options.test", "New"]];

        var skipNew = harness.Import(harness.Upload(Email, [Email, FirstName], rows, new ImportOptions { NewContacts = NewContactsBehavior.Skip, ExistingContacts = ExistingContactsBehavior.Skip }));

        Assert.Multiple(() =>
        {
            Assert.That(skipNew.Aggregates.SkippedNew, Is.EqualTo(1));
            Assert.That(skipNew.Aggregates.SkippedExisting, Is.EqualTo(1));
            Assert.That(ContactCount(), Is.EqualTo(1));
            Assert.That(ContactById(id)[FirstName], Is.EqualTo("Old"));
            Assert.That(ContactById(id)["ContactLastModified"], Is.EqualTo(Old));
        });
    }

    [Test]
    public void Invalid_Values_And_Invalid_Emails_Are_Row_Outcomes_Not_Failures()
    {
        using var harness = new ImportHarness();
        string[] targets = [Code, Email, IntegrationDatabase.NumberField];
        string?[][] rows =
        [
            ["K1", "good@rows.test", "1"],
            ["K2", "not-an-email", "2"],
            ["K3", "fine@rows.test", "twelve"],
            ["K4", "", "4"],
        ];

        var jobId = harness.Upload(Code, targets, rows);
        var skipped = harness.Import(jobId);
        string[] report = harness.ReadReport(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(skipped.Aggregates.Created, Is.EqualTo(2));
            Assert.That(skipped.Aggregates.SkippedInvalidEmail, Is.EqualTo(1));
            Assert.That(skipped.Aggregates.SkippedInvalidValue, Is.EqualTo(1));
            Assert.That(Contacts().Select(c => c[Code]), Is.EquivalentTo(new[] { "K1", "K4" }));
            Assert.That(report, Is.EqualTo(new[]
            {
                "row,outcome,reason,field",
                "1,created,,",
                "2,skipped,invalidEmail,ContactEmail",
                $"3,skipped,invalidValue,{IntegrationDatabase.NumberField}",
                "4,created,,",
            }));
        });

        IntegrationDatabase.DeleteContacts();
        using var second = new ImportHarness();
        var imported = second.Import(second.Upload(Code, targets, rows, new ImportOptions { InvalidEmail = InvalidEmailBehavior.ImportWithoutEmail }));

        Assert.Multiple(() =>
        {
            Assert.That(imported.Aggregates.Created, Is.EqualTo(3));
            Assert.That(imported.Aggregates.ImportedWithoutEmail, Is.EqualTo(1));
            Assert.That(imported.Aggregates.SkippedInvalidEmail, Is.Zero);
            Assert.That(Contacts("[" + Code + "] = 'K2'").Single()[Email], Is.Null);
        });
    }

    [Test]
    public void Import_Without_Email_Keeps_The_Existing_Email_Even_When_Empty_Values_Clear()
    {
        int id = Seed("keep@rows.test", "Ann", code: "K1");
        using var harness = new ImportHarness();

        var progress = harness.Import(harness.Upload(
            Code,
            [Code, Email, FirstName],
            [["K1", "broken", "Anna"]],
            new ImportOptions { InvalidEmail = InvalidEmailBehavior.ImportWithoutEmail, EmptyValues = EmptyValuesBehavior.Clear }));

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(ContactById(id)[Email], Is.EqualTo("keep@rows.test"));
            Assert.That(ContactById(id)[FirstName], Is.EqualTo("Anna"));
        });
    }

    [Test]
    public void Source_Stamp_Marks_Created_Contacts_And_Updated_Ones_Only_When_Asked()
    {
        int updatedId = Seed("old@stamp.test", "Old");
        int unchangedId = Seed("same@stamp.test", "Same");
        string?[][] rows = [["old@stamp.test", "Changed"], ["same@stamp.test", "Same"], ["new@stamp.test", "New"]];

        using var harness = new ImportHarness();
        harness.Import(harness.Upload(Email, [Email, FirstName], rows, new ImportOptions { SourceField = IntegrationDatabase.SourceField, SourceValue = "import-1" }));

        Assert.Multiple(() =>
        {
            Assert.That(ContactByEmail("new@stamp.test")[IntegrationDatabase.SourceField], Is.EqualTo("import-1"));
            Assert.That(ContactById(updatedId)[IntegrationDatabase.SourceField], Is.Null, "Created only: updated contacts are not stamped.");
            Assert.That(ContactById(updatedId)[FirstName], Is.EqualTo("Changed"));
        });

        string?[][] secondRows = [["old@stamp.test", "Again"], ["same@stamp.test", "Same"], ["newer@stamp.test", "Newer"]];
        harness.Import(harness.Upload(
            Email,
            [Email, FirstName],
            secondRows,
            new ImportOptions { SourceField = IntegrationDatabase.SourceField, SourceValue = "import-2", SourceStamp = SourceStampBehavior.CreatedAndUpdated }));

        Assert.Multiple(() =>
        {
            Assert.That(ContactByEmail("newer@stamp.test")[IntegrationDatabase.SourceField], Is.EqualTo("import-2"));
            Assert.That(ContactById(updatedId)[IntegrationDatabase.SourceField], Is.EqualTo("import-2"));
            Assert.That(ContactById(unchangedId)[IntegrationDatabase.SourceField], Is.Null, "A contact without changes is not stamped.");
            Assert.That(ContactByEmail("new@stamp.test")[IntegrationDatabase.SourceField], Is.EqualTo("import-1"));
        });
    }

    private static readonly ImportFieldChangeStat[] noFields = [];

    internal static bool SameCounts(ImportAggregates x, ImportAggregates y) =>
        x with { Fields = noFields } == y with { Fields = noFields } && x.Fields.SequenceEqual(y.Fields);
}
