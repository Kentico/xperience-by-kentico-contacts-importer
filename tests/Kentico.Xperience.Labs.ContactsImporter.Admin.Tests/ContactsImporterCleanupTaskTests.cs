using CMS.Scheduler;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

internal class ContactsImporterCleanupTaskTests
{
    private TempStore temp = null!;
    private MutableTimeProvider time = null!;
    private ImportJobCleanupService cleanup = null!;
    private ContactsImporterCleanupTask task = null!;

    [SetUp]
    public void SetUp()
    {
        temp = new TempStore();
        time = new MutableTimeProvider(TestData.Now);
        cleanup = new ImportJobCleanupService(temp.Store, temp.Options, time, NullLogger<ImportJobCleanupService>.Instance);
        task = new ContactsImporterCleanupTask(cleanup, NullLogger<ContactsImporterCleanupTask>.Instance);
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    private Guid CreateFinishedJob()
    {
        var job = TestData.Job() with { CreatedUtc = time.UtcNow };
        temp.Store.CreateJob(job);
        temp.Store.WriteChunk(job.JobId, new ImportChunkInfo { Index = 0, Sha256 = new string('a', 64), Rows = 1, FirstRow = 1, LastRow = 1 }, [1, 2, 3]);
        temp.Store.WriteStatus(job.JobId, new ImportJobStatus
        {
            State = ImportJobState.Completed,
            UpdatedUtc = time.UtcNow,
            FinishedUtc = time.UtcNow,
        });

        return job.JobId;
    }

    [Test]
    public void Run_Sweeps_And_Reports_Counts()
    {
        var expired = CreateFinishedJob();
        var alsoExpired = CreateFinishedJob();
        time.Advance(TimeSpan.FromDays(91));
        var fresh = CreateFinishedJob();

        string message = task.Run(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(temp.Store.JobExists(alsoExpired), Is.False);
            Assert.That(temp.Store.JobExists(expired), Is.False);
            Assert.That(temp.Store.HasChunks(fresh), Is.False);
            Assert.That(message, Is.EqualTo(ContactsImporterCleanupTask.FormatResult(new ImportSweepResult(3, 1, 0, 0, 2, 0))));
            Assert.That(message, Does.Contain("Checked 3 imports"));
        });
    }

    [Test]
    public void Run_Ignores_The_Lazy_Sweep_Interval()
    {
        CreateFinishedJob();
        cleanup.SweepIfDue();
        var other = CreateFinishedJob();

        task.Run(CancellationToken.None);

        Assert.That(temp.Store.HasChunks(other), Is.False);
    }

    [Test]
    public void Run_Counts_A_Failing_Job_And_Cleans_The_Others()
    {
        var locked = CreateFinishedJob();
        var other = CreateFinishedJob();

        string message;
        using (File.Open(temp.JobPath(locked, "chunks", "0.ndjson.gz"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            message = task.Run(CancellationToken.None);
        }

        Assert.Multiple(() =>
        {
            Assert.That(temp.Store.HasChunks(other), Is.False);
            Assert.That(message, Does.EndWith("errors 1."));
        });
    }

    [Test]
    public void Run_Returns_Error_Message_Instead_Of_Throwing()
    {
        var failing = new ContactsImporterCleanupTask(new ThrowingCleanupService(), NullLogger<ContactsImporterCleanupTask>.Instance);

        string message = failing.Run(CancellationToken.None);

        Assert.That(message, Is.EqualTo("Error: storage unavailable"));
    }

    [Test]
    public void Run_Does_Nothing_When_Cancelled()
    {
        var jobId = CreateFinishedJob();

        string message = task.Run(new CancellationToken(canceled: true));

        Assert.Multiple(() =>
        {
            Assert.That(temp.Store.HasChunks(jobId), Is.True);
            Assert.That(message, Does.StartWith("Cancelled"));
        });
    }

    [Test]
    public async Task Execute_Completes_With_A_Result()
    {
        CreateFinishedJob();

        var result = await task.Execute(null!, CancellationToken.None);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void Task_Is_Registered_With_Its_Identifier()
    {
        var registration = typeof(ContactsImporterCleanupTask).Assembly
            .GetCustomAttributes(typeof(RegisterScheduledTaskAttribute), inherit: false)
            .Cast<RegisterScheduledTaskAttribute>()
            .SingleOrDefault(a => a.TaskType == typeof(ContactsImporterCleanupTask));

        Assert.That(registration?.Identifier, Is.EqualTo(ContactsImporterCleanupTask.Identifier));
    }

    [Test]
    public void Task_Can_Be_Created_From_The_Registered_Services()
    {
        // The scheduler creates tasks with ActivatorUtilities.CreateInstance from the application services.
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddContactsImporter();
        using var provider = services.BuildServiceProvider();

        object created = ActivatorUtilities.CreateInstance(provider, typeof(ContactsImporterCleanupTask));

        Assert.That(created, Is.InstanceOf<IScheduledTask>());
    }

    private sealed class ThrowingCleanupService : IImportJobCleanupService
    {
        public void DeleteChunks(Guid jobId) => throw new NotSupportedException();

        public ImportSweepResult? SweepIfDue() => throw new NotSupportedException();

        public ImportSweepResult Sweep() => throw new IOException("storage unavailable");
    }
}
