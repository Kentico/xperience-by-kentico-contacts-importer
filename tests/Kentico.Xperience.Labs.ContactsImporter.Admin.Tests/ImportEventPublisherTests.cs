using CMS.Base;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

using Microsoft.Extensions.DependencyInjection;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

public class ImportEventPublisherTests
{
    private sealed class RecordingHandler(Func<AfterContactImportEvent, Task>? handle = null) : IAsyncEventHandler<AfterContactImportEvent>
    {
        public List<ContactImportEventData> Received { get; } = [];

        public List<int> Threads { get; } = [];

        public async Task HandleAsync(AfterContactImportEvent asyncEvent, CancellationToken cancellationToken)
        {
            Received.Add(asyncEvent.Data);
            Threads.Add(Environment.CurrentManagedThreadId);
            if (handle is not null)
            {
                await handle(asyncEvent);
            }
        }
    }

    private static readonly MutableTimeProvider time = new(TestData.Now);

    private static ImportJob Job() => TestData.Job(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")) with
    {
        Options = new ImportOptions
        {
            RecipientListId = 7,
            Unsubscribed = UnsubscribedBehavior.Resubscribe,
            Consent = ConsentMode.Blanket,
            ConsentId = 9,
            SourceField = "ContactSource",
            SourceValue = "import-2026-11-16",
            ReportIncludesKey = true,
        },
        Attestation = new ImportAttestation { UserId = 77, Utc = TestData.Now.AddMinutes(-5), StatementVersion = "1" },
    };

    private static ImportJobStatus Status() => new()
    {
        State = ImportJobState.Completed,
        Aggregates = new Dictionary<string, long>
        {
            [ImportAggregateKeys.Rows] = 3,
            [ImportAggregateKeys.ListAdded] = 2,
            [ImportAggregateKeys.FieldChangedPrefix + "ContactFirstName"] = 2,
            [ImportAggregateKeys.FieldClearedPrefix + "ContactFirstName"] = 1,
        },
    };

    private static ImportEventPublisher Publisher(List<string> log, params IAsyncEventHandler<AfterContactImportEvent>[] handlers) =>
        new(handlers, time, new ListLogger<ImportEventPublisher>(log), new FakeAudienceResolver());

    [Test]
    public void Event_Data_Has_The_Job_The_Options_The_Counts_And_The_Attestation()
    {
        var handler = new RecordingHandler();
        var log = new List<string>();

        Publisher(log, handler).Publish(Job(), Status(), ContactImportStatus.Completed, isDryRun: false, userId: 6, new ImportAudience(7, 9, "hash-used"));

        var data = handler.Received.Single();
        Assert.Multiple(() =>
        {
            Assert.That(data.JobID, Is.EqualTo(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")));
            Assert.That(data.UserID, Is.EqualTo(6));
            Assert.That(data.CreatedByUserID, Is.EqualTo(53));
            Assert.That(data.Timestamp, Is.EqualTo(time.GetLocalNow().DateTime));
            Assert.That(data.FileName, Is.EqualTo("contacts.csv"));
            Assert.That(data.FileSize, Is.EqualTo(1234));
            Assert.That(data.IsDryRun, Is.False);
            Assert.That(data.Status, Is.EqualTo(ContactImportStatus.Completed));
            Assert.That(data.Options.NewContacts, Is.EqualTo(ContactImportNewContacts.Create));
            Assert.That(data.Options.ExistingContacts, Is.EqualTo(ContactImportExistingContacts.Update));
            Assert.That(data.Options.EmptyValues, Is.EqualTo(ContactImportEmptyValues.Ignore));
            Assert.That(data.Options.MultipleMatches, Is.EqualTo(ContactImportMultipleMatches.UpdateNewest));
            Assert.That(data.Options.InvalidEmail, Is.EqualTo(ContactImportInvalidEmail.SkipRow));
            Assert.That(data.Options.Unsubscribed, Is.EqualTo(ContactImportUnsubscribed.Resubscribe));
            Assert.That(data.Options.BouncedAddress, Is.EqualTo(ContactImportBouncedAddress.SkipAndReport));
            Assert.That(data.Options.Consent, Is.EqualTo(ContactImportConsent.Blanket));
            Assert.That(data.Options.RevokedConsent, Is.EqualTo(ContactImportRevokedConsent.Keep));
            Assert.That(data.Options.SourceStamp, Is.EqualTo(ContactImportSourceStamp.CreatedOnly));
            Assert.That(data.Options.SourceValue, Is.EqualTo("import-2026-11-16"));
            Assert.That(data.Options.ReportIncludesKey, Is.True);
            Assert.That(data.Counts.Rows, Is.EqualTo(3));
            Assert.That(data.Counts.ListAdded, Is.EqualTo(2));
            Assert.That(data.Counts.Created, Is.Zero);
            Assert.That(data.Counts.FieldChanges.Select(f => (f.FieldName, f.Changed, f.Cleared)), Is.EqualTo(new[] { ("ContactFirstName", 2L, 1L) }));
            Assert.That(data.RecipientListID, Is.EqualTo(7));
            Assert.That(data.ConsentID, Is.EqualTo(9));
            Assert.That(data.ConsentHash, Is.EqualTo("hash-used"));
            Assert.That(data.AttestationUserID, Is.EqualTo(77));
            Assert.That(data.AttestationTimeUtc, Is.EqualTo(TestData.Now.AddMinutes(-5)));
            Assert.That(data.AttestationStatementVersion, Is.EqualTo("1"));
            Assert.That(data.ImportSourceFieldName, Is.EqualTo("ContactSource"));
            Assert.That(log, Is.Empty);
        });
    }

    [Test]
    public void Event_Data_Of_A_Job_Without_List_Consent_And_User_Has_No_Such_Values()
    {
        var handler = new RecordingHandler();
        var job = TestData.Job() with { Options = new ImportOptions { ConsentId = 9 } };

        Publisher([], handler).Publish(job, Status() with { State = ImportJobState.Analyzed }, ContactImportStatus.Analyzed, isDryRun: true);

        var data = handler.Received.Single();
        Assert.Multiple(() =>
        {
            Assert.That(data.UserID, Is.EqualTo(53), "Without a known user: the user who created the import.");
            Assert.That(data.IsDryRun, Is.True);
            Assert.That(data.RecipientListID, Is.Null);
            Assert.That(data.ConsentID, Is.Null, "A consent that is selected but not used is not reported.");
            Assert.That(data.ConsentHash, Is.Null);
            Assert.That(data.AttestationUserID, Is.Null);
            Assert.That(data.AttestationTimeUtc, Is.Null);
            Assert.That(data.AttestationStatementVersion, Is.Null);
            Assert.That(data.ImportSourceFieldName, Is.Null);
            Assert.That(data.Options.SourceValue, Is.Null);
            Assert.That(data.Options.Consent, Is.EqualTo(ContactImportConsent.None));
        });
    }

    [Test]
    public void Consent_Hash_Is_Read_When_The_Caller_Does_Not_Know_It()
    {
        var handler = new RecordingHandler();

        Publisher([], handler).Publish(Job(), Status(), ContactImportStatus.Cancelled, isDryRun: false);

        Assert.That(handler.Received.Single().ConsentHash, Is.EqualTo(ProcessingTestData.ConsentHash));
    }

    [Test]
    public void Handlers_Run_Off_The_Calling_Thread_And_The_Call_Waits_For_Them()
    {
        bool finished = false;
        var handler = new RecordingHandler(async _ =>
        {
            await Task.Delay(50);
            finished = true;
        });

        Publisher([], handler).Publish(Job(), Status(), ContactImportStatus.Completed, isDryRun: false);

        Assert.Multiple(() =>
        {
            Assert.That(finished, Is.True);
            Assert.That(handler.Threads.Single(), Is.Not.EqualTo(Environment.CurrentManagedThreadId));
        });
    }

    [Test]
    public void Failing_Handler_Is_Logged_And_Stops_Neither_The_Other_Handlers_Nor_The_Caller()
    {
        var log = new List<string>();
        var failing = new RecordingHandler(_ => throw new InvalidOperationException("handler broke"));
        var failingLater = new RecordingHandler(async _ =>
        {
            await Task.Yield();
            throw new TimeoutException("handler timed out");
        });
        var working = new RecordingHandler();

        Assert.That(() => Publisher(log, failing, failingLater, working).Publish(Job(), Status(), ContactImportStatus.Completed, isDryRun: false), Throws.Nothing);

        Assert.Multiple(() =>
        {
            Assert.That(working.Received, Has.Count.EqualTo(1));
            Assert.That(log, Has.Count.EqualTo(2));
            Assert.That(log, Has.All.Contains("CONTACT_IMPORT_EVENT").And.All.Contains(nameof(RecordingHandler)));
            Assert.That(log[0], Does.Contain("handler broke"));
            Assert.That(log[1], Does.Contain("handler timed out"));
        });
    }

    [Test]
    public void Nothing_Happens_Without_Handlers()
    {
        var log = new List<string>();

        Assert.That(() => Publisher(log).Publish(Job(), Status(), ContactImportStatus.Completed, isDryRun: false), Throws.Nothing);
        Assert.That(log, Is.Empty);
    }

    [Test]
    public void Handler_Registered_With_AddEventHandler_Is_Called_Through_The_Registered_Publisher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContactsImporter();
        services.AddEventHandler<AfterContactImportEvent, CountingHandler>();
        using var provider = services.BuildServiceProvider();
        CountingHandler.Count = 0;

        provider.GetRequiredService<IImportEventPublisher>().Publish(TestData.Job(), Status(), ContactImportStatus.Completed, isDryRun: false);

        Assert.That(CountingHandler.Count, Is.EqualTo(1));
    }

    private sealed class CountingHandler : IAsyncEventHandler<AfterContactImportEvent>
    {
        public static int Count { get; set; }

        public Task HandleAsync(AfterContactImportEvent asyncEvent, CancellationToken cancellationToken)
        {
            Count++;

            return Task.CompletedTask;
        }
    }
}
