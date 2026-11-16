using CMS.ContactManagement;
using CMS.DataEngine;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// Two import calls for one job at the same time, on two threads with two stores (two application instances) and the
/// real database, which runs with read committed snapshot isolation like Azure SQL (<see cref="IntegrationDatabase"/>):
/// there a lookup does not wait for, and does not see, contacts another call has inserted but not committed.
/// </summary>
public class ConcurrentCallsTests : ImportIntegrationTest
{
    private static readonly TimeSpan wait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The real repository with points where a test can run code, and a switch that turns the job lock off to show
    /// what it prevents.
    /// </summary>
    private sealed class HookedRepository(bool useJobLock) : IContactImportRepository
    {
        private readonly ContactImportRepository inner = new(Provider<ContactInfo>.Instance);

        /// <summary>Runs after a key lookup returned.</summary>
        public Action? AfterLookup { get; set; }

        /// <summary>Runs after the contacts of a batch are written, before the transaction commits.</summary>
        public Action? BeforeCommit { get; set; }

        public IReadOnlyList<ContactMatch> FindMatches(ImportProcessingContext context, IReadOnlyList<object> keys)
        {
            var matches = inner.FindMatches(context, keys);
            AfterLookup?.Invoke();

            return matches;
        }

        public IReadOnlyList<(int Index, int EquivalentIndex)> FindEquivalentKeys(IReadOnlyList<string> keys) => inner.FindEquivalentKeys(keys);

        public IReadOnlyList<int> FindMissingReferences(ImportField field, IReadOnlyList<object> values) => inner.FindMissingReferences(field, values);

        public void Write(ImportProcessingContext context, IReadOnlyList<ContactCreate> creates, IReadOnlyList<ContactUpdate> updates, DateTime now)
        {
            // The write of the repository joins this transaction, so the hook runs while its rows are not committed.
            using var transaction = new CMSTransactionScope();
            inner.Write(context, creates, updates, now);
            BeforeCommit?.Invoke();
            transaction.Commit();
        }

        public IDisposable? TryLockJob(Guid jobId, TimeSpan timeout) => useJobLock ? inner.TryLockJob(jobId, timeout) : new NoLock();

        private sealed class NoLock : IDisposable
        {
            public void Dispose()
            {
                // Nothing was locked.
            }
        }
    }

    /// <summary>
    /// One job and two application instances that process it with one batch per call.
    /// </summary>
    private sealed class TwoInstances : IDisposable
    {
        public TwoInstances(bool useJobLock, int batchSize)
        {
            RepositoryA = new HookedRepository(useJobLock);
            RepositoryB = new HookedRepository(useJobLock);
            Harness = new ImportHarness(
                options =>
                {
                    options.BatchSize = batchSize;
                    options.ProcessingTimeBudgetSeconds = 0;
                },
                repository: RepositoryA);
            var storeB = Harness.Temp.NewStore();
            B = new ImportProcessor(
                storeB,
                Harness.Schema,
                RepositoryB,
                new ImportJobCleanupService(storeB, Harness.Temp.Options, TimeProvider.System, new ListLogger<ImportJobCleanupService>(Harness.Log)),
                new ImportCacheInvalidator(),
                Harness.Temp.Options,
                TimeProvider.System,
                new ListLogger<ImportProcessor>(Harness.Log));
        }

        public ImportHarness Harness { get; }

        public HookedRepository RepositoryA { get; }

        public HookedRepository RepositoryB { get; }

        public ImportProcessor A => Harness.Processor;

        public ImportProcessor B { get; }

        public Guid UploadAnalyzedAndConfirmed(int rows)
        {
            var jobId = Harness.Upload(Email, [Email, FirstName], [.. Enumerable.Range(1, rows).Select(i => new string?[] { $"user{i}@concurrent.test", $"Name {i}" })]);
            Assert.That(Harness.Run(jobId, ImportPhase.Analyze).Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(A.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));

            return jobId;
        }

        public ImportAggregates Totals(Guid jobId) => ImportAggregates.From(Harness.Store.ReadStatus(jobId)!.Aggregates);

        public void Dispose() => Harness.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="call"/> on another thread the way another web request runs: without the ambient context
    /// of the starting thread. Xperience keeps its current database connection in that context; a task that inherits
    /// it would share the connection (and its session lock) with the thread that started it.
    /// </summary>
    private static Task<T> OtherRequest<T>(Func<T> call)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(call);
        }
    }

    private static int DuplicateEmails() =>
        IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM (SELECT [ContactEmail] FROM [OM_Contact] GROUP BY [ContactEmail] HAVING COUNT(*) > 1) AS duplicates");

    [Test]
    public void Database_Uses_Read_Committed_Snapshot_Isolation_Like_Azure_Sql() =>
        Assert.That(IntegrationDatabase.Scalar<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"), Is.True);

    [Test]
    public void Repeated_Call_While_The_First_Has_Not_Committed_Its_Batch_Writes_Nothing()
    {
        using var instances = new TwoInstances(useJobLock: true, batchSize: 2);
        var jobId = instances.UploadAnalyzedAndConfirmed(4);
        var started = instances.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        // The first call has inserted the contacts of the second batch and not committed yet when the browser's
        // repeated call (same driver token) reaches the other instance.
        ImportProgress? repeated = null;
        bool repeatedLookedUp = false;
        instances.RepositoryB.AfterLookup = () => repeatedLookedUp = true;
        instances.RepositoryA.BeforeCommit = () =>
        {
            instances.RepositoryA.BeforeCommit = null;
            var call = OtherRequest(() => instances.B.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false));
            Assert.That(call.Wait(wait), Is.True, "The repeated call must give up waiting for the job lock.");
            repeated = call.Result;
        };

        var first = instances.A.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false);
        var repeatedAgain = instances.B.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false);

        var totals = instances.Totals(jobId);
        Assert.Multiple(() =>
        {
            Assert.That(started.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(repeated!.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(repeated.Error, Is.EqualTo(ImportProcessor.BusyError));
            Assert.That(repeated.DriverToken, Is.EqualTo(started.DriverToken));
            Assert.That(repeatedLookedUp, Is.False, "The call without the lock did not even look contacts up.");
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(repeatedAgain.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(ContactCount(), Is.EqualTo(4));
            Assert.That(DuplicateEmails(), Is.Zero);
            Assert.That(totals.Rows, Is.EqualTo(4));
            Assert.That(totals.Created, Is.EqualTo(4));
        });
    }

    [Test]
    public void Without_The_Job_Lock_The_Same_Calls_Create_Contacts_Twice()
    {
        // The control for the test above: the same overlap with the job lock turned off. It shows that the overlap
        // is real on this database and that the lock, nothing else, prevents the duplicates.
        using var instances = new TwoInstances(useJobLock: false, batchSize: 2);
        var jobId = instances.UploadAnalyzedAndConfirmed(4);
        var started = instances.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        using var repeatedLookedUp = new ManualResetEventSlim();
        Task<ImportProgress>? repeated = null;
        instances.RepositoryB.AfterLookup = repeatedLookedUp.Set;
        instances.RepositoryA.BeforeCommit = () =>
        {
            instances.RepositoryA.BeforeCommit = null;
            repeated = OtherRequest(() => instances.B.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false));

            // The repeated call looks the keys up while the first call's contacts are not committed: it finds none.
            Assert.That(repeatedLookedUp.Wait(wait), Is.True, "Under read committed snapshot isolation the lookup does not wait for the open transaction.");
        };

        var first = instances.A.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false);
        Assert.That(repeated!.Wait(wait), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted), "The checkpoint of the replaced call is refused, but its contacts are committed.");
            Assert.That(repeated.Result.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(ContactCount(), Is.EqualTo(6), "Two contacts exist twice.");
            Assert.That(DuplicateEmails(), Is.EqualTo(2));
        });
    }

    [Test]
    public void Two_Instances_Calling_At_The_Same_Time_Import_Every_Row_Once()
    {
        const int rows = 120;
        using var instances = new TwoInstances(useJobLock: true, batchSize: 5);
        var jobId = instances.UploadAnalyzedAndConfirmed(rows);
        var started = instances.A.ProcessNext(jobId, ImportPhase.Execute, null, false);

        // Both instances keep calling with the browser's token, as fast as they can, until the import is finished.
        using var go = new ManualResetEventSlim();
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<ImportProcessOutcome>();
        ImportProgress Drive(ImportProcessor processor)
        {
            go.Wait();
            ImportProgress progress;
            int calls = 0;
            do
            {
                progress = processor.ProcessNext(jobId, ImportPhase.Execute, started.DriverToken, false);
                outcomes.Add(progress.Outcome);
            }
            while (progress.Outcome is ImportProcessOutcome.Processed or ImportProcessOutcome.TransientError or ImportProcessOutcome.Interrupted && ++calls < 2000);

            return progress;
        }

        var a = OtherRequest(() => Drive(instances.A));
        var b = OtherRequest(() => Drive(instances.B));
        go.Set();
        Assert.That(Task.WaitAll([a, b], TimeSpan.FromMinutes(2)), Is.True);

        var totals = instances.Totals(jobId);
        TestContext.Out.WriteLine("Outcomes: " + string.Join(", ", outcomes.GroupBy(o => o).Select(g => $"{g.Key} x {g.Count()}")));
        Assert.Multiple(() =>
        {
            Assert.That(a.Result.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(b.Result.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(outcomes, Has.None.EqualTo(ImportProcessOutcome.Interrupted), "No call was replaced in the middle of its work.");
            Assert.That(outcomes, Has.None.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(ContactCount(), Is.EqualTo(rows));
            Assert.That(DuplicateEmails(), Is.Zero);
            Assert.That(totals.Rows, Is.EqualTo(rows));
            Assert.That(totals.Created, Is.EqualTo(rows));
            Assert.That(totals.Updated + totals.Unchanged + totals.Skipped, Is.Zero);
            Assert.That(instances.Harness.Store.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Completed));
        });
    }

    [Test]
    public void Job_Lock_Is_Per_Job_And_Released_On_Dispose()
    {
        var repository = new ContactImportRepository(Provider<ContactInfo>.Instance);
        var job = Guid.NewGuid();
        var otherJob = Guid.NewGuid();

        var held = repository.TryLockJob(job, TimeSpan.FromSeconds(1));
        var (sameJob, differentJob) = OtherRequest(() =>
        {
            using var same = repository.TryLockJob(job, TimeSpan.FromMilliseconds(200));
            using var different = repository.TryLockJob(otherJob, TimeSpan.FromMilliseconds(200));

            return (same is not null, different is not null);
        }).Result;
        held!.Dispose();
        held.Dispose();
        bool afterRelease = OtherRequest(() =>
        {
            using var again = repository.TryLockJob(job, TimeSpan.FromMilliseconds(200));

            return again is not null;
        }).Result;

        Assert.Multiple(() =>
        {
            Assert.That(sameJob, Is.False);
            Assert.That(differentJob, Is.True);
            Assert.That(afterRelease, Is.True);
            Assert.That(ContactImportRepository.JobLockResource(job), Has.Length.LessThanOrEqualTo(255));
        });
    }
}
