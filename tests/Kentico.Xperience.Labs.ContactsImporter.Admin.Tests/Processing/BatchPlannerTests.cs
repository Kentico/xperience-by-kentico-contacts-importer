using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

public class BatchPlannerTests
{
    private static readonly DateTime older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime newer = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly string[] emailTargets = [Email, FirstName, LastName];

    private static BatchPlan Plan(ImportProcessingContext context, ImportRecord[] records, params ContactMatch[] matches)
    {
        var planner = new BatchPlanner(context);

        return planner.Classify(planner.Prepare(records), matches);
    }

    private static ContactMatch Match(int groupIndex, int contactId, DateTime lastModified, params object?[] values) => new(groupIndex, contactId, lastModified, values);

    private static void AssertRowsAddUp(BatchPlan plan)
    {
        var totals = ImportAggregates.From(plan.Counts.Values);
        Assert.That(totals.Created + totals.Updated + totals.Unchanged + totals.Skipped + totals.DuplicateRows, Is.EqualTo(totals.Rows), "Every row has exactly one outcome.");
        Assert.That(plan.Rows, Has.Count.EqualTo(totals.Rows));
    }

    [Test]
    public void Prepare_Groups_Rows_By_Key_In_File_Order()
    {
        var batch = new BatchPlanner(Context(Email, emailTargets)).Prepare(
        [
            Record(1, "a@x.test", "a@x.test", "A1", ""),
            Record(2, "b@x.test", "b@x.test", "B", ""),
            Record(3, "a@x.test", "a@x.test", "A2", ""),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(batch.Groups.Select(g => g.KeyText), Is.EqualTo(new[] { "a@x.test", "b@x.test" }));
            Assert.That(batch.Groups[0].Rows.Select(r => r.Row), Is.EqualTo(new long[] { 1, 3 }));
            Assert.That(batch.Groups[0].KeyValue, Is.EqualTo("a@x.test"));
            Assert.That(batch.HasSimilarKeys, Is.False);
            Assert.That(batch.Counts.Get(ImportAggregateKeys.Rows), Is.EqualTo(3));
        });
    }

    [Test]
    public void Prepare_Uses_The_Normalized_Key_As_The_Key_Value_Of_Text_Keys()
    {
        var batch = new BatchPlanner(Context(Email, emailTargets)).Prepare([Record(1, "jane@x.test", "Jane@X.test", "Jane", "")]);

        Assert.That(batch.Groups.Single().Rows.Single().Values[0], Is.EqualTo("jane@x.test"));
    }

    [TestCase("abc", "ABC", true)]
    [TestCase("resume", "résumé", true)]
    [TestCase("abc", "abd", false)]
    public void Prepare_Flags_Text_Keys_A_Collation_Could_Treat_As_Equal(string first, string second, bool similar)
    {
        var batch = new BatchPlanner(Context(CustomerId, [CustomerId, FirstName])).Prepare(
        [
            Record(1, first, first, "A"),
            Record(2, second, second, "B"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(batch.Groups, Has.Count.EqualTo(2));
            Assert.That(batch.HasSimilarKeys, Is.EqualTo(similar));
        });
    }

    [Test]
    public void Number_Keys_With_The_Same_Value_Are_One_Group()
    {
        var batch = new BatchPlanner(Context("Age", ["Age", FirstName])).Prepare(
        [
            Record(1, "8", "8", "A"),
            Record(2, "+8", "+8", "B"),
            Record(3, "9", "9", "C"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(batch.Groups.Select(g => g.KeyValue), Is.EqualTo(new object[] { 8, 9 }));
            Assert.That(batch.HasSimilarKeys, Is.False);
        });
    }

    [Test]
    public void New_Key_Is_Created_With_Its_Non_Empty_Values()
    {
        var plan = Plan(Context(Email, emailTargets), [Record(1, "a@x.test", "a@x.test", "Ann", "")]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Creates.Single().Values, Is.EqualTo(new object?[] { "a@x.test", "Ann", null }));
            Assert.That(plan.Updates, Is.Empty);
            Assert.That(plan.Rows.Single(), Is.EqualTo(new ImportRowResult(1, ImportRowOutcome.Created, null, null, "a@x.test")));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.Created), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void New_Key_Is_Skipped_When_New_Contacts_Are_Not_Created()
    {
        var plan = Plan(Context(Email, emailTargets, new ImportOptions { NewContacts = NewContactsBehavior.Skip }), [Record(1, "a@x.test", "a@x.test", "Ann", "")]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Creates, Is.Empty);
            Assert.That(plan.Rows.Single().Outcome, Is.EqualTo(ImportRowOutcome.Skipped));
            Assert.That(plan.Rows.Single().Reason, Is.EqualTo(ImportRowReasons.NewContactsNotCreated));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.SkippedNew), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Existing_Contact_With_A_Different_Value_Is_Updated_And_Field_Changes_Are_Counted()
    {
        var plan = Plan(
            Context(Email, emailTargets),
            [Record(1, "a@x.test", "a@x.test", "Ann", "")],
            Match(0, 7, older, null, "Anna", "Smith"));

        var update = plan.Updates.Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.ContactId, Is.EqualTo(7));
            Assert.That(update.States, Is.EqualTo(new[] { FieldValueState.Value, FieldValueState.Value, FieldValueState.Empty }));
            Assert.That(update.Values[1], Is.EqualTo("Ann"));
            Assert.That(plan.Rows.Single().Outcome, Is.EqualTo(ImportRowOutcome.Updated));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.Updated), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.ContactsUpdated), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.FieldChangedPrefix + FirstName), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.FieldChangedPrefix + LastName), Is.Zero, "Empty value, ignore: the field does not change.");
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Existing_Contact_With_The_Same_Values_Is_Unchanged_And_Not_Written()
    {
        var plan = Plan(
            Context(Email, emailTargets),
            [Record(1, "a@x.test", "a@x.test", "Ann", "")],
            Match(0, 7, older, null, "Ann", "Smith"));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Updates, Is.Empty);
            Assert.That(plan.Rows.Single().Outcome, Is.EqualTo(ImportRowOutcome.Unchanged));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.Unchanged), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Text_Comparison_Is_Case_Sensitive_So_A_Case_Change_Is_An_Update()
    {
        var plan = Plan(Context(Email, emailTargets), [Record(1, "a@x.test", "a@x.test", "ANN", "")], Match(0, 7, older, null, "Ann", null));

        Assert.That(plan.Updates, Has.Count.EqualTo(1));
    }

    [Test]
    public void Empty_Value_With_Clear_Is_A_Change_Only_When_The_Contact_Has_A_Value()
    {
        var context = Context(Email, emailTargets, new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear });

        var cleared = Plan(context, [Record(1, "a@x.test", "a@x.test", "Ann", "")], Match(0, 7, older, null, "Ann", "Smith"));
        var alreadyEmpty = Plan(context, [Record(1, "a@x.test", "a@x.test", "Ann", "")], Match(0, 7, older, null, "Ann", ""));

        Assert.Multiple(() =>
        {
            Assert.That(cleared.Updates, Has.Count.EqualTo(1));
            Assert.That(cleared.Counts.Get(ImportAggregateKeys.FieldClearedPrefix + LastName), Is.EqualTo(1));
            Assert.That(cleared.Counts.Get(ImportAggregateKeys.FieldChangedPrefix + LastName), Is.EqualTo(1));
            Assert.That(alreadyEmpty.Updates, Is.Empty);
            Assert.That(alreadyEmpty.Rows.Single().Outcome, Is.EqualTo(ImportRowOutcome.Unchanged));
        });
    }

    [Test]
    public void Existing_Contact_Is_Skipped_When_Existing_Contacts_Are_Not_Updated()
    {
        var plan = Plan(
            Context(Email, emailTargets, new ImportOptions { ExistingContacts = ExistingContactsBehavior.Skip }),
            [Record(1, "a@x.test", "a@x.test", "Ann", "")],
            Match(0, 7, older, null, "Anna", null),
            Match(0, 8, newer, null, "Anna", null));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Updates, Is.Empty);
            Assert.That(plan.Rows.Single().Reason, Is.EqualTo(ImportRowReasons.ExistingContactsNotUpdated));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.SkippedExisting), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.MultipleMatches), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [TestCase((int)MultipleMatchesBehavior.UpdateNewest, new[] { 8 })]
    [TestCase((int)MultipleMatchesBehavior.UpdateAll, new[] { 7, 8, 9 })]
    [TestCase((int)MultipleMatchesBehavior.Skip, new int[0])]
    public void Multiple_Matches_Follow_The_Option(int behaviorValue, int[] expectedContactIds)
    {
        var behavior = (MultipleMatchesBehavior)behaviorValue;
        var plan = Plan(
            Context(Email, emailTargets, new ImportOptions { MultipleMatches = behavior }),
            [Record(1, "a@x.test", "a@x.test", "Ann", "")],
            Match(0, 7, older, null, "A", null),
            Match(0, 8, newer, null, "B", null),
            Match(0, 9, older, null, "C", null));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Updates.Select(u => u.ContactId), Is.EqualTo(expectedContactIds));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.MultipleMatches), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.ContactsUpdated), Is.EqualTo(expectedContactIds.Length));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.Updated), Is.EqualTo(behavior == MultipleMatchesBehavior.Skip ? 0 : 1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.SkippedMultipleMatches), Is.EqualTo(behavior == MultipleMatchesBehavior.Skip ? 1 : 0));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Update_Newest_Breaks_A_Tie_By_The_Higher_Contact_Id_And_Update_All_Skips_Contacts_Without_Changes()
    {
        var newest = Plan(
            Context(Email, emailTargets),
            [Record(1, "a@x.test", "a@x.test", "Ann", "")],
            Match(0, 7, older, null, "A", null),
            Match(0, 9, older, null, "B", null));
        var all = Plan(
            Context(Email, emailTargets, new ImportOptions { MultipleMatches = MultipleMatchesBehavior.UpdateAll }),
            [Record(1, "a@x.test", "a@x.test", "Ann", "")],
            Match(0, 7, older, null, "Ann", null),
            Match(0, 9, older, null, "B", null));

        Assert.Multiple(() =>
        {
            Assert.That(newest.Updates.Single().ContactId, Is.EqualTo(9));
            Assert.That(all.Updates.Single().ContactId, Is.EqualTo(9));
        });
    }

    [Test]
    public void Duplicate_Keys_Collapse_With_Ignore_Last_Non_Empty_Value_Wins()
    {
        var plan = Plan(Context(Email, emailTargets),
        [
            Record(1, "a@x.test", "a@x.test", "First", "Smith"),
            Record(2, "b@x.test", "b@x.test", "Other", ""),
            Record(3, "a@x.test", "a@x.test", "Second", ""),
            Record(4, "a@x.test", "a@x.test", "", ""),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Creates, Has.Count.EqualTo(2));
            Assert.That(plan.Creates[0].Values, Is.EqualTo(new object?[] { "a@x.test", "Second", "Smith" }));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.DuplicateRows), Is.EqualTo(2));
            Assert.That(plan.Rows.Select(r => (r.Row, r.Outcome, r.Reason)), Is.EqualTo(new[]
            {
                (1L, ImportRowOutcome.Created, ImportRowReasons.DuplicateKey),
                (2L, ImportRowOutcome.Created, null),
                (3L, ImportRowOutcome.Created, ImportRowReasons.DuplicateKey),
                (4L, ImportRowOutcome.Created, null),
            }));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Duplicate_Keys_Collapse_With_Clear_The_Last_Row_Decides()
    {
        var plan = Plan(
            Context(Email, emailTargets, new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear }),
            [
                Record(1, "a@x.test", "a@x.test", "First", ""),
                Record(2, "a@x.test", "a@x.test", "", "Last"),
            ],
            Match(0, 7, older, null, "Db", "Db"));

        var update = plan.Updates.Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.States, Is.EqualTo(new[] { FieldValueState.Value, FieldValueState.Empty, FieldValueState.Value }));
            Assert.That(update.Values[2], Is.EqualTo("Last"));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.FieldClearedPrefix + FirstName), Is.EqualTo(1));
        });
    }

    [Test]
    public void Skipped_Group_Gives_Every_Row_The_Skip_Reason()
    {
        var plan = Plan(
            Context(Email, emailTargets, new ImportOptions { NewContacts = NewContactsBehavior.Skip }),
            [
                Record(1, "a@x.test", "a@x.test", "First", ""),
                Record(2, "a@x.test", "a@x.test", "Second", ""),
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows.Select(r => r.Reason), Is.All.EqualTo(ImportRowReasons.NewContactsNotCreated));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.SkippedNew), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.DuplicateRows), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Groups_Matching_The_Same_Contact_Are_Handled_As_One_Key()
    {
        var plan = Plan(
            Context(CustomerId, [CustomerId, FirstName]),
            [
                Record(1, "abc", "abc", "Lower"),
                Record(2, "ABC", "ABC", "Upper"),
            ],
            Match(0, 7, older, null, "Db"),
            Match(1, 7, older, null, "Db"));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Updates.Single().ContactId, Is.EqualTo(7));
            Assert.That(plan.Updates.Single().Values[1], Is.EqualTo("Upper"));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.Updated), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.DuplicateRows), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.MultipleMatches), Is.Zero);
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Equivalent_Keys_Without_A_Match_Create_One_Contact()
    {
        var planner = new BatchPlanner(Context(CustomerId, [CustomerId, FirstName]));
        var batch = planner.Prepare(
        [
            Record(1, "new", "new", "First"),
            Record(2, "other", "other", "Other"),
            Record(3, "NEW", "NEW", "Second"),
        ]);

        var plan = planner.Classify(batch, [], [(2, 0), (99, 0)]);

        Assert.Multiple(() =>
        {
            Assert.That(batch.HasSimilarKeys, Is.True);
            Assert.That(plan.Creates, Has.Count.EqualTo(2));
            Assert.That(plan.Creates[0].Values, Is.EqualTo(new object?[] { "NEW", "Second" }));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.DuplicateRows), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Invalid_Email_Skips_The_Row_By_Default_Whatever_The_Browser_Flagged()
    {
        var plan = Plan(Context(CustomerId, [CustomerId, Email, FirstName]),
        [
            Record(1, "K1", "K1", "not-an-email", "Ann"),
            Record(2, "K2", "K2", "Ok@X.Test", "Bob"),
            Record(3, "K3", "K3", "", "Cid"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows[0], Is.EqualTo(new ImportRowResult(1, ImportRowOutcome.Skipped, ImportRowReasons.InvalidEmail, Email, "K1")));
            Assert.That(plan.Creates, Has.Count.EqualTo(2));
            Assert.That(plan.Creates[0].Values[1], Is.EqualTo("ok@x.test"), "The email is lowercased by the server too.");
            Assert.That(plan.Creates[1].Values[1], Is.Null, "An empty email is not an invalid email.");
            Assert.That(plan.Counts.Get(ImportAggregateKeys.SkippedInvalidEmail), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Invalid_Email_Can_Be_Imported_Without_The_Email_Which_Then_Is_Never_Cleared()
    {
        var context = Context(CustomerId, [CustomerId, Email, FirstName], new ImportOptions { InvalidEmail = InvalidEmailBehavior.ImportWithoutEmail, EmptyValues = EmptyValuesBehavior.Clear });

        var plan = Plan(context, [Record(1, "K1", "K1", "not-an-email", "Ann")], Match(0, 7, older, null, "keep@x.test", "Old"));

        var update = plan.Updates.Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.States[1], Is.EqualTo(FieldValueState.Absent));
            Assert.That(plan.Rows.Single(), Is.EqualTo(new ImportRowResult(1, ImportRowOutcome.Updated, ImportRowReasons.EmailNotImported, Email, "K1")));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.ImportedWithoutEmail), Is.EqualTo(1));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.FieldChangedPrefix + Email), Is.Zero);
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Value_That_Does_Not_Fit_Its_Field_Skips_The_Row_And_Names_The_Field()
    {
        var plan = Plan(Context(Email, [Email, "Age"]),
        [
            Record(1, "a@x.test", "a@x.test", "twelve"),
            Record(2, "b@x.test", "b@x.test", "12"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows[0], Is.EqualTo(new ImportRowResult(1, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, "Age", "a@x.test")));
            Assert.That(plan.Creates.Single().Values, Is.EqualTo(new object?[] { "b@x.test", 12 }));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.SkippedInvalidValue), Is.EqualTo(1));
        });
        AssertRowsAddUp(plan);
    }

    [Test]
    public void Field_Missing_From_A_Row_Is_Absent_And_Never_Changes_The_Contact()
    {
        var plan = Plan(
            Context(Email, emailTargets, new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear }),
            [Record(1, "a@x.test", "a@x.test", null, "New")],
            Match(0, 7, older, null, "Keep", "Old"));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Updates.Single().States[1], Is.EqualTo(FieldValueState.Absent));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.FieldChangedPrefix + FirstName), Is.Zero);
        });
    }

    [Test]
    public void Stamped_Source_Field_That_Is_Also_Mapped_Is_Not_Compared_For_Updates()
    {
        var context = Context(
            Email,
            [Email, Source],
            new ImportOptions { SourceField = Source, SourceValue = "import", SourceStamp = SourceStampBehavior.CreatedAndUpdated });

        var plan = Plan(context, [Record(1, "a@x.test", "a@x.test", "from file")], Match(0, 7, older, null, "other"));

        Assert.That(plan.Rows.Single().Outcome, Is.EqualTo(ImportRowOutcome.Unchanged), "The stamp alone does not make a contact updated.");
    }

    [Test]
    public void All_Options_Combinations_Give_Every_Row_One_Outcome()
    {
        ImportRecord[] records =
        [
            Record(1, "K1", "K1", "new@x.test", "A"),
            Record(2, "K2", "K2", "bad", "B"),
            Record(3, "K3", "K3", "one@x.test", "C"),
            Record(4, "K4", "K4", "many@x.test", "D"),
            Record(5, "K4", "K4", "", "E"),
            Record(6, "K5", "K5", "same@x.test", "Same"),
        ];

        Assert.Multiple(() =>
        {
            foreach (var newContacts in Enum.GetValues<NewContactsBehavior>())
            {
                foreach (var existing in Enum.GetValues<ExistingContactsBehavior>())
                {
                    foreach (var empty in Enum.GetValues<EmptyValuesBehavior>())
                    {
                        foreach (var multiple in Enum.GetValues<MultipleMatchesBehavior>())
                        {
                            foreach (var invalidEmail in Enum.GetValues<InvalidEmailBehavior>())
                            {
                                var options = new ImportOptions { NewContacts = newContacts, ExistingContacts = existing, EmptyValues = empty, MultipleMatches = multiple, InvalidEmail = invalidEmail };
                                var planner = new BatchPlanner(Context(CustomerId, [CustomerId, Email, FirstName], options));
                                var batch = planner.Prepare(records);
                                var matches = new List<ContactMatch>();
                                for (int i = 0; i < batch.Groups.Count; i++)
                                {
                                    switch (batch.Groups[i].KeyText)
                                    {
                                        case "K3":
                                            matches.Add(Match(i, 3, older, null, "old@x.test", "C"));
                                            break;
                                        case "K4":
                                            matches.Add(Match(i, 4, older, null, "many@x.test", "X"));
                                            matches.Add(Match(i, 5, newer, null, "many@x.test", "Y"));
                                            break;
                                        case "K5":
                                            matches.Add(Match(i, 6, older, null, "same@x.test", "Same"));
                                            break;
                                        default:
                                            break;
                                    }
                                }

                                AssertRowsAddUp(planner.Classify(batch, matches));
                            }
                        }
                    }
                }
            }
        });
    }
}
