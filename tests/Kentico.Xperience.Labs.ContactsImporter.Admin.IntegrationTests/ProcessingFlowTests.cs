using CMS.Helpers;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

public class ProcessingFlowTests : ImportIntegrationTest
{
    private static string?[][] Rows(int count, string domain = "flow.test", string firstName = "Name") =>
        [.. Enumerable.Range(1, count).Select(i => new string?[] { $"user{i}@{domain}", $"{firstName} {i}" })];

    [Test]
    public void Dry_Run_Writes_Nothing_And_Predicts_The_Import()
    {
        int existing = Seed("user2@flow.test", "Old");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(5));

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        var status = harness.Store.ReadStatus(jobId)!;

        Assert.Multiple(() =>
        {
            Assert.That(analysis.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(analysis.State, Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(analysis.Aggregates.Created, Is.EqualTo(4));
            Assert.That(analysis.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(ContactCount(), Is.EqualTo(1), "The analysis creates nothing.");
            Assert.That(ContactById(existing)[FirstName], Is.EqualTo("Old"));
            Assert.That(ContactById(existing)["ContactLastModified"], Is.EqualTo(Old));
            Assert.That(status.Analysis, Is.Not.Null);
            Assert.That(status.DriverToken, Is.Null);
            Assert.That(harness.Store.HasChunks(jobId), Is.True, "The uploaded data stays for the import.");
            Assert.That(harness.ReadReport(jobId, ImportPhase.Analyze), Has.Length.EqualTo(6));
        });

        Assert.That(harness.Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
        var import = harness.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(import.Aggregates, Is.EqualTo(analysis.Aggregates).Using<ImportAggregates>(CreateAndUpdateTests.SameCounts));
            Assert.That(import.Analysis, Is.EqualTo(analysis.Aggregates).Using<ImportAggregates>(CreateAndUpdateTests.SameCounts));
            Assert.That(ContactCount(), Is.EqualTo(5));
            Assert.That(harness.Store.HasChunks(jobId), Is.False, "Uploaded data is deleted when the import completes.");
            Assert.That(harness.Store.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(harness.ReadReport(jobId, ImportPhase.Execute), Has.Length.EqualTo(6));
        });
    }

    [Test]
    public void Importing_The_Same_File_Again_Changes_Nothing()
    {
        using var harness = new ImportHarness(options => options.BatchSize = 3);
        harness.Import(harness.Upload(Email, [Email, FirstName], Rows(10)));
        var snapshot = Contacts();

        var again = harness.Import(harness.Upload(Email, [Email, FirstName], Rows(10)));

        Assert.Multiple(() =>
        {
            Assert.That(again.Aggregates.Unchanged, Is.EqualTo(10));
            Assert.That(again.Aggregates.Created + again.Aggregates.Updated, Is.Zero);
            Assert.That(Contacts(), Is.EqualTo(snapshot), "Same rows, same values, same modification times.");
        });
    }

    [Test]
    public void Running_A_Batch_Again_After_A_Lost_Checkpoint_Gives_The_Same_Contacts()
    {
        using var harness = new ImportHarness(OneBatchPerCall(4));
        Seed("user6@flow.test", "Old");
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(10));
        harness.Run(jobId, ImportPhase.Analyze);
        harness.Processor.Confirm(jobId);

        // First call: one batch (rows 1-4) is written and checkpointed.
        var first = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        var afterFirst = harness.Store.ReadStatus(jobId)!;

        // Second call: rows 5-8 are written. Then the application "stops" before the checkpoint is stored:
        // the status of the first call is put back.
        var second = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, first.DriverToken, false);
        Assert.That(ContactCount(), Is.EqualTo(8));
        harness.Store.WriteStatus(jobId, afterFirst);

        var final = RunWithToken(harness, jobId, first.DriverToken);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(first.RowsProcessed, Is.EqualTo(4));
            Assert.That(second.RowsProcessed, Is.EqualTo(8));
            Assert.That(final.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(final.Aggregates.Rows, Is.EqualTo(10));
            Assert.That(ContactCount(), Is.EqualTo(10), "No contact is created twice.");
            Assert.That(Contacts().Select(c => c[FirstName]), Is.EquivalentTo(Enumerable.Range(1, 10).Select(i => $"Name {i}")));

            // Rows 5-8 ran twice: the second time they match what the first time wrote, so they count as unchanged
            // (the counts of the lost call are lost with its checkpoint).
            Assert.That(final.Aggregates.Created, Is.EqualTo(6));
            Assert.That(final.Aggregates.Updated, Is.Zero);
            Assert.That(final.Aggregates.Unchanged, Is.EqualTo(4));
            Assert.That(ContactByEmail("user6@flow.test")[FirstName], Is.EqualTo("Name 6"));

            // The report has every row once, the re-run rows with their second outcome.
            string[] report = harness.ReadReport(jobId, ImportPhase.Execute);
            Assert.That(report, Has.Length.EqualTo(11));
            Assert.That(report[5], Is.EqualTo("5,unchanged,,"));
            Assert.That(report[9], Is.EqualTo("9,created,,"));
        });
    }

    [Test]
    public void Processing_Resumes_From_The_Checkpoint_In_A_New_Application_Instance()
    {
        var time = new MutableTimeProvider(DateTime.UtcNow);
        using var harness = new ImportHarness(OneBatchPerCall(3), time);
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(10), rowsPerChunk: 4);

        var first = harness.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var checkpoint = harness.Store.ReadStatus(jobId)!.Checkpoint;

        // Another instance (same storage) with another browser tab: refused while the heartbeat is fresh.
        using var other = new ImportHarness(
            options =>
            {
                options.BatchSize = 3;
                options.StoragePath = harness.Temp.Root;
            },
            time);
        var refused = other.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);

        time.Advance(TimeSpan.FromMinutes(5));
        var paused = other.Processor.GetProgress(jobId)!;
        var needsTakeOver = other.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, false);
        var resumed = other.Processor.ProcessNext(jobId, ImportPhase.Analyze, null, takeOver: true);
        var oldDriver = harness.Processor.ProcessNext(jobId, ImportPhase.Analyze, first.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(first.RowsProcessed, Is.EqualTo(3));
            Assert.That(checkpoint, Is.EqualTo(new ImportCheckpoint { Phase = ImportPhase.Analyze, ChunkIndex = 0, RowOffset = 3, LastRow = 3 }));
            Assert.That(refused.Outcome, Is.EqualTo(ImportProcessOutcome.ActiveElsewhere));
            Assert.That(paused.State, Is.EqualTo(ImportJobState.Paused));
            Assert.That(needsTakeOver.Outcome, Is.EqualTo(ImportProcessOutcome.StaleRequiresTakeOver));
            Assert.That(resumed.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(resumed.Aggregates.Rows, Is.EqualTo(10), "Rows 1-3 are not processed again.");
            Assert.That(resumed.Aggregates.Created, Is.EqualTo(10));
            Assert.That(oldDriver.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), "The old tab sees the finished analysis.");
            Assert.That(harness.ReadReport(jobId, ImportPhase.Analyze).Skip(1).Select(l => l.Split(',')[0]), Is.EqualTo(Enumerable.Range(1, 10).Select(i => $"{i}")));
        });
    }

    [Test]
    public void Cancel_Stops_At_The_Batch_Boundary_And_Keeps_What_Was_Written()
    {
        using var harness = new ImportHarness(OneBatchPerCall(2));
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(6));
        harness.Run(jobId, ImportPhase.Analyze);
        harness.Processor.Confirm(jobId);

        var first = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        var cancel = harness.Processor.Cancel(jobId);
        var afterCancel = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, first.DriverToken, false);
        var cancelAgain = harness.Processor.Cancel(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(first.RowsProcessed, Is.EqualTo(2));
            Assert.That(cancel.Outcome, Is.EqualTo(ImportCancelOutcome.Cancelled));
            Assert.That(cancel.Status!.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(afterCancel.Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
            Assert.That(afterCancel.Aggregates.Created, Is.EqualTo(2), "Partial counts stay available.");
            Assert.That(cancelAgain.Outcome, Is.EqualTo(ImportCancelOutcome.AlreadyFinished));
            Assert.That(ContactCount(), Is.EqualTo(2));
            Assert.That(harness.Store.HasChunks(jobId), Is.False);
            Assert.That(harness.Log, Has.Some.Contains("CONTACT_IMPORT_CANCEL"));
        });
    }

    [Test]
    public void Contact_Cache_Keys_Are_Touched_When_The_Import_Completes_Not_Before()
    {
        // Updates only: the raw UPDATE touches no cache key itself (BulkInsert of new contacts would).
        Seed("user1@flow.test", "Old");
        Seed("user2@flow.test", "Old");
        using var harness = new ImportHarness(OneBatchPerCall(1));
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(2));
        string cached = CacheContactDependentItem();

        harness.Run(jobId, ImportPhase.Analyze);
        bool cachedAfterAnalysis = IsCached(cached);
        harness.Processor.Confirm(jobId);
        var first = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        bool cachedWhileRunning = IsCached(cached);
        var last = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, first.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(first.Aggregates.Updated, Is.EqualTo(1));
            Assert.That(last.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(cachedAfterAnalysis, Is.True);
            Assert.That(cachedWhileRunning, Is.True, "Caches are cleared only when an import completes (D36).");
            Assert.That(IsCached(cached), Is.False, "An item depending on om.contact|all must be dropped.");
        });
    }

    [Test]
    public void Database_Error_Fails_The_Job_Without_Contact_Data_In_Status_Or_Log()
    {
        using var harness = new ImportHarness(options => options.BatchSize = 2);

        // A unique index on a custom field (as a project can add): two new contacts with the same value break it in
        // the second batch, and the database error message quotes the value.
        const string uniqueIndex = "IX_ImporterTest_UniqueCode";
        var jobId = harness.Upload(Email, [Email, FirstName, Code],
        [
            ["ok1@fail.test", "Ok", "code-1"],
            ["ok2@fail.test", "Ok", "code-2"],
            ["secret.person@fail.test", "Confidential", "private-987654"],
            ["never@fail.test", "Never", "private-987654"],
        ]);
        harness.Run(jobId, ImportPhase.Analyze);
        harness.Processor.Confirm(jobId);

        ImportProgress progress;
        IntegrationDatabase.Execute($"CREATE UNIQUE INDEX [{uniqueIndex}] ON [OM_Contact] ([{Code}]) WHERE [{Code}] IS NOT NULL");
        try
        {
            progress = harness.Run(jobId, ImportPhase.Execute);
        }
        finally
        {
            IntegrationDatabase.Execute($"DROP INDEX [{uniqueIndex}] ON [OM_Contact]");
        }

        var status = harness.Store.ReadStatus(jobId)!;
        string everything = string.Join("\n", harness.Log) + System.Text.Encoding.UTF8.GetString(ImportJson.Serialize(status));

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Failed));
            Assert.That(progress.Error, Is.EqualTo(ImportProcessor.UnexpectedError));
            Assert.That(status.ErrorMessage, Is.EqualTo(ImportProcessor.UnexpectedError));
            Assert.That(ContactCount(), Is.EqualTo(2), "The failed batch is rolled back as a whole; earlier batches stay.");
            Assert.That(harness.Store.HasChunks(jobId), Is.False);
            Assert.That(harness.Log, Has.Some.Contains("CONTACT_IMPORT_FAILURE").And.Contains("SqlException (SQL error 2601"));
            Assert.That(everything, Does.Not.Contain("secret.person").And.Not.Contain("Confidential").And.Not.Contain("987654"));
            Assert.That(harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false).Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
        });
    }

    [Test]
    public void Confirm_Refuses_An_Old_Analysis_And_The_Job_Must_Be_Analyzed_Again()
    {
        var time = new MutableTimeProvider(DateTime.UtcNow);
        using var harness = new ImportHarness(options => options.AnalysisValidHours = 2, time);
        var jobId = harness.Upload(Email, [Email, FirstName], Rows(2));
        var notAnalyzed = harness.Processor.Confirm(jobId);
        harness.Run(jobId, ImportPhase.Analyze);

        time.Advance(TimeSpan.FromHours(3));
        var expired = harness.Processor.Confirm(jobId);
        var executeWithoutAnalysis = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        harness.Run(jobId, ImportPhase.Analyze);
        var confirmed = harness.Processor.Confirm(jobId, new ImportAttestation { UserId = 53, Utc = time.UtcNow, StatementVersion = "v1" });

        time.Advance(TimeSpan.FromHours(3));
        var expiredAtStart = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);

        Assert.Multiple(() =>
        {
            Assert.That(notAnalyzed.Outcome, Is.EqualTo(ImportConfirmOutcome.InvalidState));
            Assert.That(expired.Outcome, Is.EqualTo(ImportConfirmOutcome.AnalysisExpired));
            Assert.That(expired.Status!.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(executeWithoutAnalysis.Outcome, Is.EqualTo(ImportProcessOutcome.InvalidState));
            Assert.That(confirmed.Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
            Assert.That(harness.Store.ReadJob(jobId)!.Attestation!.StatementVersion, Is.EqualTo("v1"));
            Assert.That(expiredAtStart.Outcome, Is.EqualTo(ImportProcessOutcome.AnalysisExpired));
            Assert.That(expiredAtStart.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(ContactCount(), Is.Zero);
        });
    }

    [Test]
    public void Report_Has_The_Key_Column_Only_When_Asked_And_Is_Safe_To_Open_In_A_Spreadsheet()
    {
        using var harness = new ImportHarness();
        string?[][] rows = [["=1+1", "Formula"], ["A,\"B\"", "Quoted"], ["plain", "Plain"]];

        var withoutKey = harness.Upload(Code, [Code, FirstName], rows);
        harness.Run(withoutKey, ImportPhase.Analyze);
        var withKey = harness.Upload(Code, [Code, FirstName], rows, new ImportOptions { ReportIncludesKey = true });
        harness.Run(withKey, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(harness.ReadReport(withoutKey, ImportPhase.Analyze), Is.EqualTo(new[]
            {
                "row,outcome,reason,field",
                "1,created,,",
                "2,created,,",
                "3,created,,",
            }));
            Assert.That(harness.ReadReport(withKey, ImportPhase.Analyze), Is.EqualTo(new[]
            {
                "row,outcome,reason,field,key",
                "1,created,,,\"'=1+1\"",
                "2,created,,,\"A,\"\"B\"\"\"",
                "3,created,,,plain",
            }));
            Assert.That(harness.Reports.HasReport(withKey, ImportPhase.Execute), Is.False);
        });
    }

    /// <summary>
    /// No time budget: every processing call returns after one batch.
    /// </summary>
    private static Action<ContactsImporterOptions> OneBatchPerCall(int batchSize) => options =>
    {
        options.BatchSize = batchSize;
        options.ProcessingTimeBudgetSeconds = 0;
    };

    private static ImportProgress RunWithToken(ImportHarness harness, Guid jobId, string? token)
    {
        ImportProgress progress;
        do
        {
            progress = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, token, false);
            token = progress.DriverToken;
        }
        while (progress.Outcome == ImportProcessOutcome.Processed);

        return progress;
    }

    private static string CacheContactDependentItem()
    {
        string key = "contactsimporter|integrationtest|" + Guid.NewGuid().ToString("N");
        CacheHelper.Add(key, "cached", CacheHelper.GetCacheDependency("om.contact|all"), DateTimeOffset.Now.AddHours(1), TimeSpan.Zero);
        Assert.That(CacheHelper.TryGetItem(key, out string? _), Is.True, "The item must be cached before the import.");

        return key;
    }

    private static bool IsCached(string key) => CacheHelper.TryGetItem(key, out string? _);
}
