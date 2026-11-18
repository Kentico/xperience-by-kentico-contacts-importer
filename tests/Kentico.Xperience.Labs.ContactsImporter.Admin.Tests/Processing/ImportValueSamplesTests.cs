using Microsoft.Extensions.Logging.Abstractions;

using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

/// <summary>
/// Examples of changed and cleared values (PLAN D57): collected by the analysis, at most three per field and kind with
/// the lowest rows, stored with the uploaded data and deleted with it.
/// </summary>
public class ImportValueSamplesTests
{
    private static readonly string[] targets = [Email, FirstName, LastName];

    private static ImportValueSample Sample(string field, long row, bool cleared = false, string? current = "old", string? value = "new") =>
        new() { Field = field, Row = row, Cleared = cleared, Key = $"user{row}@x.test", Current = current, New = cleared ? null : value };

    [Test]
    public void Planner_Collects_Changed_And_Cleared_Values_Of_Updated_Contacts_Only_When_Asked()
    {
        var context = Context(Email, targets, new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear });
        ImportRecord[] records =
        [
            Record(1, "a@x.test", "a@x.test", "Ann", ""),
            Record(2, "b@x.test", "b@x.test", "Bob", "Brown"),
            Record(3, "new@x.test", "new@x.test", "Nora", "New"),
        ];
        ContactMatch[] matches =
        [
            new(0, 10, TestData.Now, [null, "Anna", "Adams"]),
            new(1, 11, TestData.Now, [null, "Bob", "Black"]),
        ];

        var collecting = new BatchPlanner(context, collectSamples: true);
        var plan = collecting.Classify(collecting.Prepare(records), matches);
        var plain = new BatchPlanner(context);
        var withoutSamples = plain.Classify(plain.Prepare(records), matches);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Samples, Is.EqualTo(new[]
            {
                new ImportValueSample { Field = FirstName, Row = 1, Key = "a@x.test", Current = "Anna", New = "Ann" },
                new ImportValueSample { Field = LastName, Row = 1, Cleared = true, Key = "a@x.test", Current = "Adams", New = null },
                new ImportValueSample { Field = LastName, Row = 2, Key = "b@x.test", Current = "Black", New = "Brown" },
            }));
            Assert.That(withoutSamples.Samples, Is.Empty);
            Assert.That(withoutSamples.Counts.Values, Is.EqualTo(plan.Counts.Values), "Collecting changes no decision.");
        });
    }

    [Test]
    public void Planner_Keeps_Three_Examples_Per_Field_And_Kind_With_The_Lowest_Rows()
    {
        var context = Context(Email, targets);
        var records = Enumerable.Range(1, 8).Select(r => Record(r, $"u{r}@x.test", $"u{r}@x.test", $"New {r}", "")).ToArray();
        var matches = Enumerable.Range(0, 8).Select(i => new ContactMatch(i, 100 + i, TestData.Now, [null, $"Old {i + 1}", "Last"])).ToArray();

        var planner = new BatchPlanner(context, collectSamples: true);
        var plan = planner.Classify(planner.Prepare(records), matches);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Samples.Select(s => (s.Field, s.Row)), Is.EqualTo(new[] { (FirstName, 1L), (FirstName, 2L), (FirstName, 3L) }));
            Assert.That(plan.Samples.Any(s => s.Cleared), Is.False, "Empty values are ignored by default: nothing is cleared.");
        });
    }

    [Test]
    public void Planner_Uses_The_Last_Row_Of_A_Key_And_One_Example_For_Several_Contacts()
    {
        var context = Context(Email, targets, new ImportOptions { MultipleMatches = MultipleMatchesBehavior.UpdateAll });
        ImportRecord[] records =
        [
            Record(4, "a@x.test", "a@x.test", "Ann", ""),
            Record(9, "a@x.test", "a@x.test", "", "Adams"),
        ];
        ContactMatch[] matches =
        [
            new(0, 21, TestData.Now, [null, "One", "X"]),
            new(0, 20, TestData.Now, [null, "Two", "Y"]),
        ];

        var planner = new BatchPlanner(context, collectSamples: true);
        var plan = planner.Classify(planner.Prepare(records), matches);

        Assert.That(plan.Samples, Is.EqualTo(new[]
        {
            new ImportValueSample { Field = FirstName, Row = 9, Key = "a@x.test", Current = "Two", New = "Ann" },
            new ImportValueSample { Field = LastName, Row = 9, Key = "a@x.test", Current = "Y", New = "Adams" },
        }));
    }

    [Test]
    public void Set_Gives_The_Same_Examples_In_Any_Order_And_With_Repeated_Rows()
    {
        var samples = Enumerable.Range(1, 6).SelectMany(r => new[] { Sample(FirstName, r), Sample(FirstName, r, cleared: true), Sample(LastName, 10 - r) }).ToList();
        var expected = ImportValueSamples.Select(samples);

        var reversed = ImportValueSamples.Select(Enumerable.Reverse(samples));
        var repeated = ImportValueSamples.Select([.. samples, .. samples.Select(s => s with { Current = "processed again" })]);

        Assert.Multiple(() =>
        {
            Assert.That(expected.Select(s => (s.Field, s.Cleared, s.Row)), Is.EqualTo(new[]
            {
                (FirstName, true, 1L), (FirstName, true, 2L), (FirstName, true, 3L),
                (FirstName, false, 1L), (FirstName, false, 2L), (FirstName, false, 3L),
                (LastName, false, 4L), (LastName, false, 5L), (LastName, false, 6L),
            }));
            Assert.That(reversed, Is.EqualTo(expected));
            Assert.That(repeated, Is.EqualTo(expected), "A row already kept is not taken again.");
        });
    }

    [Test]
    public void Values_Are_Formatted_Simply_And_Cut()
    {
        var job = TestData.Job(null, Email, Email, "Day", "Active", "Total", "Uid", "ContactBirthday");
        Assert.That(ImportProcessingContext.TryCreate(job, Fields, out var context, out _, ImportAudience.None), Is.True);
        var fields = context!.Fields;

        Assert.Multiple(() =>
        {
            Assert.That(ImportValueSamples.Format(fields[1], new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Unspecified)), Is.EqualTo("2026-03-04"));
            Assert.That(ImportValueSamples.Format(fields[2], true), Is.EqualTo("true"));
            Assert.That(ImportValueSamples.Format(fields[2], 0), Is.EqualTo("false"));
            Assert.That(ImportValueSamples.Format(fields[3], 12.5m), Is.EqualTo("12.5"));
            Assert.That(ImportValueSamples.Format(fields[4], Guid.Empty), Is.EqualTo("00000000-0000-0000-0000-000000000000"));
            Assert.That(ImportValueSamples.Format(fields[5], new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified)), Is.EqualTo("2026-03-04 05:06:07"));
            Assert.That(ImportValueSamples.Format(fields[0], DBNull.Value), Is.Null);
            Assert.That(ImportValueSamples.Format(fields[0], ""), Is.Null);
            Assert.That(ImportValueSamples.Format(fields[0], new string('x', 500)), Has.Length.EqualTo(ImportValueSamples.MaxValueLength).And.EndWith("…"));
        });
    }

    /// <summary>Processor over a temporary store with existing contacts for every row.</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(Action<ContactsImporterOptions>? configure = null)
        {
            Temp = new TempStore(options =>
            {
                options.BatchSize = 2;
                configure?.Invoke(options);
            });
            Cleanup = new ImportJobCleanupService(Temp.Store, Temp.Options, Time, NullLogger<ImportJobCleanupService>.Instance);
            Processor = new ImportProcessor(Temp.Store, new FakeContactFieldSchemaProvider(Fields), Repository, Cleanup, new FakeCacheInvalidator(), Temp.Options, Time, NullLogger<ImportProcessor>.Instance);
        }

        public TempStore Temp { get; }

        public ImportJobStore Store => Temp.Store;

        public MutableTimeProvider Time { get; } = new(TestData.Now);

        public FakeContactRepository Repository { get; } = new();

        public ImportJobCleanupService Cleanup { get; }

        public ImportProcessor Processor { get; }

        /// <summary>
        /// Stores an Uploaded job of <paramref name="rowCount"/> rows: odd rows change the first name, even rows have an
        /// empty one. Every row has a contact with the first name <c>Old {n}</c>.
        /// </summary>
        public Guid Upload(int rowCount, ImportOptions? options = null)
        {
            var job = TestData.Job(null, Email, Email, FirstName) with { Options = options ?? new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear } };
            Store.CreateJob(job);

            byte[] gzipped = TestData.Gzip(string.Join('\n', Enumerable.Range(1, rowCount).Select(r => TestData.Record(r, $"user{r}@x.test", r % 2 == 1 ? $"New {r}" : ""))));
            var chunk = new ImportChunkInfo { Index = 0, Sha256 = TestData.Sha256Hex(gzipped), Rows = rowCount, FirstRow = 1, LastRow = rowCount };
            Store.WriteChunk(job.JobId, chunk, gzipped);
            Store.WriteStatus(job.JobId, new ImportJobStatus
            {
                State = ImportJobState.Uploaded,
                Chunks = [chunk],
                Profile = new ImportProfile { RowsRead = rowCount, RowsValid = rowCount, ChunkCount = 1 },
                UpdatedUtc = TestData.Now,
            });

            var context = Context(Email, [Email, FirstName]);
            for (int r = 1; r <= rowCount; r++)
            {
                Repository.Add(context, TestData.Now, $"user{r}@x.test", $"Old {r}");
            }

            return job.JobId;
        }

        public ImportProgress Run(Guid jobId, ImportPhase phase, string? token = null)
        {
            ImportProgress progress;
            do
            {
                progress = Processor.ProcessNext(jobId, phase, token, false);
                token = progress.DriverToken;
            }
            while (progress.Outcome == ImportProcessOutcome.Processed);

            return progress;
        }

        public IReadOnlyList<ImportValueSample> Samples(Guid jobId) => ImportValueSamples.Read(Store, jobId);

        public void Dispose() => Temp.Dispose();
    }

    private static IReadOnlyList<ImportValueSample> Reference(int rowCount)
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(rowCount);
        Assert.That(fixture.Run(jobId, ImportPhase.Analyze).Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));

        return fixture.Samples(jobId);
    }

    [Test]
    public void Analysis_Stores_Three_Changed_And_Three_Cleared_Examples_Per_Field()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(12);

        fixture.Run(jobId, ImportPhase.Analyze);
        var samples = fixture.Samples(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(samples, Is.EqualTo(new[]
            {
                new ImportValueSample { Field = FirstName, Row = 2, Cleared = true, Key = "user2@x.test", Current = "Old 2" },
                new ImportValueSample { Field = FirstName, Row = 4, Cleared = true, Key = "user4@x.test", Current = "Old 4" },
                new ImportValueSample { Field = FirstName, Row = 6, Cleared = true, Key = "user6@x.test", Current = "Old 6" },
                new ImportValueSample { Field = FirstName, Row = 1, Key = "user1@x.test", Current = "Old 1", New = "New 1" },
                new ImportValueSample { Field = FirstName, Row = 3, Key = "user3@x.test", Current = "Old 3", New = "New 3" },
                new ImportValueSample { Field = FirstName, Row = 5, Key = "user5@x.test", Current = "Old 5", New = "New 5" },
            }));
            Assert.That(fixture.Store.ListSampleParts(jobId), Is.EqualTo(new long[] { 1, 3, 5 }), "Batches whose examples are not needed store no part.");
            Assert.That(Directory.GetFiles(fixture.Temp.JobPath(jobId, "chunks"), "samples-*"), Has.Length.EqualTo(3));
            Assert.That(fixture.Store.ListChunks(jobId), Has.Count.EqualTo(1), "Example parts are not chunks.");
        });
    }

    [Test]
    public void Examples_Are_The_Same_However_The_Analysis_Is_Split_Into_Calls()
    {
        var expected = Reference(12);
        using var fixture = new Fixture(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = fixture.Upload(12);
        fixture.Repository.OnLookup = _ => fixture.Time.Advance(TimeSpan.FromSeconds(2));

        var last = fixture.Run(jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(last.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(fixture.Repository.Lookups, Is.EqualTo(6), "One batch per call.");
            Assert.That(fixture.Samples(jobId), Is.EqualTo(expected));
        });
    }

    [Test]
    public void Batches_Processed_Again_After_A_Lost_Checkpoint_Give_The_Same_Examples()
    {
        var expected = Reference(12);
        using var fixture = new Fixture(options => options.ProcessingTimeBudgetSeconds = 10);
        var jobId = fixture.Upload(12);
        fixture.Repository.OnLookup = _ => fixture.Time.Advance(TimeSpan.FromSeconds(6));

        // Two batches (rows 1-4), then the checkpoint of the second batch is lost: rows 3-4 are processed again.
        var first = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        fixture.Store.UpdateStatus(jobId, status => status with { Checkpoint = status.Checkpoint with { RowOffset = 2, LastRow = 2 } });

        // A call that was replaced also left a part with other values beyond the checkpoint.
        fixture.Store.WriteSamplePart(jobId, 5, [Sample(FirstName, 5, current: "stale"), Sample(FirstName, 7, cleared: true, current: "stale")]);

        var last = fixture.Run(jobId, ImportPhase.Analyze, first.DriverToken);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(last.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(fixture.Samples(jobId), Is.EqualTo(expected));
            Assert.That(fixture.Samples(jobId).Select(s => s.Current), Has.None.EqualTo("stale"));
        });
    }

    [Test]
    public void New_Analysis_Replaces_The_Examples_Of_The_Previous_One()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(6);
        fixture.Run(jobId, ImportPhase.Analyze);
        var before = fixture.Samples(jobId);

        // An options change returns the job to Uploaded; empty values are now ignored and nothing else changes.
        fixture.Store.WriteJob(fixture.Store.ReadJob(jobId)! with { Options = new ImportOptions() });
        fixture.Store.UpdateStatus(jobId, status => ImportJobStateMachine.Transition(status, ImportJobState.Uploaded, TestData.Now));
        foreach (var contact in fixture.Repository.Contacts.Where(c => c.Id % 2 == 1))
        {
            contact.Values[1] = $"New {contact.Id}";
        }

        fixture.Run(jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(before, Has.Count.EqualTo(6));
            Assert.That(fixture.Samples(jobId), Is.Empty);
            Assert.That(fixture.Store.ListSampleParts(jobId), Is.Empty);
        });
    }

    [Test]
    public void Examples_Are_Deleted_With_The_Uploaded_Data_And_The_Import_Collects_None()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(6);
        fixture.Run(jobId, ImportPhase.Analyze);
        var parts = fixture.Store.ListSampleParts(jobId);
        Assert.That(fixture.Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
        var partsDuringImport = new List<IReadOnlyList<long>>();
        fixture.Repository.OnWrite = _ => partsDuringImport.Add(fixture.Store.ListSampleParts(jobId));

        var done = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(parts, Is.Not.Empty);
            Assert.That(done.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(partsDuringImport, Is.Not.Empty.And.All.EqualTo(parts));
            Assert.That(fixture.Store.ListSampleParts(jobId), Is.Empty);
            Assert.That(fixture.Samples(jobId), Is.Empty);
        });
    }

    [Test]
    public void Cancel_And_Sweep_Delete_The_Examples()
    {
        using var fixture = new Fixture();
        var cancelled = fixture.Upload(4);
        fixture.Run(cancelled, ImportPhase.Analyze);
        var finished = fixture.Upload(4);
        fixture.Run(finished, ImportPhase.Analyze);
        fixture.Store.UpdateStatus(finished, status => ImportJobStateMachine.Transition(status, ImportJobState.Cancelled, TestData.Now));

        // Left behind by a call that was still analyzing when the job finished.
        Assert.That(fixture.Store.ListSampleParts(finished), Is.Not.Empty);
        fixture.Store.DeleteChunk(finished, 0);

        fixture.Processor.Cancel(cancelled);
        var sweep = fixture.Cleanup.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Store.ListSampleParts(cancelled), Is.Empty);
            Assert.That(fixture.Store.ListSampleParts(finished), Is.Empty);
            Assert.That(sweep.ChunksDeleted, Is.EqualTo(1));
        });
    }

    [Test]
    public void Example_Part_Is_Not_Written_When_The_Uploaded_Data_Is_Gone()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(2);
        fixture.Store.DeleteChunks(jobId);

        fixture.Store.WriteSamplePart(jobId, 1, [Sample(FirstName, 1)]);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Store.ListSampleParts(jobId), Is.Empty);
            Assert.That(Directory.Exists(fixture.Temp.JobPath(jobId, "chunks")), Is.False);
        });
    }
}
