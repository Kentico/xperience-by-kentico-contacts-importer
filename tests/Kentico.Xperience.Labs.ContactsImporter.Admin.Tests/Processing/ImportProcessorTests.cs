using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

public class ImportProcessorTests
{
    /// <summary>
    /// Processor over a temporary store and an in-memory contact table.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture(Action<ContactsImporterOptions>? configure = null)
        {
            Temp = new TempStore(options =>
            {
                options.BatchSize = 2;
                configure?.Invoke(options);
            });
            var cleanup = new ImportJobCleanupService(Temp.Store, Temp.Options, Time, NullLogger<ImportJobCleanupService>.Instance);
            Processor = new ImportProcessor(Temp.Store, Schema, Repository, cleanup, Cache, Temp.Options, Time, new ListLogger<ImportProcessor>(Log));
        }

        public TempStore Temp { get; }

        public ImportJobStore Store => Temp.Store;

        public MutableTimeProvider Time { get; } = new(TestData.Now);

        public FakeContactFieldSchemaProvider Schema { get; } = new(Fields);

        public FakeContactRepository Repository { get; } = new();

        public FakeCacheInvalidator Cache { get; } = new();

        public List<string> Log { get; } = [];

        public ImportProcessor Processor { get; }

        /// <summary>
        /// Stores an Uploaded job with <paramref name="rowCount"/> rows (<c>user{n}@x.test</c>, <c>Name {n}</c>) in chunks.
        /// </summary>
        public Guid Upload(int rowCount, int rowsPerChunk = 100, ImportOptions? options = null)
        {
            var job = TestData.Job(null, Email, Email, FirstName) with { Options = options ?? new ImportOptions() };
            Store.CreateJob(job);

            var chunks = new List<ImportChunkInfo>();
            foreach (int[] rows in Enumerable.Range(1, rowCount).Chunk(rowsPerChunk))
            {
                byte[] gzipped = TestData.Gzip(string.Join('\n', rows.Select(r => TestData.Record(r, $"user{r}@x.test", $"Name {r}"))));
                var chunk = new ImportChunkInfo { Index = chunks.Count, Sha256 = TestData.Sha256Hex(gzipped), Rows = rows.Length, FirstRow = rows[0], LastRow = rows[^1] };
                Store.WriteChunk(job.JobId, chunk, gzipped);
                chunks.Add(chunk);
            }

            Store.WriteStatus(job.JobId, new ImportJobStatus
            {
                State = ImportJobState.Uploaded,
                Chunks = chunks,
                Profile = new ImportProfile { RowsRead = rowCount, RowsValid = rowCount, ChunkCount = chunks.Count },
                UpdatedUtc = TestData.Now,
            });

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

        public Guid UploadAnalyzedAndConfirmed(int rowCount, int rowsPerChunk = 100, ImportOptions? options = null)
        {
            var jobId = Upload(rowCount, rowsPerChunk, options);
            Assert.That(Run(jobId, ImportPhase.Analyze).Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));

            return jobId;
        }

        public ImportJobStatus Status(Guid jobId) => Store.ReadStatus(jobId)!;

        public void Dispose() => Temp.Dispose();
    }

    [Test]
    public void Unknown_Job_Is_Reported()
    {
        using var fixture = new Fixture();
        var jobId = Guid.NewGuid();

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false).Outcome, Is.EqualTo(ImportProcessOutcome.JobNotFound));
            Assert.That(fixture.Processor.GetProgress(jobId), Is.Null);
            Assert.That(fixture.Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.JobNotFound));
            Assert.That(fixture.Processor.Cancel(jobId).Outcome, Is.EqualTo(ImportCancelOutcome.JobNotFound));
        });
    }

    [Test]
    public void Analyze_Runs_All_Batches_Within_The_Time_Budget_And_Finishes_Without_Writes()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(5, rowsPerChunk: 3);

        var progress = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        var status = fixture.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(progress.DriverToken, Is.Null);
            Assert.That(progress.RowsProcessed, Is.EqualTo(5));
            Assert.That(progress.RowsTotal, Is.EqualTo(5));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(5));
            Assert.That(fixture.Repository.Lookups, Is.EqualTo(3), "Chunk of 3 rows = batches of 2 + 1, chunk of 2 rows = one batch.");
            Assert.That(fixture.Repository.Writes, Is.Zero);
            Assert.That(fixture.Repository.Contacts, Is.Empty);
            Assert.That(fixture.Cache.Calls, Is.Zero);
            Assert.That(status.State, Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(status.Analysis!.FinishedUtc, Is.EqualTo(TestData.Now));
            Assert.That(status.Analysis.Aggregates[ImportAggregateKeys.Created], Is.EqualTo(5));
            Assert.That(status.DriverToken, Is.Null);
            Assert.That(status.HeartbeatUtc, Is.Null);
            Assert.That(status.Checkpoint, Is.EqualTo(new ImportCheckpoint { Phase = ImportPhase.Analyze, ChunkIndex = 2, RowOffset = 0, LastRow = 5 }));
            Assert.That(fixture.Store.HasChunks(jobId), Is.True);
            Assert.That(fixture.Log, Has.Exactly(1).Contains("CONTACT_IMPORT_START"));
            Assert.That(fixture.Log, Has.Exactly(1).Contains("CONTACT_IMPORT_FINISH"));
        });
    }

    [Test]
    public void Call_Returns_When_The_Time_Budget_Is_Used_And_The_Next_Call_Continues_From_The_Checkpoint()
    {
        using var fixture = new Fixture(options => options.ProcessingTimeBudgetSeconds = 10);
        var jobId = fixture.Upload(7, rowsPerChunk: 4);

        // Every batch "takes" 6 seconds: the budget is used after two batches.
        fixture.Repository.OnLookup = _ => fixture.Time.Advance(TimeSpan.FromSeconds(6));

        var first = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var checkpoint = fixture.Status(jobId).Checkpoint;
        var heartbeat = fixture.Status(jobId).HeartbeatUtc;
        var second = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, first.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(first.State, Is.EqualTo(ImportJobState.Analyzing));
            Assert.That(first.DriverToken, Is.Not.Null);
            Assert.That(first.RowsProcessed, Is.EqualTo(4));
            Assert.That(checkpoint, Is.EqualTo(new ImportCheckpoint { Phase = ImportPhase.Analyze, ChunkIndex = 1, RowOffset = 0, LastRow = 4 }));
            Assert.That(heartbeat, Is.EqualTo(TestData.Now.AddSeconds(12)));
            Assert.That(second.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(second.RowsProcessed, Is.EqualTo(7));
            Assert.That(fixture.Repository.Lookups, Is.EqualTo(4));
        });
    }

    [Test]
    public void Checkpoint_Inside_A_Chunk_Skips_The_Processed_Rows()
    {
        using var fixture = new Fixture(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = fixture.Upload(5);
        fixture.Repository.OnLookup = _ => fixture.Time.Advance(TimeSpan.FromSeconds(2));

        var first = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var checkpoint = fixture.Status(jobId).Checkpoint;
        var last = fixture.Run(jobId, ImportPhase.Analyze, first.DriverToken);

        Assert.Multiple(() =>
        {
            Assert.That(checkpoint, Is.EqualTo(new ImportCheckpoint { Phase = ImportPhase.Analyze, ChunkIndex = 0, RowOffset = 2, LastRow = 2 }));
            Assert.That(last.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(last.Aggregates.Rows, Is.EqualTo(5));
            Assert.That(fixture.Repository.Lookups, Is.EqualTo(3), "No batch is read twice.");
        });
    }

    [Test]
    public void Request_Cancellation_Stops_After_The_Current_Batch_And_Leaves_The_Job_Resumable()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(6);
        using var cancellation = new CancellationTokenSource();
        fixture.Repository.OnLookup = _ => cancellation.Cancel();

        var progress = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false, cancellation.Token);
        var resumed = fixture.Run(jobId, ImportPhase.Analyze, progress.DriverToken);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(progress.RowsProcessed, Is.EqualTo(2));
            Assert.That(resumed.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
        });
    }

    [Test]
    public void Execute_Writes_Completes_Clears_The_Cache_Once_And_Deletes_The_Uploaded_Data()
    {
        using var fixture = new Fixture();
        var jobId = fixture.UploadAnalyzedAndConfirmed(5);

        var progress = fixture.Run(jobId, ImportPhase.Execute);

        var status = fixture.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(5));
            Assert.That(progress.Analysis!.Created, Is.EqualTo(5));
            Assert.That(progress.AnalyzedUtc, Is.EqualTo(TestData.Now));
            Assert.That(fixture.Repository.Contacts, Has.Count.EqualTo(5));
            Assert.That(fixture.Repository.Writes, Is.EqualTo(3));
            Assert.That(fixture.Cache.Calls, Is.EqualTo(1));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(status.FinishedUtc, Is.Not.Null);
            Assert.That(status.Analysis, Is.Not.Null);
            Assert.That(fixture.Store.HasChunks(jobId), Is.False);
            Assert.That(fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false).Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), "Repeating the last call is harmless.");
            Assert.That(fixture.Cache.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public void Phases_Start_Only_From_Their_States()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(2);

        var executeBeforeAnalysis = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        fixture.Run(jobId, ImportPhase.Analyze);
        var analyzeAgain = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var executeBeforeConfirm = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        fixture.Processor.Confirm(jobId);
        var analyzeAfterConfirm = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(executeBeforeAnalysis.Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
            Assert.That(analyzeAgain.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(executeBeforeConfirm.Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
            Assert.That(analyzeAfterConfirm.Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
            Assert.That(fixture.Repository.Writes, Is.Zero);
        });
    }

    [Test]
    public void Only_One_Driver_Processes_A_Job_Until_Its_Heartbeat_Is_Stale()
    {
        using var fixture = new Fixture(options => options.ProcessingTimeBudgetSeconds = 1);
        var jobId = fixture.Upload(6);
        fixture.Repository.OnLookup = _ => fixture.Time.Advance(TimeSpan.FromSeconds(2));

        var first = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var otherTab = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var wrongToken = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, "not-the-token", true);
        fixture.Time.Advance(TimeSpan.FromSeconds(61));
        var paused = fixture.Processor.GetProgress(jobId)!;
        var stale = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var takenOver = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, true);
        var oldDriver = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, first.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(otherTab.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere));
            Assert.That(otherTab.DriverToken, Is.Null);
            Assert.That(otherTab.RowsProcessed, Is.EqualTo(2), "A refused tab still sees the progress.");
            Assert.That(wrongToken.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere));
            Assert.That(paused.State, Is.EqualTo(ImportJobState.Paused));
            Assert.That(stale.Outcome, Is.EqualTo(ImportProcessOutcome.StaleRequiresTakeOver));
            Assert.That(takenOver.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(takenOver.DriverToken, Is.Not.Null.And.Not.EqualTo(first.DriverToken));
            Assert.That(takenOver.RowsProcessed, Is.EqualTo(4));
            Assert.That(oldDriver.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere));
        });
    }

    [Test]
    public void Cancel_During_A_Call_Interrupts_It_At_The_Batch_Boundary()
    {
        using var fixture = new Fixture();
        var jobId = fixture.UploadAnalyzedAndConfirmed(6);
        ImportCancelResult? cancel = null;
        fixture.Repository.OnWrite = write =>
        {
            if (write == 2)
            {
                cancel = fixture.Processor.Cancel(jobId);
            }
        };

        var progress = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);

        var status = fixture.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(cancel!.Outcome, Is.EqualTo(ImportCancelOutcome.Cancelled));
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(fixture.Repository.Writes, Is.EqualTo(2), "The batch in progress finishes, no further batch starts.");
            Assert.That(fixture.Repository.Contacts, Has.Count.EqualTo(4));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(status.Aggregates[ImportAggregateKeys.Rows], Is.EqualTo(2), "The counts of the checkpointed batches stay.");
            Assert.That(fixture.Store.HasChunks(jobId), Is.False);
            Assert.That(fixture.Cache.Calls, Is.Zero, "No cache clear unless the import completes.");
            Assert.That(fixture.Log, Has.Some.Contains("CONTACT_IMPORT_CANCEL"));
        });
    }

    [Test]
    public void Cancel_Works_In_Every_Unfinished_State_And_Only_Once()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(2);

        var cancelled = fixture.Processor.Cancel(jobId);
        var again = fixture.Processor.Cancel(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Outcome, Is.EqualTo(ImportCancelOutcome.Cancelled));
            Assert.That(cancelled.Status!.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(again.Outcome, Is.EqualTo(ImportCancelOutcome.AlreadyFinished));
            Assert.That(fixture.Store.HasChunks(jobId), Is.False);
            Assert.That(fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false).Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
        });
    }

    [Test]
    public void Unexpected_Error_Fails_The_Job_With_A_Message_And_Log_Free_Of_Contact_Data()
    {
        using var fixture = new Fixture();
        var jobId = fixture.UploadAnalyzedAndConfirmed(6);
        fixture.Repository.OnWrite = write =>
        {
            if (write == 2)
            {
                throw new InvalidOperationException("String or binary data would be truncated. Truncated value: 'user3@x.test'.");
            }
        };

        var progress = fixture.Run(jobId, ImportPhase.Execute);

        var status = fixture.Status(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Failed));
            Assert.That(progress.Error, Is.EqualTo(ImportProcessor.UnexpectedError));
            Assert.That(status.ErrorMessage, Is.EqualTo(ImportProcessor.UnexpectedError));
            Assert.That(status.Checkpoint.LastRow, Is.EqualTo(2));
            Assert.That(fixture.Store.HasChunks(jobId), Is.False);
            Assert.That(fixture.Cache.Calls, Is.Zero);
            Assert.That(fixture.Log, Has.Exactly(1).Contains("CONTACT_IMPORT_FAILURE").And.Contains("InvalidOperationException"));
            Assert.That(string.Join('\n', fixture.Log) + Encoding.UTF8.GetString(ImportJson.Serialize(status)), Does.Not.Contain("user3").And.Not.Contain("Truncated value"));
        });
    }

    [Test]
    public void Temporary_Error_Keeps_The_Job_Running_And_The_Next_Call_Continues()
    {
        using var fixture = new Fixture();
        var jobId = fixture.UploadAnalyzedAndConfirmed(6);
        fixture.Repository.OnWrite = write =>
        {
            if (write == 2)
            {
                throw new TimeoutException("Timeout for user3@x.test");
            }
        };

        var interrupted = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        var finished = fixture.Run(jobId, ImportPhase.Execute, interrupted.DriverToken);

        Assert.Multiple(() =>
        {
            Assert.That(interrupted.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(interrupted.State, Is.EqualTo(ImportJobState.Running));
            Assert.That(interrupted.Error, Is.EqualTo(ImportProcessor.TransientError));
            Assert.That(interrupted.RowsProcessed, Is.EqualTo(2));
            Assert.That(interrupted.DriverToken, Is.Not.Null);
            Assert.That(finished.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(fixture.Repository.Contacts, Has.Count.EqualTo(6));
            Assert.That(string.Join('\n', fixture.Log), Does.Contain("CONTACT_IMPORT_RETRY").And.Contain("TimeoutException").And.Not.Contain("user3"));
        });
    }

    [Test]
    public void Failed_Cache_Clear_Is_Tried_Again_By_The_Next_Call_Before_The_Job_Completes()
    {
        using var fixture = new Fixture();
        var jobId = fixture.UploadAnalyzedAndConfirmed(2);
        fixture.Cache.Throw = new InvalidOperationException("cache");

        var failed = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        var stateAfterFailure = fixture.Status(jobId).State;
        fixture.Cache.Throw = null;
        var finished = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, failed.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(failed.Error, Is.EqualTo(ImportProcessor.CacheError));
            Assert.That(stateAfterFailure, Is.EqualTo(ImportJobState.Running));
            Assert.That(finished.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(fixture.Cache.Calls, Is.EqualTo(2));
            Assert.That(fixture.Repository.Writes, Is.EqualTo(1), "No row is processed again.");
        });
    }

    [Test]
    public void Missing_Or_Unreadable_Uploaded_Data_Fails_The_Job()
    {
        using var fixture = new Fixture();
        var missing = fixture.Upload(4, rowsPerChunk: 2);
        fixture.Store.DeleteChunk(missing, 1);
        var corrupt = fixture.Upload(2);
        File.WriteAllBytes(fixture.Temp.JobPath(corrupt, "chunks", "0.ndjson.gz"), [1, 2, 3, 4]);
        var shorter = fixture.Upload(2);
        fixture.Store.WriteChunk(shorter, fixture.Status(shorter).Chunks[0], TestData.Gzip(TestData.Record(1, "user1@x.test")));
        fixture.Store.UpdateStatus(shorter, status => status with { Chunks = [status.Chunks[0] with { Rows = 3 }] });

        var missingProgress = fixture.Run(missing, ImportPhase.Analyze);
        var corruptProgress = fixture.Run(corrupt, ImportPhase.Analyze);
        var shorterProgress = fixture.Run(shorter, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(missingProgress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(missingProgress.Error?.ToString(), Does.Contain("chunk 1 is missing"));
            Assert.That(missingProgress.RowsProcessed, Is.EqualTo(2));
            Assert.That(corruptProgress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(corruptProgress.Error?.ToString(), Does.Contain("chunk 0 cannot be read"));
            Assert.That(shorterProgress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(shorterProgress.Error?.ToString(), Does.Contain("chunk 0 is incomplete"));
        });
    }

    [Test]
    public void Job_That_No_Longer_Matches_The_Contact_Class_Is_Refused_With_The_Reason_And_Keeps_Its_Upload()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(2);
        fixture.Schema.Fields = [.. Fields.Where(f => f.Name != FirstName)];

        var progress = fixture.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.Error?.ToString(), Does.Contain("no longer exists"));
            Assert.That(fixture.Status(jobId).State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(fixture.Store.HasChunks(jobId), Is.True);
        });
    }

    [Test]
    public void Second_Analysis_Replaces_The_Report_And_The_Analysis_Summary()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(3);
        fixture.Run(jobId, ImportPhase.Analyze);
        var reader = new ImportReportReader(fixture.Store);
        using var first = new MemoryStream();
        long firstRows = reader.CopyTo(jobId, ImportPhase.Analyze, first);

        // An options change returns the job to Uploaded; in the meantime a contact appeared.
        fixture.Store.UpdateStatus(jobId, status => ImportJobStateMachine.Transition(status, ImportJobState.Uploaded, TestData.Now));
        var summaryAfterReset = fixture.Status(jobId).Analysis;
        var context = Context(Email, [Email, FirstName]);
        fixture.Repository.Add(context, TestData.Now, "user1@x.test", "Name 1");
        var second = fixture.Run(jobId, ImportPhase.Analyze);
        using var again = new MemoryStream();

        Assert.Multiple(() =>
        {
            Assert.That(firstRows, Is.EqualTo(3));
            Assert.That(summaryAfterReset, Is.Null);
            Assert.That(second.Aggregates.Created, Is.EqualTo(2));
            Assert.That(second.Aggregates.Unchanged, Is.EqualTo(1));
            Assert.That(reader.CopyTo(jobId, ImportPhase.Analyze, again), Is.EqualTo(3));
            Assert.That(fixture.Status(jobId).Analysis!.Aggregates[ImportAggregateKeys.Created], Is.EqualTo(2));
        });
    }

    [Test]
    public void Running_The_Same_Data_Twice_Leaves_The_Contacts_As_They_Are()
    {
        using var fixture = new Fixture();
        fixture.Run(fixture.UploadAnalyzedAndConfirmed(5), ImportPhase.Execute);
        var snapshot = fixture.Repository.Contacts.Select(c => (c.Id, string.Join('|', c.Values), c.LastModified)).ToList();
        fixture.Time.Advance(TimeSpan.FromHours(1));

        var again = fixture.Run(fixture.UploadAnalyzedAndConfirmed(5), ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(again.Aggregates.Unchanged, Is.EqualTo(5));
            Assert.That(fixture.Repository.Contacts.Select(c => (c.Id, string.Join('|', c.Values), c.LastModified)), Is.EqualTo(snapshot));
        });
    }

    [Test]
    public void Similar_Keys_Ask_The_Database_Which_Keys_Are_Equal()
    {
        using var fixture = new Fixture(options => options.BatchSize = 10);
        fixture.Repository.IgnoreCase = true;
        var job = TestData.Job(null, CustomerId, CustomerId, FirstName);
        fixture.Store.CreateJob(job);
        byte[] gzipped = TestData.Gzip(string.Join('\n', new[] { "abc", "ABC", "other" }.Select((key, i) =>
            $"{{\"r\":{i + 1},\"k\":\"{key}\",\"v\":{{\"CustomerID\":\"{key}\",\"ContactFirstName\":\"N{i}\"}}}}")));
        var chunk = new ImportChunkInfo { Index = 0, Sha256 = TestData.Sha256Hex(gzipped), Rows = 3, FirstRow = 1, LastRow = 3 };
        fixture.Store.WriteChunk(job.JobId, chunk, gzipped);
        fixture.Store.WriteStatus(job.JobId, new ImportJobStatus { State = ImportJobState.Uploaded, Chunks = [chunk], Profile = new ImportProfile { RowsRead = 3, RowsValid = 3, ChunkCount = 1 }, UpdatedUtc = TestData.Now });

        var progress = fixture.Run(job.JobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Repository.EquivalenceLookups, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(2));
            Assert.That(progress.Aggregates.DuplicateRows, Is.EqualTo(1));
        });
    }

    [Test]
    public void Confirm_Stores_The_Attestation_And_Is_Repeatable()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(2);
        fixture.Run(jobId, ImportPhase.Analyze);
        var attestation = new ImportAttestation { UserId = 53, Utc = TestData.Now, StatementVersion = "2026-11" };

        var first = fixture.Processor.Confirm(jobId, attestation);
        var second = fixture.Processor.Confirm(jobId, attestation);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
            Assert.That(first.Status!.State, Is.EqualTo(ImportJobState.Confirmed));
            Assert.That(second.Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
            Assert.That(fixture.Store.ReadJob(jobId)!.Attestation, Is.EqualTo(attestation));
            Assert.That(fixture.Status(jobId).Analysis, Is.Not.Null);
        });
    }

    [Test]
    public void Old_Analysis_Is_Refused_At_Confirm_And_At_The_Start_Of_The_Import()
    {
        using var fixture = new Fixture(options => options.AnalysisValidHours = 2);
        var jobId = fixture.Upload(2);
        fixture.Run(jobId, ImportPhase.Analyze);
        fixture.Time.Advance(TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(1)));

        var expired = fixture.Processor.Confirm(jobId);
        fixture.Run(jobId, ImportPhase.Analyze);
        var confirmed = fixture.Processor.Confirm(jobId);
        fixture.Time.Advance(TimeSpan.FromHours(3));
        var expiredAtStart = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(expired.Outcome, Is.EqualTo(ImportConfirmOutcome.AnalysisExpired));
            Assert.That(expired.Status!.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(confirmed.Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
            Assert.That(expiredAtStart.Outcome, Is.EqualTo(ImportProcessOutcome.AnalysisExpired));
            Assert.That(expiredAtStart.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(fixture.Repository.Writes, Is.Zero);
        });
    }

    [Test]
    public void Transient_Errors_Are_Recognized_Through_Wrapping_Exceptions() => Assert.Multiple(() =>
                                                                                      {
                                                                                          Assert.That(ImportProcessor.IsTransient(new TimeoutException()), Is.True);
                                                                                          Assert.That(ImportProcessor.IsTransient(new IOException()), Is.True);
                                                                                          Assert.That(ImportProcessor.IsTransient(new InvalidOperationException("wrapper", new TimeoutException())), Is.True);
                                                                                          Assert.That(ImportProcessor.IsTransient(new InvalidOperationException()), Is.False);
                                                                                          Assert.That(ImportProcessor.IsTransient(new InvalidDataException()), Is.False);
                                                                                      });

    [Test]
    public void Options_For_Processing_Have_Defaults_And_Are_Validated()
    {
        var options = new ContactsImporterOptions();
        var invalid = new ContactsImporterOptions { ProcessingTimeBudgetSeconds = 0, AnalysisValidHours = -1 };

        Assert.Multiple(() =>
        {
            Assert.That(options.ProcessingTimeBudgetSeconds, Is.EqualTo(15));
            Assert.That(options.AnalysisValidHours, Is.EqualTo(24));
            Assert.That(options.ProcessingTimeBudget, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(options.AnalysisValid, Is.EqualTo(TimeSpan.FromHours(24)));
            Assert.That(new ContactsImporterOptionsValidator().Validate(null, options).Succeeded, Is.True);
            Assert.That(
                new ContactsImporterOptionsValidator().Validate(null, invalid).FailureMessage,
                Does.Contain(nameof(ContactsImporterOptions.ProcessingTimeBudgetSeconds)).And.Contain(nameof(ContactsImporterOptions.AnalysisValidHours)));
        });
    }

    [Test]
    public void Cache_Keys_Follow_The_Job_Options()
    {
        var job = TestData.Job();

        Assert.Multiple(() =>
        {
            Assert.That(ImportCacheInvalidator.GetCacheKeys(job), Is.EqualTo(new[] { "om.contact|all" }));
            Assert.That(
                ImportCacheInvalidator.GetCacheKeys(job with { Options = new ImportOptions { RecipientListId = 12, Consent = ConsentMode.Blanket, ConsentId = 3 } }),
                Is.EqualTo(new[] { "om.contact|all", "om.contactgroupmembercontact|all", "emaillibrary.emailsubscriptionconfirmation|all", "om.recipientlist|byid|12", "cms.consentagreement|all" }));
        });
    }
}
