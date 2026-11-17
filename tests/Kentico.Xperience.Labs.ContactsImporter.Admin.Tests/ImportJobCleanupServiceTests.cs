using Microsoft.Extensions.Logging.Abstractions;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// Retention rules of PLAN §11 "File cleanup" with the default options:
/// reports 7 days, reports with keys 24 hours, abandoned jobs 7 days, status 90 days.
/// </summary>
internal class ImportJobCleanupServiceTests
{
    private TempStore temp = null!;
    private MutableTimeProvider time = null!;
    private ImportJobCleanupService service = null!;

    [SetUp]
    public void SetUp()
    {
        temp = new TempStore();
        time = new MutableTimeProvider(TestData.Now);
        service = new ImportJobCleanupService(temp.Store, temp.Options, time, NullLogger<ImportJobCleanupService>.Instance);
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    /// <summary>
    /// Creates a job with one chunk and one report, in the given state as of the current test time.
    /// </summary>
    private Guid CreateJob(ImportJobState state, bool reportIncludesKey = false)
    {
        var job = TestData.Job() with { CreatedUtc = time.UtcNow };
        job = job with { Options = job.Options with { ReportIncludesKey = reportIncludesKey } };
        temp.Store.CreateJob(job);
        temp.Store.WriteChunk(job.JobId, new ImportChunkInfo { Index = 0, Sha256 = new string('a', 64), Rows = 1, FirstRow = 1, LastRow = 1 }, [1, 2, 3]);
        File.WriteAllText(temp.JobPath(job.JobId, "report-execute.csv.gz"), "report");
        temp.Store.WriteStatus(job.JobId, new ImportJobStatus
        {
            State = state,
            UpdatedUtc = time.UtcNow,
            FinishedUtc = ImportJobStateMachine.IsTerminal(state) ? time.UtcNow : null,
        });

        return job.JobId;
    }

    private bool HasChunks(Guid jobId) => temp.Store.HasChunks(jobId);

    private bool HasReports(Guid jobId) => temp.Store.ListReports(jobId).Count > 0;

    [TestCase(ImportJobState.Completed)]
    [TestCase(ImportJobState.Failed)]
    [TestCase(ImportJobState.Cancelled)]
    public void Sweep_Deletes_Chunks_Of_Finished_Jobs_At_Once_And_Keeps_Fresh_Reports(ImportJobState state)
    {
        var jobId = CreateJob(state);

        var result = service.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(HasChunks(jobId), Is.False);
            Assert.That(HasReports(jobId), Is.True);
            Assert.That(temp.Store.ReadStatus(jobId)!.State, Is.EqualTo(state));
            Assert.That(result, Is.EqualTo(new ImportSweepResult(JobsChecked: 1, ChunksDeleted: 1, ReportsDeleted: 0, JobsAbandoned: 0, JobsDeleted: 0, Errors: 0)));
        });
    }

    [Test]
    public void Sweep_Deletes_Report_Without_Keys_After_Seven_Days()
    {
        var jobId = CreateJob(ImportJobState.Completed);

        time.Advance(TimeSpan.FromDays(7));
        service.Sweep();
        bool keptAtSevenDays = HasReports(jobId);
        time.Advance(TimeSpan.FromMinutes(1));
        var result = service.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(keptAtSevenDays, Is.True);
            Assert.That(HasReports(jobId), Is.False);
            Assert.That(result.ReportsDeleted, Is.EqualTo(1));
            Assert.That(temp.Store.ReadStatus(jobId), Is.Not.Null, "status stays for the recent imports list");
            Assert.That(temp.Store.ReadJob(jobId), Is.Not.Null);
        });
    }

    [Test]
    public void Sweep_Deletes_Report_With_Keys_After_Twenty_Four_Hours()
    {
        var jobId = CreateJob(ImportJobState.Completed, reportIncludesKey: true);

        time.Advance(TimeSpan.FromHours(24));
        service.Sweep();
        bool keptAtLimit = HasReports(jobId);
        time.Advance(TimeSpan.FromMinutes(1));
        service.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(keptAtLimit, Is.True);
            Assert.That(HasReports(jobId), Is.False);
        });
    }

    [Test]
    public void Sweep_Uses_Short_Report_Retention_When_Job_Configuration_Is_Unreadable()
    {
        var jobId = CreateJob(ImportJobState.Completed);
        File.WriteAllText(temp.JobPath(jobId, "job.json"), "broken");

        time.Advance(TimeSpan.FromHours(25));
        service.Sweep();

        Assert.That(HasReports(jobId), Is.False);
    }

    [Test]
    public void Sweep_Deletes_Finished_Job_After_Status_Retention()
    {
        var jobId = CreateJob(ImportJobState.Completed);

        time.Advance(TimeSpan.FromDays(90));
        service.Sweep();
        bool keptAtLimit = temp.Store.JobExists(jobId);
        time.Advance(TimeSpan.FromMinutes(1));
        var result = service.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(keptAtLimit, Is.True);
            Assert.That(temp.Store.JobExists(jobId), Is.False);
            Assert.That(result.JobsDeleted, Is.EqualTo(1));
        });
    }

    [TestCase(ImportJobState.Configuring)]
    [TestCase(ImportJobState.Uploading)]
    [TestCase(ImportJobState.Uploaded)]
    [TestCase(ImportJobState.Analyzed)]
    [TestCase(ImportJobState.Running)]
    public void Sweep_Leaves_Active_Unfinished_Jobs_Alone(ImportJobState state)
    {
        var jobId = CreateJob(state);

        time.Advance(TimeSpan.FromDays(7));
        var result = service.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(HasChunks(jobId), Is.True);
            Assert.That(HasReports(jobId), Is.True);
            Assert.That(temp.Store.ReadStatus(jobId)!.State, Is.EqualTo(state));
            Assert.That(result, Is.EqualTo(new ImportSweepResult(1, 0, 0, 0, 0, 0)));
        });
    }

    [TestCase(ImportJobState.Uploading)]
    [TestCase(ImportJobState.Analyzed)]
    [TestCase(ImportJobState.Running)]
    public void Sweep_Cancels_And_Cleans_Abandoned_Jobs(ImportJobState state)
    {
        var jobId = CreateJob(state);

        time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        var result = service.Sweep();

        var status = temp.Store.ReadStatus(jobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(HasChunks(jobId), Is.False);
            Assert.That(HasReports(jobId), Is.False);
            Assert.That(status.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(status.FinishedUtc, Is.EqualTo(time.UtcNow));
            Assert.That(status.ErrorMessage, Is.EqualTo(ImportJobCleanupService.AbandonedError));
            Assert.That(result.JobsAbandoned, Is.EqualTo(1));
            Assert.That(temp.Store.ReadJob(jobId), Is.Not.Null, "the job stays in the recent imports list");
        });
    }

    [Test]
    public void Sweep_Counts_Heartbeat_As_Activity()
    {
        var jobId = CreateJob(ImportJobState.Running);
        time.Advance(TimeSpan.FromDays(6));
        temp.Store.UpdateStatus(jobId, s => s with { HeartbeatUtc = time.UtcNow });

        time.Advance(TimeSpan.FromDays(6));
        service.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(temp.Store.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Running));
            Assert.That(HasChunks(jobId), Is.True);
        });
    }

    [Test]
    public void Sweep_Deletes_Old_Job_Folder_Without_Readable_Status()
    {
        var missingStatus = CreateJob(ImportJobState.Uploading);
        var corruptStatus = CreateJob(ImportJobState.Uploading);
        File.Delete(temp.JobPath(missingStatus, "status.json"));
        File.WriteAllText(temp.JobPath(corruptStatus, "status.json"), "broken");

        var early = service.Sweep();
        bool keptWhileYoung = temp.Store.JobExists(missingStatus) && temp.Store.JobExists(corruptStatus);
        time.Advance(TimeSpan.FromDays(8));
        var late = service.Sweep();

        Assert.Multiple(() =>
        {
            Assert.That(keptWhileYoung, Is.True);
            Assert.That(early.JobsDeleted, Is.Zero);
            Assert.That(temp.Store.JobExists(missingStatus), Is.False);
            Assert.That(temp.Store.JobExists(corruptStatus), Is.False);
            Assert.That(late.JobsDeleted, Is.EqualTo(2));
        });
    }

    [Test]
    public void Sweep_Continues_After_A_Job_Fails_And_Ignores_Foreign_Folders()
    {
        var locked = CreateJob(ImportJobState.Completed);
        var other = CreateJob(ImportJobState.Completed);
        Directory.CreateDirectory(Path.Combine(temp.Root, "not-a-job"));

        ImportSweepResult result;
        using (File.Open(temp.JobPath(locked, "chunks", "0.ndjson.gz"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = service.Sweep();
        }

        Assert.Multiple(() =>
        {
            Assert.That(result.JobsChecked, Is.EqualTo(2));
            Assert.That(result.Errors, Is.EqualTo(1));
            Assert.That(HasChunks(other), Is.False);
            Assert.That(Directory.Exists(Path.Combine(temp.Root, "not-a-job")), Is.True);
        });
    }

    [Test]
    public void SweepIfDue_Runs_At_Most_Once_Per_Interval()
    {
        CreateJob(ImportJobState.Completed);

        var first = service.SweepIfDue();
        var second = service.SweepIfDue();
        time.Advance(ImportJobCleanupService.SweepInterval);
        var third = service.SweepIfDue();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Null);
            Assert.That(third, Is.Not.Null);
        });
    }

    [Test]
    public void DeleteChunks_Removes_Only_Chunks()
    {
        var jobId = CreateJob(ImportJobState.Completed);

        service.DeleteChunks(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(HasChunks(jobId), Is.False);
            Assert.That(HasReports(jobId), Is.True);
        });
    }

    [Test]
    public void DeleteImportFiles_Removes_Chunks_And_Reports_And_Keeps_Finished_State()
    {
        var jobId = CreateJob(ImportJobState.Completed);

        bool deleted = service.DeleteImportFiles(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(deleted, Is.True);
            Assert.That(HasChunks(jobId), Is.False);
            Assert.That(HasReports(jobId), Is.False);
            Assert.That(temp.Store.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Completed));
            Assert.That(temp.Store.ReadJob(jobId), Is.Not.Null);
        });
    }

    [Test]
    public void DeleteImportFiles_Cancels_An_Unfinished_Job()
    {
        var jobId = CreateJob(ImportJobState.Analyzed);
        time.Advance(TimeSpan.FromMinutes(5));

        service.DeleteImportFiles(jobId);

        var status = temp.Store.ReadStatus(jobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(status.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(status.FinishedUtc, Is.EqualTo(time.UtcNow));
            Assert.That(HasChunks(jobId), Is.False);
            Assert.That(HasReports(jobId), Is.False);
        });
    }

    [Test]
    public void DeleteImportFiles_Returns_False_For_Unknown_Job() =>
        Assert.That(service.DeleteImportFiles(Guid.NewGuid()), Is.False);
}
