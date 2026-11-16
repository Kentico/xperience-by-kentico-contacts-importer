namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

public class DuplicatesAndMatchesTests : ImportIntegrationTest
{
    [Test]
    public void Multiple_Matches_Update_Newest_Updates_Only_The_Most_Recently_Modified_Contact()
    {
        int older = Seed("twin@match.test", "Older", lastModified: Old);
        int newer = Seed("twin@match.test", "Newer", lastModified: Old.AddDays(1));
        using var harness = new ImportHarness();

        var progress = harness.Import(harness.Upload(Email, [Email, FirstName], [["twin@match.test", "Imported"]]));

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.MultipleMatches, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ContactsUpdated, Is.EqualTo(1));
            Assert.That(ContactById(newer)[FirstName], Is.EqualTo("Imported"));
            Assert.That(ContactById(older)[FirstName], Is.EqualTo("Older"));
            Assert.That(ContactById(older)["ContactLastModified"], Is.EqualTo(Old));
        });
    }

    [Test]
    public void Multiple_Matches_Update_All_Updates_Every_Matching_Contact()
    {
        int first = Seed("twin@match.test", "First");
        int second = Seed("twin@match.test", "Second", lastModified: Old.AddDays(1));
        int same = Seed("twin@match.test", "Imported", lastModified: Old.AddDays(2));
        using var harness = new ImportHarness();

        var progress = harness.Import(harness.Upload(
            Email,
            [Email, FirstName],
            [["twin@match.test", "Imported"]],
            new ImportOptions { MultipleMatches = MultipleMatchesBehavior.UpdateAll }));

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.MultipleMatches, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ContactsUpdated, Is.EqualTo(2));
            Assert.That(ContactById(first)[FirstName], Is.EqualTo("Imported"));
            Assert.That(ContactById(second)[FirstName], Is.EqualTo("Imported"));
            Assert.That(ContactById(same)["ContactLastModified"], Is.EqualTo(Old.AddDays(2)), "The contact that already has the values is not touched.");
        });
    }

    [Test]
    public void Multiple_Matches_Skip_Reports_The_Row_And_Changes_Nothing()
    {
        int first = Seed("twin@match.test", "First");
        int second = Seed("twin@match.test", "Second");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(
            Email,
            [Email, FirstName],
            [["twin@match.test", "Imported"], ["single@match.test", "Single"]],
            new ImportOptions { MultipleMatches = MultipleMatchesBehavior.Skip });

        var progress = harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.SkippedMultipleMatches, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(1));
            Assert.That(ContactById(first)[FirstName], Is.EqualTo("First"));
            Assert.That(ContactById(second)[FirstName], Is.EqualTo("Second"));
            Assert.That(harness.ReadReport(jobId, ImportPhase.Execute), Is.EqualTo(new[]
            {
                "row,outcome,reason,field",
                "1,skipped,multipleMatches,",
                "2,created,,",
            }));
        });
    }

    [Test]
    public void Duplicate_Keys_In_One_Batch_Are_Collapsed_Last_Non_Empty_Value_Wins()
    {
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, [Email, FirstName, LastName],
        [
            ["dup@batch.test", "First", "Smith"],
            ["other@batch.test", "Other", ""],
            ["dup@batch.test", "Second", ""],
            ["dup@batch.test", "", ""],
        ]);

        var progress = harness.Import(jobId);

        var contact = ContactByEmail("dup@batch.test");
        Assert.Multiple(() =>
        {
            Assert.That(ContactCount(), Is.EqualTo(2));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(2));
            Assert.That(progress.Aggregates.DuplicateRows, Is.EqualTo(2));
            Assert.That(progress.Aggregates.Rows, Is.EqualTo(4));
            Assert.That(contact[FirstName], Is.EqualTo("Second"));
            Assert.That(contact[LastName], Is.EqualTo("Smith"));
            Assert.That(harness.ReadReport(jobId, ImportPhase.Execute), Is.EqualTo(new[]
            {
                "row,outcome,reason,field",
                "1,created,duplicateKey,",
                "2,created,,",
                "3,created,duplicateKey,",
                "4,created,,",
            }));
        });
    }

    [Test]
    public void Duplicate_Keys_In_One_Batch_With_Clear_Apply_The_Last_Row()
    {
        int id = Seed("dup@clear.test", "Db", "Db");
        using var harness = new ImportHarness();

        harness.Import(harness.Upload(
            Email,
            [Email, FirstName, LastName],
            [["dup@clear.test", "First", ""], ["dup@clear.test", "", "Last"]],
            new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear }));

        Assert.Multiple(() =>
        {
            Assert.That(ContactById(id)[FirstName], Is.Null);
            Assert.That(ContactById(id)[LastName], Is.EqualTo("Last"));
        });
    }

    [Test]
    public void Duplicate_Keys_In_Different_Batches_Converge_On_One_Contact_But_The_Analysis_Counts_Two_Creates()
    {
        using var harness = new ImportHarness(options => options.BatchSize = 2);
        var jobId = harness.Upload(Email, [Email, FirstName, LastName],
        [
            ["dup@cross.test", "First", "Smith"],
            ["a@cross.test", "A", ""],
            ["b@cross.test", "B", ""],
            ["dup@cross.test", "Second", ""],
        ]);

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        harness.Processor.Confirm(jobId);
        var progress = harness.Run(jobId, ImportPhase.Execute);

        var contact = ContactByEmail("dup@cross.test");
        Assert.Multiple(() =>
        {
            Assert.That(analysis.Aggregates.Created, Is.EqualTo(4), "Without writes the second occurrence also looks new (documented over-count).");
            Assert.That(progress.Aggregates.Created, Is.EqualTo(3));
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Analysis!.Created, Is.EqualTo(4), "Expected counts stay available next to the actual ones.");
            Assert.That(ContactCount(), Is.EqualTo(3));
            Assert.That(contact[FirstName], Is.EqualTo("Second"));
            Assert.That(contact[LastName], Is.EqualTo("Smith"));
        });
    }

    [Test]
    public void Custom_Text_Field_Can_Be_The_Key()
    {
        int existing = Seed("old@key.test", "Old", code: "CUST-1");
        using var harness = new ImportHarness();

        var progress = harness.Import(harness.Upload(Code, [Code, Email, FirstName],
        [
            ["CUST-1", "changed@key.test", "Changed"],
            ["CUST-2", "new@key.test", "New"],
            ["CUST-2", "newer@key.test", ""],
        ]));

        var created = Contacts($"[{Code}] = 'CUST-2'").Single();
        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(1));
            Assert.That(progress.Aggregates.DuplicateRows, Is.EqualTo(1));
            Assert.That(ContactById(existing)[Email], Is.EqualTo("changed@key.test"));
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("Changed"));
            Assert.That(ContactById(existing)[Code], Is.EqualTo("CUST-1"));
            Assert.That(created[Email], Is.EqualTo("newer@key.test"));
            Assert.That(created[FirstName], Is.EqualTo("New"));
        });
    }

    [Test]
    public void Custom_Number_Field_Can_Be_The_Key_And_Equal_Numbers_Are_One_Key()
    {
        int existing = IntegrationDatabase.Scalar<int>(
            $"INSERT INTO [OM_Contact] ([{IntegrationDatabase.NumberField}], [ContactFirstName], [ContactGUID], [ContactCreated], [ContactLastModified]) VALUES (7, 'Old', NEWID(), @now, @now); SELECT CAST(SCOPE_IDENTITY() AS int);",
            ("@now", Old));
        using var harness = new ImportHarness();

        var progress = harness.Import(harness.Upload(IntegrationDatabase.NumberField, [IntegrationDatabase.NumberField, FirstName],
        [
            ["7", "Seven"],
            ["8", "Eight"],
            ["+8", "Eight again"],
        ]));

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(1));
            Assert.That(progress.Aggregates.DuplicateRows, Is.EqualTo(1));
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("Seven"));
            Assert.That(Contacts($"[{IntegrationDatabase.NumberField}] = 8").Single()[FirstName], Is.EqualTo("Eight again"));
        });
    }

    [Test]
    public void Keys_The_Database_Treats_As_Equal_Are_One_Key()
    {
        bool caseInsensitive = IntegrationDatabase.Scalar<int>("SELECT CASE WHEN N'a' = N'A' THEN 1 ELSE 0 END") == 1;
        if (!caseInsensitive)
        {
            Assert.Ignore("The test database has a case-sensitive collation.");
        }

        int existing = Seed(null, "Old", code: "Abc");
        using var harness = new ImportHarness();

        // "abc"/"ABC" match the same contact; "new"/"NEW" match none, but would be two contacts with the same key.
        var progress = harness.Import(harness.Upload(Code, [Code, FirstName],
        [
            ["abc", "Lower"],
            ["ABC", "Upper"],
            ["new", "First"],
            ["NEW", "Second"],
        ]));

        Assert.Multiple(() =>
        {
            Assert.That(ContactCount(), Is.EqualTo(2));
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(1));
            Assert.That(progress.Aggregates.DuplicateRows, Is.EqualTo(2));
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("Upper"));
            Assert.That(ContactById(existing)[Code], Is.EqualTo("Abc"), "The key of a matched contact is not rewritten.");
            Assert.That(Contacts("[ContactID] <> @id", ("@id", existing)).Single()[FirstName], Is.EqualTo("Second"));
        });
    }

    [Test]
    public void Email_Key_Matches_Existing_Contacts_Whatever_Their_Letter_Case()
    {
        int existing = Seed("Mixed.Case@Match.Test", "Old");
        using var harness = new ImportHarness();

        var progress = harness.Import(harness.Upload(Email, [Email, FirstName], [["mixed.case@match.test", "New"]]));

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(ContactCount(), Is.EqualTo(1));
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("New"));
        });
    }
}
