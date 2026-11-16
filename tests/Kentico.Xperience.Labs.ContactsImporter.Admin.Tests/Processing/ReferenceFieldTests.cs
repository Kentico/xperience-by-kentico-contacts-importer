using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

/// <summary>
/// Fields that reference another object (country, contact status, user, ...): a value that does not exist there skips
/// its row, in the analysis and in the import, and never fails the job.
/// </summary>
public class ReferenceFieldTests
{
    private static readonly string[] countryTargets = [Email, FirstName, Country];

    /// <summary>
    /// An existence check over in-memory "tables", recording what it was asked.
    /// </summary>
    private sealed class Lookup(params int[] existing)
    {
        public List<(string Field, object[] Values)> Calls { get; } = [];

        public IReadOnlyList<int> FindMissing(ImportField field, IReadOnlyList<object> values)
        {
            Calls.Add((field.Name, [.. values]));

            return [.. Enumerable.Range(0, values.Count).Where(i => !existing.Contains((int)values[i]))];
        }
    }

    private static BatchPlan Plan(ImportProcessingContext context, Lookup lookup, ImportRecord[] records, params ContactMatch[] matches)
    {
        var planner = new BatchPlanner(context);

        return planner.Classify(planner.Prepare(records, lookup.FindMissing), matches);
    }

    private static void AssertRowsAddUp(BatchPlan plan)
    {
        var totals = ImportAggregates.From(plan.Counts.Values);
        Assert.That(totals.Created + totals.Updated + totals.Unchanged + totals.Skipped + totals.DuplicateRows, Is.EqualTo(totals.Rows), "Every row has exactly one outcome.");
        Assert.That(plan.Rows, Has.Count.EqualTo(totals.Rows));
    }

    [Test]
    public void Context_Groups_Checked_Reference_Fields_By_Referenced_Table()
    {
        var context = Context(Email, [Email, Owner, Country, Deputy, Persona, FirstName]);

        Assert.Multiple(() =>
        {
            Assert.That(context.ReferenceGroups.Select(group => group.Select(f => f.Name)), Is.EqualTo(new[] { new[] { Owner, Deputy }, [Country] }), "Owner and deputy reference the same table and column.");
            Assert.That(context.Fields[context.FieldIndexes[Persona]].Reference, Is.Null, "A reference whose table is not known is not checked.");
            Assert.That(context.Fields[context.FieldIndexes[FirstName]].Reference, Is.Null);
            Assert.That(Context(Email, [Email, FirstName]).ReferenceGroups, Is.Empty);
        });
    }

    [Test]
    public void Row_With_A_Reference_That_Does_Not_Exist_Is_Skipped_And_The_Other_Rows_Are_Imported()
    {
        var lookup = new Lookup(1, 2);

        var plan = Plan(Context(Email, countryTargets), lookup,
        [
            Record(1, "a@x.test", "a@x.test", "A", "1"),
            Record(2, "b@x.test", "b@x.test", "B", "999"),
            Record(3, "c@x.test", "c@x.test", "C", ""),
            Record(4, "d@x.test", "d@x.test", "D", null),
            Record(5, "e@x.test", "e@x.test", "E", "2"),
            Record(6, "f@x.test", "f@x.test", "F", "999"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows.Select(r => r.Outcome), Is.EqualTo(new[]
            {
                ImportRowOutcome.Created, ImportRowOutcome.Skipped, ImportRowOutcome.Created, ImportRowOutcome.Created, ImportRowOutcome.Created, ImportRowOutcome.Skipped,
            }));
            Assert.That(plan.Rows[1], Is.EqualTo(new ImportRowResult(2, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, Country, "b@x.test")));
            Assert.That(plan.Rows[5].Field, Is.EqualTo(Country));
            Assert.That(plan.Creates.Select(c => c.Values[2]), Is.EqualTo(new object?[] { 1, null, null, 2 }));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.SkippedInvalidValue), Is.EqualTo(2));
            Assert.That(plan.Counts.Get(ImportAggregateKeys.Created), Is.EqualTo(4));
            Assert.That(lookup.Calls, Has.Count.EqualTo(1), "One check per referenced table and batch.");
            Assert.That(lookup.Calls[0].Field, Is.EqualTo(Country));
            Assert.That(lookup.Calls[0].Values, Is.EqualTo(new object[] { 1, 999, 2 }), "Distinct values only; empty values are not checked.");
            AssertRowsAddUp(plan);
        });
    }

    [Test]
    public void Empty_And_Absent_References_Are_Not_Checked()
    {
        var lookup = new Lookup();

        var plan = Plan(Context(Email, countryTargets), lookup,
        [
            Record(1, "a@x.test", "a@x.test", "A", ""),
            Record(2, "b@x.test", "b@x.test", "B", null),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(lookup.Calls, Is.Empty);
            Assert.That(plan.Counts.Get(ImportAggregateKeys.Created), Is.EqualTo(2));
            AssertRowsAddUp(plan);
        });
    }

    [Test]
    public void Empty_Reference_Clears_The_Field_Of_An_Existing_Contact_Per_The_Empty_Values_Option()
    {
        var keep = Context(Email, countryTargets);
        var clear = Context(Email, countryTargets, new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear });
        ImportRecord[] records = [Record(1, "a@x.test", "a@x.test", "A", "")];
        var match = new ContactMatch(0, 7, TestData.Now, [null, "A", 5]);

        var kept = Plan(keep, new Lookup(), records, match);
        var cleared = Plan(clear, new Lookup(), records, match);

        Assert.Multiple(() =>
        {
            Assert.That(kept.Rows[0].Outcome, Is.EqualTo(ImportRowOutcome.Unchanged));
            Assert.That(cleared.Rows[0].Outcome, Is.EqualTo(ImportRowOutcome.Updated));
            Assert.That(cleared.Counts.Get(ImportAggregateKeys.FieldClearedPrefix + Country), Is.EqualTo(1));
        });
    }

    [Test]
    public void Fields_That_Reference_The_Same_Table_Are_Checked_With_One_Query()
    {
        var lookup = new Lookup(1, 2);

        var plan = Plan(Context(Email, [Email, Owner, Deputy]), lookup,
        [
            Record(1, "a@x.test", "a@x.test", "1", "2"),
            Record(2, "b@x.test", "b@x.test", "2", "50"),
            Record(3, "c@x.test", "c@x.test", "60", "50"),
            Record(4, "d@x.test", "d@x.test", "", "1"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(lookup.Calls, Has.Count.EqualTo(1));
            Assert.That(lookup.Calls[0].Values, Is.EquivalentTo(new object[] { 1, 2, 50, 60 }));
            Assert.That(plan.Rows.Select(r => (r.Outcome, r.Field)), Is.EqualTo(new (ImportRowOutcome, string?)[]
            {
                (ImportRowOutcome.Created, null), (ImportRowOutcome.Skipped, Deputy), (ImportRowOutcome.Skipped, Owner), (ImportRowOutcome.Created, null),
            }));
            AssertRowsAddUp(plan);
        });
    }

    [Test]
    public void Reference_Whose_Table_Is_Not_Known_Is_Written_Unchecked()
    {
        var lookup = new Lookup();

        var plan = Plan(Context(Email, [Email, Persona]), lookup, [Record(1, "a@x.test", "a@x.test", "12345")]);

        Assert.Multiple(() =>
        {
            Assert.That(lookup.Calls, Is.Empty);
            Assert.That(plan.Creates.Single().Values[1], Is.EqualTo(12345));
        });
    }

    [Test]
    public void Value_Of_The_Wrong_Type_In_A_Reference_Field_Is_Skipped_Before_The_Check()
    {
        var lookup = new Lookup(1);

        var plan = Plan(Context(Email, countryTargets), lookup,
        [
            Record(1, "a@x.test", "a@x.test", "A", "Slovakia"),
            Record(2, "b@x.test", "b@x.test", "B", "1"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows[0], Is.EqualTo(new ImportRowResult(1, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, Country, "a@x.test")));
            Assert.That(plan.Rows[1].Outcome, Is.EqualTo(ImportRowOutcome.Created));
            Assert.That(lookup.Calls.Single().Values, Is.EqualTo(new object[] { 1 }));
            AssertRowsAddUp(plan);
        });
    }

    [Test]
    public void Row_With_A_Missing_Reference_Does_Not_Take_Part_In_The_Merge_Of_Duplicate_Keys()
    {
        var context = Context(Email, countryTargets);

        var badFirst = Plan(context, new Lookup(1),
        [
            Record(1, "a@x.test", "a@x.test", "First", "999"),
            Record(2, "a@x.test", "a@x.test", "", "1"),
        ]);
        var badLast = Plan(context, new Lookup(1),
        [
            Record(1, "a@x.test", "a@x.test", "First", "1"),
            Record(2, "a@x.test", "a@x.test", "Last", "999"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(badFirst.Rows.Select(r => (r.Outcome, r.Reason)), Is.EqualTo(new (ImportRowOutcome, string?)[]
            {
                (ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue), (ImportRowOutcome.Created, null),
            }));
            Assert.That(badFirst.Creates.Single().Values, Is.EqualTo(new object?[] { "a@x.test", null, 1 }), "Nothing of the skipped row is imported.");
            Assert.That(badFirst.Counts.Get(ImportAggregateKeys.DuplicateRows), Is.Zero);

            Assert.That(badLast.Rows.Select(r => (r.Outcome, r.Reason)), Is.EqualTo(new (ImportRowOutcome, string?)[]
            {
                (ImportRowOutcome.Created, null), (ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue),
            }));
            Assert.That(badLast.Creates.Single().Values, Is.EqualTo(new object?[] { "a@x.test", "First", 1 }));
            AssertRowsAddUp(badFirst);
            AssertRowsAddUp(badLast);
        });
    }

    [Test]
    public void Reference_Field_Can_Be_The_Key_And_Is_Checked_Like_Any_Other()
    {
        var lookup = new Lookup(1);

        var plan = Plan(Context(Owner, [Owner, FirstName]), lookup,
        [
            Record(1, "1", "1", "A"),
            Record(2, "77", "77", "B"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows[0].Outcome, Is.EqualTo(ImportRowOutcome.Created));
            Assert.That(plan.Rows[1], Is.EqualTo(new ImportRowResult(2, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, Owner, "77")));
            AssertRowsAddUp(plan);
        });
    }

    [Test]
    public void Without_A_Check_The_Planner_Behaves_As_Before()
    {
        var planner = new BatchPlanner(Context(Email, countryTargets));

        var plan = planner.Classify(planner.Prepare([Record(1, "a@x.test", "a@x.test", "A", "999")]), []);

        Assert.That(plan.Rows.Single().Outcome, Is.EqualTo(ImportRowOutcome.Created));
    }

    [Test]
    public void Missing_References_Sql_Reads_The_Values_From_Json_And_Quotes_Identifiers_From_Metadata()
    {
        var context = Context(Email, [Email, Country, Owner]);
        var odd = new ImportField(0, "Odd", ImportValueKind.Guid, Reference: new ContactFieldReference(null, "T]; DROP TABLE x; --", "Id]", "s"));

        Assert.Multiple(() =>
        {
            Assert.That(ContactSqlBuilder.BuildMissingReferencesSql(context.Fields[1]), Is.EqualTo(
                "SELECT k.[i] AS [__i] FROM OPENJSON(@keys) WITH ([i] int '$.i', [k] int '$.k') AS k"
                + " WHERE NOT EXISTS (SELECT 1 FROM [CMS_Country] AS r WHERE r.[CountryID] = k.[k])"));
            Assert.That(ContactSqlBuilder.BuildMissingReferencesSql(context.Fields[2]), Does.EndWith("FROM [dbo].[CMS_User] AS r WHERE r.[UserID] = k.[k])"));
            Assert.That(ContactSqlBuilder.BuildMissingReferencesSql(odd), Does.Contain("[k] uniqueidentifier '$.k'").And.EndsWith("FROM [s].[T]]; DROP TABLE x; --] AS r WHERE r.[Id]]] = k.[k])"));
            Assert.That(() => ContactSqlBuilder.BuildMissingReferencesSql(context.Fields[0]), Throws.ArgumentException);
        });
    }

    [Test]
    public void Constraint_Conflict_Is_Sql_Error_547_Anywhere_In_The_Exception_Chain() => Assert.Multiple(() =>
    {
        Assert.That(ImportProcessor.IsConstraintConflict(SqlErrors.Create(547)), Is.True);
        Assert.That(ImportProcessor.IsConstraintConflict(new InvalidOperationException("outer", SqlErrors.Create(547))), Is.True);
        Assert.That(ImportProcessor.IsConstraintConflict(SqlErrors.Create(2601)), Is.False);
        Assert.That(ImportProcessor.IsConstraintConflict(new InvalidOperationException("547")), Is.False);
        Assert.That(ImportProcessor.IsTransient(SqlErrors.Create(547)), Is.False, "A constraint conflict is not retried by later calls.");
    });

    /// <summary>
    /// Processor over a temporary store and an in-memory contact table, for jobs that map a country reference.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(string[]? targets = null)
        {
            Targets = targets ?? countryTargets;
            Temp = new TempStore(options => options.BatchSize = 3);
            var cleanup = new ImportJobCleanupService(Temp.Store, Temp.Options, Time, NullLogger<ImportJobCleanupService>.Instance);
            Processor = new ImportProcessor(Temp.Store, new FakeContactFieldSchemaProvider(Fields), Repository, cleanup, new FakeCacheInvalidator(), Temp.Options, Time, new ListLogger<ImportProcessor>(Log));
            Repository.ReferencedRows["CMS_Country"] = [1, 2];
        }

        public string[] Targets { get; }

        public TempStore Temp { get; }

        public MutableTimeProvider Time { get; } = new(TestData.Now);

        public FakeContactRepository Repository { get; } = new();

        public List<string> Log { get; } = [];

        public ImportProcessor Processor { get; }

        /// <summary>
        /// Stores an Uploaded job; each row has one value per target (<c>null</c> = no value) and the first target is the key.
        /// </summary>
        public Guid Upload(params string?[][] rows)
        {
            var job = TestData.Job(null, Targets[0], Targets);
            Temp.Store.CreateJob(job);

            string[] lines = [.. rows.Select((row, i) => JsonSerializer.Serialize(new
            {
                r = i + 1,
                k = row[0],
                v = Targets.Select((target, t) => (target, value: row[t])).Where(x => x.value is not null).ToDictionary(x => x.target, x => x.value),
            }))];
            byte[] gzipped = TestData.Gzip(string.Join('\n', lines));
            var chunk = new ImportChunkInfo { Index = 0, Sha256 = TestData.Sha256Hex(gzipped), Rows = rows.Length, FirstRow = 1, LastRow = rows.Length };
            Temp.Store.WriteChunk(job.JobId, chunk, gzipped);
            Temp.Store.WriteStatus(job.JobId, new ImportJobStatus
            {
                State = ImportJobState.Uploaded,
                Chunks = [chunk],
                Profile = new ImportProfile { RowsRead = rows.Length, RowsValid = rows.Length, ChunkCount = 1 },
                UpdatedUtc = TestData.Now,
            });

            return job.JobId;
        }

        public ImportProgress Run(Guid jobId, ImportPhase phase)
        {
            string? token = null;
            ImportProgress progress;
            do
            {
                progress = Processor.ProcessNext(jobId, phase, token, false);
                token = progress.DriverToken;
            }
            while (progress.Outcome == ImportProcessOutcome.Processed);

            return progress;
        }

        public ImportProgress Analyze(Guid jobId)
        {
            var analysis = Run(jobId, ImportPhase.Analyze);
            Assert.That(analysis.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), analysis.Error);
            Assert.That(Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));

            return analysis;
        }

        public string[] Report(Guid jobId, ImportPhase phase)
        {
            using var buffer = new MemoryStream();
            Assert.That(new ImportReportReader(Temp.Store).CopyTo(jobId, phase, buffer), Is.GreaterThan(0));
            buffer.Position = 0;
            using var reader = new StreamReader(new GZipStream(buffer, CompressionMode.Decompress));

            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        public void Dispose() => Temp.Dispose();
    }

    private static readonly string?[][] fiveRows =
    [
        ["a@x.test", "A", "1"],
        ["b@x.test", "B", "999"],
        ["c@x.test", "C", ""],
        ["d@x.test", "D", "2"],
        ["e@x.test", "E", "888"],
    ];

    private static void AssertAddsUp(ImportAggregates totals) =>
        Assert.That(totals.Created + totals.Updated + totals.Unchanged + totals.Skipped + totals.DuplicateRows, Is.EqualTo(totals.Rows), "Every row has exactly one outcome.");

    [Test]
    public void Analysis_And_Import_Skip_The_Same_Rows_And_The_Job_Completes()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(fiveRows);

        var analysis = fixture.Analyze(jobId);
        int contactsAfterAnalysis = fixture.Repository.Contacts.Count;
        var import = fixture.Run(jobId, ImportPhase.Execute);

        string[] analyzeReport = fixture.Report(jobId, ImportPhase.Analyze);
        string[] executeReport = fixture.Report(jobId, ImportPhase.Execute);
        Assert.Multiple(() =>
        {
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), import.Error);
            Assert.That(import.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(contactsAfterAnalysis, Is.Zero);
            Assert.That(import.Aggregates.Rows, Is.EqualTo(5));
            Assert.That(import.Aggregates.Created, Is.EqualTo(3));
            Assert.That(import.Aggregates.SkippedInvalidValue, Is.EqualTo(2));
            Assert.That(import.Aggregates.Skipped, Is.EqualTo(2));
            Assert.That(analysis.Aggregates, Is.EqualTo(import.Aggregates).Using<ImportAggregates>((x, y) =>
                x.Rows == y.Rows && x.Created == y.Created && x.Updated == y.Updated && x.Unchanged == y.Unchanged && x.Skipped == y.Skipped && x.SkippedInvalidValue == y.SkippedInvalidValue && x.DuplicateRows == y.DuplicateRows));
            AssertAddsUp(analysis.Aggregates);
            AssertAddsUp(import.Aggregates);
            Assert.That(executeReport, Is.EqualTo(analyzeReport), "The dry run reports what the import does.");
            Assert.That(executeReport, Does.Contain("2,skipped,invalidValue,CountryID").And.Contain("5,skipped,invalidValue,CountryID"));
            Assert.That(executeReport.Where(line => line.Contains("invalidValue", StringComparison.Ordinal)), Has.Exactly(2).Items);
            Assert.That(fixture.Repository.Contacts.Select(c => c.Values[2]), Is.EqualTo(new object?[] { 1, null, 2 }));
            Assert.That(fixture.Repository.ReferenceLookups, Has.Count.EqualTo(4), "Two batches of the analysis and two of the import, one query each.");
            Assert.That(string.Join('\n', fixture.Log), Does.Not.Contain("CONTACT_IMPORT_FAILURE").And.Not.Contain("x.test"));
        });
    }

    [Test]
    public void Referenced_Object_Deleted_Between_Check_And_Write_Is_Checked_Again_And_Its_Rows_Are_Skipped()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(fiveRows);
        fixture.Analyze(jobId);
        fixture.Repository.OnWrite = write =>
        {
            if (write == 1)
            {
                // Country 1 is deleted after the check of the first batch; the database refuses the write.
                fixture.Repository.ReferencedRows["CMS_Country"].Remove(1);
                throw new InvalidOperationException("The INSERT statement conflicted with the FOREIGN KEY constraint. a@x.test", SqlErrors.Create(547));
            }
        };

        var import = fixture.Run(jobId, ImportPhase.Execute);

        string[] report = fixture.Report(jobId, ImportPhase.Execute);
        Assert.Multiple(() =>
        {
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), import.Error);
            Assert.That(import.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(import.Aggregates.Created, Is.EqualTo(2));
            Assert.That(import.Aggregates.SkippedInvalidValue, Is.EqualTo(3));
            AssertAddsUp(import.Aggregates);
            Assert.That(report, Does.Contain("1,skipped,invalidValue,CountryID").And.Contain("2,skipped,invalidValue,CountryID"));
            Assert.That(report.Count(line => line.StartsWith("1,", StringComparison.Ordinal)), Is.EqualTo(1), "The batch is reported once, as it was written.");
            Assert.That(fixture.Repository.Writes, Is.EqualTo(3), "The refused write, its repetition and the second batch.");
            Assert.That(fixture.Repository.Contacts.Select(c => c.Values[0]), Is.EqualTo(new object?[] { "c@x.test", "d@x.test" }));
            Assert.That(string.Join('\n', fixture.Log), Does.Contain("CONTACT_IMPORT_RETRY").And.Contain("SQL error 547").And.Not.Contain("CONTACT_IMPORT_FAILURE").And.Not.Contain("x.test"));
        });
    }

    [Test]
    public void Second_Constraint_Conflict_Of_A_Batch_Fails_The_Job_As_Before()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(fiveRows);
        fixture.Analyze(jobId);
        fixture.Repository.OnWrite = _ => throw new InvalidOperationException("conflict", SqlErrors.Create(547));

        var import = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(import.Error, Is.EqualTo(ImportProcessor.UnexpectedError));
            Assert.That(fixture.Repository.Writes, Is.EqualTo(2), "One repetition, no loop.");
            Assert.That(fixture.Repository.Contacts, Is.Empty);
            Assert.That(fixture.Log, Has.Exactly(1).Contains("CONTACT_IMPORT_FAILURE").And.Contains("SQL error 547"));
        });
    }

    [Test]
    public void Constraint_Conflict_Of_A_Job_Without_Reference_Fields_Fails_It_Right_Away()
    {
        using var fixture = new Fixture([Email, FirstName]);
        var jobId = fixture.Upload(["a@x.test", "A"], ["b@x.test", "B"]);
        fixture.Analyze(jobId);
        fixture.Repository.OnWrite = _ => throw new InvalidOperationException("conflict", SqlErrors.Create(547));

        var import = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(fixture.Repository.Writes, Is.EqualTo(1));
            Assert.That(fixture.Repository.ReferenceLookups, Is.Empty, "A job without reference fields costs no extra query.");
        });
    }

    [Test]
    public void Other_Database_Errors_Of_A_Job_With_Reference_Fields_Keep_Their_Behaviour()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(fiveRows);
        fixture.Analyze(jobId);
        fixture.Repository.OnWrite = _ => throw new InvalidOperationException("duplicate", SqlErrors.Create(2601));

        var import = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(fixture.Repository.Writes, Is.EqualTo(1));
        });
    }
}

/// <summary>
/// Builds a <see cref="SqlException"/> with an error number; the type has no public constructor.
/// </summary>
internal static class SqlErrors
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static SqlException Create(int number)
    {
        var constructor = typeof(SqlError).GetConstructors(Flags)
            .Where(c => c.GetParameters() is [{ } first, ..] && first.ParameterType == typeof(int))
            .OrderBy(c => c.GetParameters().Length)
            .First();
        object?[] arguments = [.. constructor.GetParameters().Select(parameter => DefaultArgument(parameter.ParameterType))];
        arguments[0] = number;
        object error = constructor.Invoke(arguments);

        object errors = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", Flags)!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethods(Flags).First(m =>
            m.Name == "CreateException"
            && m.GetParameters() is [{ } collection, { } version]
            && collection.ParameterType == typeof(SqlErrorCollection)
            && version.ParameterType == typeof(string));

        return (SqlException)create.Invoke(null, [errors, "16.00.0000"])!;
    }

    private static object? DefaultArgument(Type type)
    {
        if (type == typeof(string))
        {
            return "test";
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
