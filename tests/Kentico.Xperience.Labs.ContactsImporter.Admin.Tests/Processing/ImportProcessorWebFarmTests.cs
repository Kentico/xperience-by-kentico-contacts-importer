using Microsoft.Extensions.Logging.Abstractions;

using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

/// <summary>
/// Two application instances (two processors, each with its own store and in-process lock) over one shared folder
/// and one contact table: cancel, take over and repeated calls on one instance while the other is inside a batch,
/// slow batches, and configuration problems found before the import writes.
/// </summary>
public class ImportProcessorWebFarmTests
{
    private sealed class SchemaProvider(ContactFieldDefinition[] fields) : IContactFieldSchemaProvider
    {
        public IReadOnlyList<ContactFieldDefinition> Fields { get; set; } = fields;

        public Exception? Throw { get; set; }

        public int Reads { get; private set; }

        public IReadOnlyList<ContactFieldDefinition> GetMappableFields()
        {
            Reads++;

            return Throw is null ? Fields : throw Throw;
        }
    }

    private sealed class Farm : IDisposable
    {
        public Farm(Action<ContactsImporterOptions>? configure = null)
        {
            Temp = new TempStore(options =>
            {
                options.BatchSize = 2;
                configure?.Invoke(options);
            });
            StoreB = Temp.NewStore();
            A = Create(Temp.Store, LogA);
            B = Create(StoreB, LogB);
        }

        public TempStore Temp { get; }

        public ImportJobStore StoreB { get; }

        public MutableTimeProvider Time { get; } = new(TestData.Now);

        public SchemaProvider Schema { get; } = new(Fields);

        public FakeContactRepository Repository { get; } = new();

        public FakeCacheInvalidator Cache { get; } = new();

        public List<string> LogA { get; } = [];

        public List<string> LogB { get; } = [];

        public ImportProcessor A { get; }

        public ImportProcessor B { get; }

        /// <summary>
        /// Stores an Uploaded job with <paramref name="rowCount"/> rows (<c>user{n}@x.test</c>), one chunk.
        /// </summary>
        public Guid Upload(int rowCount)
        {
            var job = TestData.Job(null, Email, Email, FirstName);
            Temp.Store.CreateJob(job);
            byte[] gzipped = TestData.Gzip(string.Join('\n', Enumerable.Range(1, rowCount).Select(r => TestData.Record(r, $"user{r}@x.test", $"Name {r}"))));
            var chunk = new ImportChunkInfo { Index = 0, Sha256 = TestData.Sha256Hex(gzipped), Rows = rowCount, FirstRow = 1, LastRow = rowCount };
            Temp.Store.WriteChunk(job.JobId, chunk, gzipped);
            Temp.Store.WriteStatus(job.JobId, new ImportJobStatus
            {
                State = ImportJobState.Uploaded,
                Chunks = [chunk],
                Profile = new ImportProfile { RowsRead = rowCount, RowsValid = rowCount, ChunkCount = 1 },
                UpdatedUtc = TestData.Now,
            });

            return job.JobId;
        }

        public Guid UploadAnalyzedAndConfirmed(int rowCount)
        {
            var jobId = Upload(rowCount);
            Assert.That(Run(A, jobId, ImportPhase.Analyze).Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(A.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));

            return jobId;
        }

        public static ImportProgress Run(ImportProcessor processor, Guid jobId, ImportPhase phase, string? token = null)
        {
            ImportProgress progress;
            do
            {
                progress = processor.ProcessNext(jobId, phase, token, false);
                token = progress.DriverToken;
            }
            while (progress.Outcome == ImportProcessOutcome.Processed);

            return progress;
        }

        public ImportJobStatus Status(Guid jobId) => StoreB.ReadStatus(jobId)!;

        public void Dispose() => Temp.Dispose();

        private ImportProcessor Create(ImportJobStore store, List<string> log) =>
            new(store, Schema, Repository, new ImportJobCleanupService(store, Temp.Options, Time, NullLogger<ImportJobCleanupService>.Instance), Cache, Temp.Options, Time, new ListLogger<ImportProcessor>(log));
    }

    /// <summary>Runs <paramref name="action"/> the first time the returned hook is called.</summary>
    private static Action<int> Once(Action action)
    {
        bool done = false;

        return _ =>
        {
            if (!done)
            {
                done = true;
                action();
            }
        };
    }

    [Test]
    public void Cancel_On_Another_Instance_During_The_Lookup_Stops_The_Driver_Before_It_Writes()
    {
        using var farm = new Farm();
        var jobId = farm.UploadAnalyzedAndConfirmed(6);
        ImportCancelResult? cancel = null;
        farm.Repository.OnLookup = Once(() => cancel = farm.B.Cancel(jobId));

        var progress = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(cancel!.Outcome, Is.EqualTo(ImportCancelOutcome.Cancelled));
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(farm.Repository.Writes, Is.Zero, "The batch whose lookup was running when the job was cancelled is not written.");
            Assert.That(farm.Repository.Contacts, Is.Empty);
            Assert.That(farm.Status(jobId).State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(farm.Temp.Store.HasChunks(jobId), Is.False);
            Assert.That(farm.Cache.Calls, Is.Zero);
        });
    }

    [Test]
    public void Cancel_On_Another_Instance_During_The_Write_Is_Not_Overwritten_By_The_Checkpoint()
    {
        using var farm = new Farm();
        var jobId = farm.UploadAnalyzedAndConfirmed(6);
        farm.Repository.OnWrite = Once(() => farm.B.Cancel(jobId));

        var progress = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);
        var again = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, true);

        var status = farm.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(farm.Repository.Writes, Is.EqualTo(1), "The write in progress finishes, no further batch starts.");
            Assert.That(status.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(status.Checkpoint.LastRow, Is.Zero, "The checkpoint of the batch was refused.");
            Assert.That(again.Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
            Assert.That(File.Exists(farm.Temp.JobPath(jobId, ImportJobStore.FinalStatusFileName)), Is.True);
        });
    }

    [Test]
    public void Take_Over_While_A_Call_Still_Imports_Waits_For_The_Job_Lock_And_Writes_Nothing()
    {
        using var farm = new Farm(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = farm.UploadAnalyzedAndConfirmed(6);
        ImportProgress? takeOver = null;
        bool slowLookup = true;
        farm.Repository.OnLookup = _ =>
        {
            if (slowLookup)
            {
                // The lookup of the driver hangs longer than the heartbeat age; the marketer continues in another tab.
                slowLookup = false;
                farm.Time.Advance(TimeSpan.FromSeconds(61));
                takeOver = farm.B.ProcessNext(jobId, ImportPhase.Execute, null, true);
            }
            else
            {
                farm.Time.Advance(TimeSpan.FromSeconds(2));
            }
        };

        var driver = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        var status = farm.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(takeOver!.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError), "The call that imports holds the job lock.");
            Assert.That(takeOver.Error, Is.EqualTo(ImportProcessor.BusyError));
            Assert.That(takeOver.DriverToken, Is.Null);
            Assert.That(driver.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(driver.RowsProcessed, Is.EqualTo(2));
            Assert.That(farm.Repository.Lookups - 3, Is.EqualTo(1), "The refused call looked nothing up (3 lookups were the analysis).");
            Assert.That(farm.Repository.Writes, Is.EqualTo(1));
            Assert.That(farm.Repository.Contacts, Has.Count.EqualTo(2), "No contact was created twice.");
            Assert.That(status.State, Is.EqualTo(ImportJobState.Running));
            Assert.That(status.DriverToken, Is.EqualTo(driver.DriverToken), "The refused call changed nothing in the status.");
            Assert.That(farm.Repository.LockRefusals, Is.EqualTo(1));
            Assert.That(farm.Repository.LockedJobs, Is.Empty, "The lock is released when the call returns.");
        });
    }

    [Test]
    public void Repeated_Import_Call_Does_Nothing_While_The_First_Still_Runs_And_Continues_After_It()
    {
        using var farm = new Farm(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = farm.UploadAnalyzedAndConfirmed(6);
        farm.Repository.OnLookup = _ => farm.Time.Advance(TimeSpan.FromSeconds(2));
        var started = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        // The browser got no response to its next call and sends it again; the repeated call reaches another instance
        // while the first one is still inside its batch: once during the lookup, once during the write.
        ImportProgress? duringLookup = null;
        ImportProgress? duringWrite = null;
        farm.Repository.OnLookup = Once(() =>
        {
            farm.Time.Advance(TimeSpan.FromSeconds(2));
            duringLookup = farm.B.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false);
        });
        farm.Repository.OnWrite = Once(() => duringWrite = farm.B.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false));

        var stillRunning = farm.A.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false);

        var afterOverlap = farm.Status(jobId);
        farm.Repository.OnLookup = null;
        var repeatedAgain = Farm.Run(farm.B, jobId, ImportPhase.Execute, started.DriverToken);

        var totals = ImportAggregates.From(farm.Status(jobId).Aggregates);
        Assert.Multiple(() =>
        {
            Assert.That(duringLookup!.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(duringLookup.DriverToken, Is.EqualTo(started.DriverToken), "The browser keeps its token for the next attempt.");
            Assert.That(duringLookup.Error, Is.EqualTo(ImportProcessor.BusyError));
            Assert.That(duringWrite!.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(stillRunning.Outcome, Is.EqualTo(ImportProcessOutcome.Processed), "The first call is not disturbed.");
            Assert.That(afterOverlap.Aggregates[ImportAggregateKeys.Rows], Is.EqualTo(4));
            Assert.That(afterOverlap.Aggregates[ImportAggregateKeys.Created], Is.EqualTo(4));
            Assert.That(farm.Repository.LockRefusals, Is.EqualTo(2));
            Assert.That(repeatedAgain.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), "The repeated call continues once the first one returned.");
            Assert.That(farm.Repository.Writes, Is.EqualTo(3), "Every batch was written exactly once.");
            Assert.That(farm.Repository.Contacts, Has.Count.EqualTo(6));
            Assert.That(farm.Repository.Contacts.Select(c => c.Values[0]), Is.Unique);
            Assert.That(totals.Rows, Is.EqualTo(6));
            Assert.That(totals.Created, Is.EqualTo(6));
            Assert.That(farm.Repository.LockedJobs, Is.Empty);
        });
    }

    [Test]
    public void Call_For_A_Job_Whose_Lock_Is_Held_Still_Learns_That_The_Job_Is_Driven_Elsewhere_Or_Finished()
    {
        using var farm = new Farm();
        var jobId = farm.UploadAnalyzedAndConfirmed(6);
        ImportProgress? otherTab = null;
        ImportProgress? afterCancel = null;
        farm.Repository.OnWrite = Once(() =>
        {
            otherTab = farm.B.ProcessNext(jobId, ImportPhase.Execute, null, true);
            farm.B.Cancel(jobId);
            afterCancel = farm.B.ProcessNext(jobId, ImportPhase.Execute, null, true);
        });

        var driver = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(otherTab!.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere));
            Assert.That(afterCancel!.Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
            Assert.That(afterCancel.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(driver.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted));
            Assert.That(farm.Repository.LockRefusals, Is.EqualTo(2));
            Assert.That(farm.Repository.LockedJobs, Is.Empty);
        });
    }

    [Test]
    public void Error_Taking_The_Job_Lock_Is_Temporary_And_Changes_Nothing()
    {
        using var farm = new Farm();
        var jobId = farm.UploadAnalyzedAndConfirmed(4);
        long version = farm.Status(jobId).Version;
        farm.Repository.OnLock = () => throw new InvalidOperationException("secret@contact.test");

        var failed = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        var status = farm.Status(jobId);
        int writesAfterFailure = farm.Repository.Writes;
        farm.Repository.OnLock = null;
        var import = Farm.Run(farm.A, jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Confirmed));
            Assert.That(status.Version, Is.EqualTo(version));
            Assert.That(writesAfterFailure, Is.Zero);
            Assert.That(string.Join('\n', farm.LogA), Does.Not.Contain("secret@contact.test"));
            Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
        });
    }

    [Test]
    public void Job_Lock_Is_Released_After_Every_Way_An_Import_Call_Ends()
    {
        using var farm = new Farm();
        var failing = farm.UploadAnalyzedAndConfirmed(4);
        farm.Repository.OnWrite = _ => throw new InvalidOperationException("boom");
        var failed = farm.A.ProcessNext(failing, ImportPhase.Execute, null, false);
        bool lockedAfterFailure = farm.Repository.LockedJobs.Count > 0;

        var temporary = farm.UploadAnalyzedAndConfirmed(4);
        farm.Repository.OnWrite = _ => throw new TimeoutException();
        var transient = farm.A.ProcessNext(temporary, ImportPhase.Execute, null, false);
        bool lockedAfterTemporaryError = farm.Repository.LockedJobs.Count > 0;

        farm.Repository.OnWrite = null;
        var finished = Farm.Run(farm.B, temporary, ImportPhase.Execute, transient.DriverToken);
        var again = farm.B.ProcessNext(temporary, ImportPhase.Execute, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(lockedAfterFailure, Is.False);
            Assert.That(transient.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(lockedAfterTemporaryError, Is.False);
            Assert.That(finished.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(again.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(farm.Repository.LockedJobs, Is.Empty);
            Assert.That(farm.Repository.LockRefusals, Is.Zero);
        });
    }

    [Test]
    public void Analysis_Takes_No_Job_Lock_And_A_Repeated_Call_Replaces_The_Call_That_Still_Runs()
    {
        using var farm = new Farm(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = farm.Upload(6);
        farm.Repository.OnLookup = _ => farm.Time.Advance(TimeSpan.FromSeconds(2));
        var started = farm.A.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        ImportProgress? repeated = null;
        bool lockedDuringAnalysis = false;
        bool first = true;
        farm.Repository.OnLookup = _ =>
        {
            farm.Time.Advance(TimeSpan.FromSeconds(2));
            if (first)
            {
                first = false;
                lockedDuringAnalysis = farm.Repository.LockedJobs.Count > 0;
                repeated = farm.B.ProcessNext(jobId, ImportPhase.Analyze, started.DriverToken, false);
            }
        };

        var stillRunning = farm.A.ProcessNext(jobId, ImportPhase.Analyze, started.DriverToken, false);

        var status = farm.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(lockedDuringAnalysis, Is.False);
            Assert.That(repeated!.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(repeated.DriverToken, Is.EqualTo(started.DriverToken), "The browser keeps its token.");
            Assert.That(stillRunning.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted), "The checkpoint of the older call is refused.");
            Assert.That(status.State, Is.EqualTo(ImportJobState.Analyzing));
            Assert.That(status.Aggregates[ImportAggregateKeys.Rows], Is.EqualTo(4), "The batch both calls analyzed is counted once.");
            Assert.That(Farm.Run(farm.B, jobId, ImportPhase.Analyze).Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere), "Another tab is still refused.");
        });
    }

    [Test]
    public void Error_In_A_Call_That_No_Longer_Drives_The_Job_Does_Not_Fail_It()
    {
        using var farm = new Farm(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = farm.Upload(6);
        farm.Repository.OnLookup = _ => farm.Time.Advance(TimeSpan.FromSeconds(2));
        var started = farm.A.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        bool first = true;
        farm.Repository.OnLookup = _ =>
        {
            farm.Time.Advance(TimeSpan.FromSeconds(2));
            if (first)
            {
                first = false;
                farm.B.ProcessNext(jobId, ImportPhase.Analyze, started.DriverToken, false);

                throw new InvalidOperationException("secret@contact.test");
            }
        };

        var replaced = farm.A.ProcessNext(jobId, ImportPhase.Analyze, started.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(replaced.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted));
            Assert.That(farm.Status(jobId).State, Is.EqualTo(ImportJobState.Analyzing));
            Assert.That(farm.Temp.Store.HasChunks(jobId), Is.True);
            Assert.That(string.Join('\n', farm.LogA), Does.Not.Contain("secret@contact.test"));
        });
    }

    [Test]
    public void Batch_Slower_Than_The_Heartbeat_Age_Is_Not_Taken_Over_While_It_Is_Written()
    {
        using var farm = new Farm();
        var jobId = farm.UploadAnalyzedAndConfirmed(6);
        ImportProgress? duringWrite = null;
        farm.Repository.OnLookup = Once(() => farm.Time.Advance(TimeSpan.FromSeconds(50)));
        farm.Repository.OnWrite = Once(() =>
        {
            // 50 s lookup + 50 s write: 100 s after the call started, which is more than the 60 s heartbeat age.
            farm.Time.Advance(TimeSpan.FromSeconds(50));
            duringWrite = farm.B.ProcessNext(jobId, ImportPhase.Execute, null, true);
        });

        var first = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        var afterBatch = farm.Status(jobId);
        farm.Time.Advance(TimeSpan.FromSeconds(150));
        var quietFor150 = farm.B.ProcessNext(jobId, ImportPhase.Execute, null, true);
        var shown = farm.B.GetProgress(jobId)!;
        farm.Time.Advance(TimeSpan.FromSeconds(51));
        var quietFor201 = farm.B.ProcessNext(jobId, ImportPhase.Execute, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(duringWrite!.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere), "The driver sent a heartbeat right before the write.");
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed), "The time budget is used after the slow batch.");
            Assert.That(first.RowsProcessed, Is.EqualTo(2));
            Assert.That(farm.Repository.Contacts, Has.Count.EqualTo(2));
            Assert.That(afterBatch.LongestBatchSeconds, Is.EqualTo(100));
            Assert.That(quietFor150.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere), "After a 100 s batch the driver counts as gone after 200 s, not 60 s.");
            Assert.That(shown.State, Is.EqualTo(ImportJobState.Running));
            Assert.That(quietFor201.Outcome, Is.EqualTo(ImportProcessOutcome.StaleRequiresTakeOver));
        });
    }

    [Test]
    public void Configuration_Problem_At_The_Start_Of_The_Analysis_Returns_The_Job_To_Uploaded_With_Its_Data()
    {
        using var farm = new Farm();
        var jobId = farm.Upload(4);
        farm.Schema.Fields = [.. Fields.Where(f => f.Name != FirstName)];

        var rejected = farm.A.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        var status = farm.Status(jobId);
        farm.Schema.Fields = Fields;
        var analyzed = Farm.Run(farm.A, jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(rejected.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(rejected.Error?.ToString(), Does.Contain("no longer exists"));
            Assert.That(rejected.DriverToken, Is.Null);
            Assert.That(status.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(status.ErrorMessage?.ToString(), Does.Contain("no longer exists"));
            Assert.That(status.DriverToken, Is.Null);
            Assert.That(status.FinishedUtc, Is.Null);
            Assert.That(farm.LogA, Has.Some.Contains("CONTACT_IMPORT_REJECTED"));
            Assert.That(farm.Repository.Lookups, Is.EqualTo(2), "Only the analysis after the fix read contacts.");
            Assert.That(analyzed.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), "The same upload is analyzed after the fix.");
            Assert.That(analyzed.Error, Is.Null);
            Assert.That(farm.Status(jobId).Error, Is.Null);
            Assert.That(farm.Temp.Store.HasChunks(jobId), Is.True);
        });
    }

    [Test]
    public void Configuration_Problem_During_The_Analysis_Returns_The_Job_To_Uploaded_With_Its_Data()
    {
        using var farm = new Farm(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = farm.Upload(6);
        farm.Repository.OnLookup = _ => farm.Time.Advance(TimeSpan.FromSeconds(2));
        var first = farm.A.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        farm.Schema.Fields = [.. Fields.Where(f => f.Name != FirstName)];

        var rejected = farm.A.ProcessNext(jobId, ImportPhase.Analyze, first.DriverToken, false);

        var status = farm.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(rejected.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(status.Aggregates, Is.Empty);
            Assert.That(status.Checkpoint.LastRow, Is.Zero);
            Assert.That(farm.Temp.Store.HasChunks(jobId), Is.True);
        });
    }

    [Test]
    public void Configuration_Problem_At_The_Start_Of_The_Import_Returns_The_Job_To_Uploaded_With_Its_Data()
    {
        using var farm = new Farm();
        var jobId = farm.UploadAnalyzedAndConfirmed(4);
        farm.Schema.Fields = [.. Fields.Select(f => f.Name == FirstName ? f with { DataType = "binary" } : f)];

        var rejected = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        var status = farm.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(rejected.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(rejected.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(rejected.Error?.ToString(), Does.Contain("data type"));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(status.Analysis, Is.Null, "The import needs a new analysis.");
            Assert.That(farm.Repository.Writes, Is.Zero);
            Assert.That(farm.Temp.Store.HasChunks(jobId), Is.True);
            Assert.That(farm.Cache.Calls, Is.Zero);
        });
    }

    [Test]
    public void Configuration_Problem_While_The_Import_Writes_Fails_The_Job_And_Deletes_Its_Data()
    {
        using var farm = new Farm(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = farm.UploadAnalyzedAndConfirmed(6);
        farm.Repository.OnLookup = _ => farm.Time.Advance(TimeSpan.FromSeconds(2));
        var first = farm.A.ProcessNext(jobId, ImportPhase.Execute, null, false);
        farm.Schema.Fields = [.. Fields.Where(f => f.Name != FirstName)];

        var failed = farm.A.ProcessNext(jobId, ImportPhase.Execute, first.DriverToken, false);

        var status = farm.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(failed.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(failed.State, Is.EqualTo(ImportJobState.Failed));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Failed));
            Assert.That(status.ErrorMessage?.ToString(), Does.Contain("no longer exists"));
            Assert.That(status.Aggregates[ImportAggregateKeys.Rows], Is.EqualTo(2), "The counts of what was written stay.");
            Assert.That(farm.Temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [Test]
    public void Caller_That_May_Not_Drive_The_Job_Cannot_Reset_It_With_A_Configuration_Problem()
    {
        using var farm = new Farm(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = farm.Upload(6);
        farm.Repository.OnLookup = _ => farm.Time.Advance(TimeSpan.FromSeconds(2));
        farm.A.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        farm.Schema.Fields = [.. Fields.Where(f => f.Name != FirstName)];
        int schemaReads = farm.Schema.Reads;

        var otherTab = farm.B.ProcessNext(jobId, ImportPhase.Analyze, null, true);

        Assert.Multiple(() =>
        {
            Assert.That(otherTab.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere));
            Assert.That(farm.Status(jobId).State, Is.EqualTo(ImportJobState.Analyzing));
            Assert.That(farm.Schema.Reads, Is.EqualTo(schemaReads), "The configuration is only checked for the caller that may process.");
        });
    }

    [Test]
    public void Temporary_Error_Reading_The_Contact_Class_Changes_Nothing()
    {
        using var farm = new Farm();
        var jobId = farm.Upload(2);
        farm.Schema.Throw = new TimeoutException("secret@contact.test");

        var progress = farm.A.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        var status = farm.Status(jobId);
        farm.Schema.Throw = null;
        var analyzed = Farm.Run(farm.A, jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(progress.Error, Is.EqualTo(ImportProcessor.TransientError));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(status.Version, Is.Zero, "Nothing was written.");
            Assert.That(string.Join('\n', farm.LogA), Does.Not.Contain("secret@contact.test"));
            Assert.That(analyzed.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
        });
    }

    [Test]
    public void Unexpected_Error_Reading_The_Contact_Class_Fails_The_Job()
    {
        using var farm = new Farm();
        var jobId = farm.Upload(2);
        farm.Schema.Throw = new InvalidOperationException("secret@contact.test");

        var progress = farm.A.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Failed));
            Assert.That(progress.Error, Is.EqualTo(ImportProcessor.UnexpectedError));
            Assert.That(string.Join('\n', farm.LogA), Does.Not.Contain("secret@contact.test"));
        });
    }
}
