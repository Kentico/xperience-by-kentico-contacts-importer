using System.IO.Compression;
using System.Text;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.UIPages;

/// <summary>
/// Page command logic of the Options, Review, Run and Results steps: <c>SAVE_OPTIONS</c>, <c>PROCESS_NEXT</c>,
/// <c>CONFIRM_IMPORT</c>, <c>CANCEL_IMPORT</c>, <c>GET_REPORT</c> and the extended <c>GET_JOB</c>.
/// </summary>
internal class ImportProcessCommandTests
{
    private const int UserId = 53;

    private TempStore temp = null!;
    private MutableTimeProvider time = null!;
    private FakeContactFieldSchemaProvider schema = null!;
    private FakeContactRepository repository = null!;
    private FakeCacheInvalidator cache = null!;
    private ImportProcessGate gate = null!;
    private ImportCommandService service = null!;
    private Guid jobId;
    private string id = null!;

    [SetUp]
    public void SetUp()
    {
        temp = new TempStore(o =>
        {
            o.ChunkSizeBytes = 64 * 1024;
            o.BatchSize = 10;
        });
        time = new MutableTimeProvider(TestData.Now);
        schema = new FakeContactFieldSchemaProvider(TestData.Fields);
        repository = new FakeContactRepository();
        cache = new FakeCacheInvalidator();
        gate = new ImportProcessGate();
        service = CreateService(new ImportReportPager(temp.Store));

        jobId = Guid.NewGuid();
        id = jobId.ToString("D");
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    private ImportCommandService CreateService(IImportReportPager pager)
    {
        var ingest = new ChunkIngestService(temp.Store, schema, temp.Options, time);
        var cleanup = new ImportJobCleanupService(temp.Store, temp.Options, time, NullLogger<ImportJobCleanupService>.Instance);
        var processor = new ImportProcessor(temp.Store, schema, repository, cleanup, cache, temp.Options, time, NullLogger<ImportProcessor>.Instance);

        return new ImportCommandService(
            temp.Store,
            ingest,
            schema,
            cleanup,
            new NoUserNames(),
            processor,
            pager,
            new ImportReportReader(temp.Store),
            gate,
            new ResourceImportTexts(),
            new FakeChoiceProvider(),
            new FakeChoiceProvider(),
            temp.Options,
            time);
    }

    /// <summary>Uploads <paramref name="rows"/> rows (<c>user{n}@example.com</c>) through the commands: the job ends Uploaded.</summary>
    private void Upload(int rows = 25, params (string Source, string Target)[] mapping)
    {
        if (mapping.Length == 0)
        {
            mapping = [("Email", "ContactEmail"), ("First Name", "ContactFirstName")];
        }

        var config = new SaveConfigRequest
        {
            File = new ImportFileDto { Name = "contacts.csv", Size = 1234, Fingerprint = "0a1b2c3d" },
            KeyField = "ContactEmail",
            Mapping = [.. mapping.Select(m => new ImportMappingDto { Source = m.Source, Target = m.Target })],
            DateFormat = "iso",
        };
        Assert.That(service.SaveConfig(id, config, UserId).Code, Is.EqualTo(ImportCommandCodes.OK));

        string[] records = [.. Enumerable.Range(2, rows).Select(r => TestData.Record(r, $"user{r}@example.com"))];
        Assert.That(service.UploadChunk(id, TestData.Payload(jobId, 0, records)).Code, Is.EqualTo(ImportCommandCodes.STORED));
        var profile = new ImportProfileDto { RowsRead = rows, RowsValid = rows, ChunkCount = 1 };
        Assert.That(service.CompleteUpload(id, new CompleteUploadRequest { Profile = profile }).Code, Is.EqualTo(ImportCommandCodes.OK));
    }

    private static ImportOptionsDto Options(
        string newContacts = "create",
        string existingContacts = "update",
        string emptyValues = "ignore",
        string multipleMatches = "updateNewest",
        string invalidEmail = "skipRow",
        string? sourceField = null,
        string? sourceValue = null,
        string sourceStamp = "createdOnly",
        bool reportIncludesKey = false,
        int? recipientListId = null,
        string? unsubscribed = "keep",
        string? bouncedAddress = "addAndReport",
        string? consent = "none",
        int? consentId = null,
        string? revokedConsent = "keep") => new()
        {
            RecipientListId = recipientListId,
            Unsubscribed = unsubscribed,
            BouncedAddress = bouncedAddress,
            Consent = consent,
            ConsentId = consentId,
            RevokedConsent = revokedConsent,
            NewContacts = newContacts,
            ExistingContacts = existingContacts,
            EmptyValues = emptyValues,
            MultipleMatches = multipleMatches,
            InvalidEmail = invalidEmail,
            SourceField = sourceField,
            SourceValue = sourceValue,
            SourceStamp = sourceStamp,
            ReportIncludesKey = reportIncludesKey,
        };

    private static ProcessNextRequest Next(string phase, string? token = null, bool takeOver = false) =>
        new() { Phase = phase, DriverToken = token, TakeOver = takeOver };

    /// <summary>Calls <c>PROCESS_NEXT</c> until the phase is finished; returns the last answer.</summary>
    private ImportProgressDto Drive(string phase, string? token = null)
    {
        for (int i = 0; i < 1000; i++)
        {
            var progress = service.ProcessNext(id, Next(phase, token), UserId);
            if (progress.Outcome != "processed")
            {
                return progress;
            }

            token = progress.DriverToken;
        }

        throw new InvalidOperationException("The phase did not finish.");
    }

    /// <summary><c>CONFIRM_IMPORT</c> of an import that needs no consent confirmation, by a user without that permission.</summary>
    private ImportJobResponse Confirm() => service.Confirm(id, new ConfirmImportRequest(), UserId, canConfirmConsent: false);

    private ImportJobState State() => temp.Store.ReadStatus(jobId)!.State;

    private ImportOptions StoredOptions() => temp.Store.ReadJob(jobId)!.Options;

    private static string Gunzip(string base64)
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    [Test]
    public void GetJob_Returns_Default_Options_Source_Fields_And_Progress()
    {
        Upload();

        var response = service.GetJob(id);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Job!.Options, Is.EqualTo(Options()));
            Assert.That(response.Job.InvalidEmailApplies, Is.False);
            Assert.That(response.Job.SourceFields.Select(f => f.Name), Is.EqualTo(new[] { "ContactLastName", "ContactNotes", "CustomerID", "ContactSource" }));
            Assert.That(response.Job.DefaultSourceValue, Is.EqualTo("import-2026-11-16-contacts.csv"));
            Assert.That(response.Progress!.Phase, Is.EqualTo("analyze"));
            Assert.That(response.Progress.State, Is.EqualTo("Uploaded"));
            Assert.That(response.Progress.RowsProcessed, Is.Zero);
            Assert.That(response.Progress.RowsTotal, Is.EqualTo(25));
            Assert.That(response.Progress.DriverToken, Is.Null);
            Assert.That(response.Progress.Analysis, Is.Null);
            Assert.That(response.Reports, Is.EqualTo(new ImportReportsDto(false, false, false)));
        });
    }

    [Test]
    public void SaveOptions_Stores_The_Options_And_Keeps_An_Uploaded_Job_Uploaded()
    {
        Upload();

        var response = service.SaveOptions(id, Options(
            newContacts: "skip",
            emptyValues: "clear",
            multipleMatches: "updateAll",
            sourceField: "ContactSource",
            sourceValue: "  webinar-2026  ",
            sourceStamp: "createdAndUpdated",
            reportIncludesKey: true));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Uploaded"));
            Assert.That(response.Job!.Options.SourceValue, Is.EqualTo("webinar-2026"));
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions
            {
                NewContacts = NewContactsBehavior.Skip,
                EmptyValues = EmptyValuesBehavior.Clear,
                MultipleMatches = MultipleMatchesBehavior.UpdateAll,
                SourceField = "ContactSource",
                SourceValue = "webinar-2026",
                SourceStamp = SourceStampBehavior.CreatedAndUpdated,
                ReportIncludesKey = true,
            }));
        });
    }

    [Test]
    public void SaveOptions_Without_The_List_And_Consent_Options_Stores_The_Ones_That_Change_Nothing()
    {
        Upload();
        var job = temp.Store.ReadJob(jobId)!;
        temp.Store.WriteJob(job with
        {
            Options = job.Options with
            {
                RecipientListId = FakeChoiceProvider.NewsletterListId,
                Unsubscribed = UnsubscribedBehavior.Resubscribe,
                Consent = ConsentMode.Blanket,
                ConsentId = FakeChoiceProvider.SignupConsentId,
                RevokedConsent = RevokedConsentBehavior.ReAgree,
            },
        });

        // A request of a client that does not know these options: they are not kept from the stored job.
        var response = service.SaveOptions(id, Options(emptyValues: "clear", unsubscribed: null, bouncedAddress: null, consent: null, revokedConsent: null));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear }));
            Assert.That(response.Job!.AttestationRequired, Is.False);
        });
    }

    [Test]
    public void SaveOptions_Refuses_Unknown_Option_Values()
    {
        Upload();

        Assert.Multiple(() =>
        {
            Assert.That(service.SaveOptions(id, null).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, new ImportOptionsDto()).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(newContacts: "Create")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(newContacts: "0")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(existingContacts: "merge")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(emptyValues: "")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(multipleMatches: "newest")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(invalidEmail: "import")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(sourceStamp: "all")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions("not-a-guid", Options()).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(Guid.NewGuid().ToString("D"), Options()).Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions()));
        });
    }

    [TestCase("ContactBirthday", "x", Description = "not a text field")]
    [TestCase("ContactFirstName", "x", Description = "mapped by this import")]
    [TestCase("NoSuchField", "x")]
    [TestCase("ContactSource", "   ", Description = "no value")]
    [TestCase("CustomerID", "123456789012345678901234567890123456789012345678901", Description = "51 characters for a field of 50")]
    public void SaveOptions_Validates_The_Source_Stamp(string sourceField, string sourceValue)
    {
        Upload();

        var response = service.SaveOptions(id, Options(sourceField: sourceField, sourceValue: sourceValue));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_CONFIG));
            Assert.That(response.Errors, Is.Not.Empty);
            Assert.That(StoredOptions().SourceField, Is.Null);
        });
    }

    [Test]
    public void SaveOptions_Applies_The_Invalid_Email_Option_Only_When_Email_Is_Mapped_And_Not_The_Key()
    {
        Upload();
        service.SaveOptions(id, Options(invalidEmail: "importWithoutEmail"));
        var emailIsKey = StoredOptions().InvalidEmail;

        var job = temp.Store.ReadJob(jobId)!;
        temp.Store.WriteJob(job with
        {
            KeyField = "CustomerID",
            Mapping = [.. job.Mapping, new ImportFieldMapping { Source = "Customer", Target = "CustomerID" }],
        });
        var response = service.SaveOptions(id, Options(invalidEmail: "importWithoutEmail"));

        Assert.Multiple(() =>
        {
            Assert.That(emailIsKey, Is.EqualTo(InvalidEmailBehavior.SkipRow));
            Assert.That(response.Job!.InvalidEmailApplies, Is.True);
            Assert.That(StoredOptions().InvalidEmail, Is.EqualTo(InvalidEmailBehavior.ImportWithoutEmail));
        });
    }

    [TestCase(ImportJobState.Configuring)]
    [TestCase(ImportJobState.Uploading)]
    [TestCase(ImportJobState.Running)]
    [TestCase(ImportJobState.Completed)]
    [TestCase(ImportJobState.Failed)]
    [TestCase(ImportJobState.Cancelled)]
    public void SaveOptions_Is_Refused_In_States_Outside_Uploaded_To_Confirmed(ImportJobState state)
    {
        Upload();
        temp.Store.WriteStatus(jobId, temp.Store.ReadStatus(jobId)! with { State = state });

        var response = service.SaveOptions(id, Options(emptyValues: "clear"));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(StoredOptions().EmptyValues, Is.EqualTo(EmptyValuesBehavior.Ignore));
            Assert.That(State(), Is.EqualTo(state));
        });
    }

    [Test]
    public void SaveOptions_After_The_Analysis_Returns_The_Job_To_Uploaded_And_Clears_The_Confirmation()
    {
        Upload();
        Drive("analyze");
        Assert.That(Confirm().Code, Is.EqualTo(ImportCommandCodes.OK));
        var job = temp.Store.ReadJob(jobId)!;
        temp.Store.WriteJob(job with { Attestation = new ImportAttestation { UserId = UserId, Utc = TestData.Now, StatementVersion = "1" } });

        var response = service.SaveOptions(id, Options(newContacts: "skip"));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Uploaded"));
            Assert.That(response.Progress!.Analysis, Is.Null);
            Assert.That(response.Progress.RowsProcessed, Is.Zero);
            Assert.That(response.Reports!.Analyze, Is.False, "the report of the old analysis is no longer offered");
            Assert.That(temp.Store.ReadJob(jobId)!.Attestation, Is.Null);
            Assert.That(StoredOptions().NewContacts, Is.EqualTo(NewContactsBehavior.Skip));
        });
    }

    [Test]
    public void SaveOptions_Without_A_Change_Keeps_The_Analysis()
    {
        Upload();
        Drive("analyze");

        var response = service.SaveOptions(id, Options());

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Analyzed"));
            Assert.That(response.Progress!.Analysis!.Created, Is.EqualTo(25));
        });
    }

    [Test]
    public void SaveOptions_Is_Refused_While_A_Request_Processes_The_Job()
    {
        Upload();
        Assert.That(gate.TryEnter(jobId), Is.True);

        var response = service.SaveOptions(id, Options(emptyValues: "clear"));
        gate.Exit(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(StoredOptions().EmptyValues, Is.EqualTo(EmptyValuesBehavior.Ignore));
            Assert.That(service.SaveOptions(id, Options(emptyValues: "clear")).Code, Is.EqualTo(ImportCommandCodes.OK));
        });
    }

    [Test]
    public void SaveConfig_Drops_A_Source_Field_That_Becomes_A_Mapped_Column()
    {
        Upload();
        service.SaveOptions(id, Options(sourceField: "ContactSource", sourceValue: "webinar"));

        var config = new SaveConfigRequest
        {
            File = new ImportFileDto { Name = "contacts.csv", Size = 1234, Fingerprint = "0a1b2c3d" },
            KeyField = "ContactEmail",
            Mapping = [new ImportMappingDto { Source = "Email", Target = "ContactEmail" }, new ImportMappingDto { Source = "Source", Target = "ContactSource" }],
            DateFormat = "iso",
        };
        Assert.That(service.SaveConfig(id, config, UserId).Code, Is.EqualTo(ImportCommandCodes.OK));

        Assert.Multiple(() =>
        {
            Assert.That(StoredOptions().SourceField, Is.Null);
            Assert.That(StoredOptions().SourceValue, Is.Null);
        });
    }

    [Test]
    public void ProcessNext_Analyzes_In_Bounded_Calls_And_Reports_Server_Counts()
    {
        temp.Options.Value.ProcessingTimeBudgetSeconds = 0;
        Upload(rows: 25);

        var first = service.ProcessNext(id, Next("analyze"), UserId);
        var last = Drive("analyze", first.DriverToken);
        var job = service.GetJob(id);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo("processed"));
            Assert.That(first.Phase, Is.EqualTo("analyze"));
            Assert.That(first.State, Is.EqualTo("Analyzing"));
            Assert.That(first.DriverToken, Is.Not.Null.And.Not.Empty);
            Assert.That(first.RowsProcessed, Is.EqualTo(10), "one batch per call with a zero time budget");
            Assert.That(first.RowsTotal, Is.EqualTo(25));
            Assert.That(first.Aggregates.Created, Is.EqualTo(10));

            Assert.That(last.Outcome, Is.EqualTo("phaseFinished"));
            Assert.That(last.State, Is.EqualTo("Analyzed"));
            Assert.That(last.DriverToken, Is.Null);
            Assert.That(last.Analysis!.Rows, Is.EqualTo(25));
            Assert.That(last.Analysis.Created, Is.EqualTo(25));
            Assert.That(last.AnalyzedUtc, Is.EqualTo(TestData.Now));

            Assert.That(job.Progress!.Analysis!.Created, Is.EqualTo(25));
            Assert.That(job.Reports, Is.EqualTo(new ImportReportsDto(true, false, false)));
            Assert.That(repository.Contacts, Is.Empty, "the analysis writes nothing");
        });
    }

    [Test]
    public void ProcessNext_Runs_The_Import_After_The_Confirmation()
    {
        Upload(rows: 25);
        Drive("analyze");
        var beforeConfirm = service.ProcessNext(id, Next("execute"), UserId);
        var confirmed = Confirm();

        var last = Drive("execute");
        var job = service.GetJob(id);
        var list = service.ListImports().Imports.Single();

        Assert.Multiple(() =>
        {
            Assert.That(beforeConfirm.Outcome, Is.EqualTo("invalidState"));
            Assert.That(confirmed.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(confirmed.Status!.State, Is.EqualTo("Confirmed"));
            Assert.That(last.Outcome, Is.EqualTo("phaseFinished"));
            Assert.That(last.Phase, Is.EqualTo("execute"));
            Assert.That(last.State, Is.EqualTo("Completed"));
            Assert.That(last.Aggregates.Created, Is.EqualTo(25));
            Assert.That(last.Analysis!.Created, Is.EqualTo(25));
            Assert.That(repository.Contacts, Has.Count.EqualTo(25));
            Assert.That(cache.Calls, Is.EqualTo(1));
            Assert.That(job.Progress!.Phase, Is.EqualTo("execute"));
            Assert.That(job.Reports, Is.EqualTo(new ImportReportsDto(true, true, false)));
            Assert.That(list.ImportStarted, Is.True);
            Assert.That(list.Created, Is.EqualTo(25));
            Assert.That(list.Updated, Is.Zero);
            Assert.That(list.CanContinue, Is.False);
        });
    }

    [Test]
    public void ProcessNext_Refuses_Invalid_Requests_Without_Touching_The_Job()
    {
        Upload();

        Assert.Multiple(() =>
        {
            Assert.That(service.ProcessNext(id, null, UserId).Outcome, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.ProcessNext(id, Next("Analyze"), UserId).Outcome, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.ProcessNext(id, Next("import"), UserId).Outcome, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.ProcessNext(id, Next("analyze", new string('a', 65)), UserId).Outcome, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.ProcessNext("../x", Next("analyze"), UserId).Outcome, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.ProcessNext(Guid.NewGuid().ToString("D"), Next("analyze"), UserId).Outcome, Is.EqualTo("jobNotFound"));
            Assert.That(State(), Is.EqualTo(ImportJobState.Uploaded));
        });
    }

    [Test]
    public void ProcessNext_Answers_Busy_While_Another_Request_Processes_The_Job()
    {
        temp.Options.Value.ProcessingTimeBudgetSeconds = 0;
        Upload(rows: 25);
        var first = service.ProcessNext(id, Next("analyze"), UserId);
        ImportProgressDto? second = null;
        ImportProgressDto? stranger = null;
        // A second request of the same driver and one of another tab arrive while the first one is in a batch.
        repository.OnLookup = _ =>
        {
            second ??= service.ProcessNext(id, Next("analyze", first.DriverToken), UserId);
            stranger ??= service.ProcessNext(id, Next("analyze", takeOver: true), UserId);
        };

        var next = service.ProcessNext(id, Next("analyze", first.DriverToken), UserId);
        repository.OnLookup = null;

        Assert.Multiple(() =>
        {
            Assert.That(second!.Outcome, Is.EqualTo(ImportCommandCodes.BUSY));
            Assert.That(second.DriverToken, Is.Null);
            Assert.That(second.RowsProcessed, Is.EqualTo(10));
            Assert.That(second.Error, Is.Not.Empty);
            Assert.That(stranger!.Outcome, Is.EqualTo("activeElsewhere"), "a request without the driver token is another driver");
            Assert.That(stranger.DriverToken, Is.Null);
            Assert.That(stranger.RowsProcessed, Is.EqualTo(10));
            Assert.That(next.Outcome, Is.EqualTo("processed"));
            Assert.That(next.RowsProcessed, Is.EqualTo(20), "no row was processed twice");
            Assert.That(gate.IsBusy(jobId), Is.False);
            Assert.That(Drive("analyze").Outcome, Is.EqualTo("activeElsewhere"), "a call without the token is another driver");
        });
    }

    [Test]
    public void ProcessNext_Reports_A_Failed_Job_And_Releases_It()
    {
        Upload();
        repository.OnLookup = _ => throw new InvalidOperationException("secret@example.com");

        var failed = service.ProcessNext(id, Next("analyze"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Outcome, Is.EqualTo("failed"));
            Assert.That(failed.State, Is.EqualTo("Failed"));
            Assert.That(failed.Error, Is.Not.Empty.And.Not.Contain("secret"));
            Assert.That(gate.IsBusy(jobId), Is.False);
            Assert.That(service.GetJob(id).Status!.Error, Is.EqualTo(failed.Error));
        });
    }

    [Test]
    public void ProcessNext_Reports_Another_Driver_And_Allows_Take_Over_When_It_Is_Gone()
    {
        temp.Options.Value.ProcessingTimeBudgetSeconds = 0;
        Upload(rows: 25);
        service.ProcessNext(id, Next("analyze"), UserId);

        var active = service.ProcessNext(id, Next("analyze"), UserId);
        time.Advance(TimeSpan.FromSeconds(temp.Options.Value.HeartbeatStaleSeconds + 1));
        var paused = service.GetJob(id);
        var stale = service.ProcessNext(id, Next("analyze"), UserId);
        var takenOver = service.ProcessNext(id, Next("analyze", takeOver: true), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(active.Outcome, Is.EqualTo("activeElsewhere"));
            Assert.That(active.RowsProcessed, Is.EqualTo(10));
            Assert.That(paused.Status!.State, Is.EqualTo("Paused"));
            Assert.That(paused.Progress!.Phase, Is.EqualTo("analyze"));
            Assert.That(stale.Outcome, Is.EqualTo("staleRequiresTakeOver"));
            Assert.That(takenOver.Outcome, Is.EqualTo("processed"));
            Assert.That(takenOver.RowsProcessed, Is.EqualTo(20));
        });
    }

    [Test]
    public void Confirm_Is_Refused_Before_The_Analysis_And_When_It_Is_Too_Old()
    {
        Upload();
        var tooEarly = Confirm();
        Drive("analyze");
        time.Advance(TimeSpan.FromHours(temp.Options.Value.AnalysisValidHours + 1));

        var expired = Confirm();

        Assert.Multiple(() =>
        {
            Assert.That(tooEarly.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(tooEarly.Status!.State, Is.EqualTo("Uploaded"));
            Assert.That(expired.Code, Is.EqualTo(ImportCommandCodes.ANALYSIS_EXPIRED));
            Assert.That(expired.Message, Is.Not.Empty);
            Assert.That(expired.Status!.State, Is.EqualTo("Uploaded"));
            Assert.That(service.Confirm("x", null, UserId, false).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.Confirm(Guid.NewGuid().ToString("D"), null, UserId, false).Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
        });
    }

    [Test]
    public void Cancel_During_The_Import_Stops_The_Driver_At_The_Next_Batch()
    {
        temp.Options.Value.ProcessingTimeBudgetSeconds = 0;
        Upload(rows: 25);
        Drive("analyze");
        Confirm();
        var first = service.ProcessNext(id, Next("execute"), UserId);

        var cancelled = service.Cancel(id, UserId);
        var next = service.ProcessNext(id, Next("execute", first.DriverToken), UserId);
        var job = service.GetJob(id);

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(cancelled.Status!.State, Is.EqualTo("Cancelled"));
            Assert.That(next.Outcome, Is.EqualTo("invalidState"));
            Assert.That(next.State, Is.EqualTo("Cancelled"));
            Assert.That(repository.Contacts, Has.Count.EqualTo(10), "contacts written before the cancellation stay");
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
            Assert.That(job.Progress!.Phase, Is.EqualTo("execute"));
            Assert.That(job.Progress.RowsProcessed, Is.EqualTo(10));
            Assert.That(job.Progress.Analysis!.Created, Is.EqualTo(25));
            Assert.That(cache.Calls, Is.Zero);
        });
    }

    [Test]
    public void GetReport_Returns_The_Report_In_Pages_That_Join_To_The_Whole_Report()
    {
        Upload(rows: 25);
        service.SaveOptions(id, Options(reportIncludesKey: true));
        Drive("analyze");
        service = CreateService(new ImportReportPager(temp.Store, maxPageBytes: 200));

        var text = new StringBuilder();
        var pages = new List<GetReportResponse>();
        var request = new GetReportRequest { Phase = "analyze" };
        do
        {
            var page = service.GetReport(id, request);
            Assert.That(page.Code, Is.EqualTo(ImportCommandCodes.OK), page.Message);
            pages.Add(page);
            text.Append(Gunzip(page.Data!));
            request = new GetReportRequest { Phase = "analyze", Part = page.NextPart, Line = page.NextLine };
        }
        while (!pages[^1].Done && pages.Count < 100);

        using var whole = new MemoryStream();
        new ImportReportReader(temp.Store).CopyTo(jobId, ImportPhase.Analyze, whole);
        string expected = Gunzip(Convert.ToBase64String(whole.ToArray()));
        string[] lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Multiple(() =>
        {
            Assert.That(pages, Has.Count.GreaterThan(2), "small pages: several requests");
            Assert.That(pages.Sum(p => p.Rows), Is.EqualTo(25));
            Assert.That(pages.Select(p => p.Header).Distinct(), Is.EqualTo(new[] { "row,outcome,reason,field,key" }));
            Assert.That(pages[0].FileName, Is.EqualTo("contact-import-analyze-report.csv.gz"));
            Assert.That(pages.SkipLast(1).Select(p => Gunzip(p.Data!).Length), Has.All.LessThan(200 + 100), "a page ends one line after the limit at most");
            Assert.That(lines, Has.Length.EqualTo(25));
            Assert.That(lines[0], Is.EqualTo("2,created,,,user2@example.com"));
            Assert.That(lines[^1], Is.EqualTo("26,created,,,user26@example.com"));
            Assert.That(pages[0].Header + "\n" + text, Is.EqualTo(expected), "the pages join to exactly the report CopyTo writes");
        });
    }

    [Test]
    public void GetReport_Is_Refused_For_A_Missing_Or_Outdated_Report_And_Invalid_Requests()
    {
        Upload();
        var beforeAnalysis = service.GetReport(id, new GetReportRequest { Phase = "analyze" });
        Drive("analyze");
        var executeReport = service.GetReport(id, new GetReportRequest { Phase = "execute" });
        service.SaveOptions(id, Options(newContacts: "skip"));
        var afterOptionsChange = service.GetReport(id, new GetReportRequest { Phase = "analyze" });

        Assert.Multiple(() =>
        {
            Assert.That(beforeAnalysis.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(executeReport.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(afterOptionsChange.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE), "report parts of a reset analysis are not served");
            Assert.That(afterOptionsChange.Data, Is.Null);
            Assert.That(service.GetReport(id, null).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.GetReport(id, new GetReportRequest { Phase = "all" }).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.GetReport(id, new GetReportRequest { Phase = "analyze", Part = -1 }).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.GetReport(id, new GetReportRequest { Phase = "analyze", Line = -1 }).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.GetReport("..", new GetReportRequest { Phase = "analyze" }).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.GetReport(Guid.NewGuid().ToString("D"), new GetReportRequest { Phase = "analyze" }).Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
        });
    }

    [Test]
    public void ReportPager_Replaces_Rows_Processed_Again_And_Survives_A_Cursor_To_A_Deleted_Part()
    {
        var store = temp.Store;
        var job = TestData.Job();
        store.CreateJob(job);
        // A call wrote rows 2-5, stopped before its checkpoint, and the next call processed rows 4-7 again.
        store.WriteReport(job.JobId, ImportReportFormat.PartFileName(ImportPhase.Execute, 2), TestData.Gzip("2,created,,\n3,created,,\n4,created,,\n5,created,,\n"));
        store.WriteReport(job.JobId, ImportReportFormat.PartFileName(ImportPhase.Execute, 4), TestData.Gzip("4,updated,,\n5,updated,,\nnot a row\n6,created,,\n7,created,,\n"));
        var pager = new ImportReportPager(store, maxPageBytes: 20);

        var first = pager.ReadPage(job.JobId, ImportPhase.Execute, 0, 0)!;
        var second = pager.ReadPage(job.JobId, ImportPhase.Execute, first.NextPart, first.NextLine)!;
        var third = pager.ReadPage(job.JobId, ImportPhase.Execute, second.NextPart, second.NextLine)!;
        var fromDeletedPart = pager.ReadPage(job.JobId, ImportPhase.Execute, 3, 1)!;
        var pastTheEnd = pager.ReadPage(job.JobId, ImportPhase.Execute, 100, 0)!;
        static string Text(ImportReportPage page) => Gunzip(Convert.ToBase64String(page.GzippedLines));

        Assert.Multiple(() =>
        {
            Assert.That(Text(first), Is.EqualTo("2,created,,\n3,created,,\n"));
            Assert.That((first.Rows, first.NextPart, first.NextLine, first.Done), Is.EqualTo((2, 2L, 2, false)));
            Assert.That(Text(second), Is.EqualTo("4,updated,,\n5,updated,,\n"));
            Assert.That((second.NextPart, second.NextLine, second.Done), Is.EqualTo((4L, 2, false)));
            Assert.That(Text(third), Is.EqualTo("6,created,,\n7,created,,\n"));
            Assert.That(third.Done, Is.False, "the page filled up: the next one is empty and ends the report");
            Assert.That(pager.ReadPage(job.JobId, ImportPhase.Execute, third.NextPart, third.NextLine)!.Done, Is.True);
            Assert.That(Text(fromDeletedPart), Is.EqualTo("4,updated,,\n5,updated,,\n"));
            Assert.That((pastTheEnd.Rows, pastTheEnd.Done), Is.EqualTo((0, true)));
            Assert.That(pager.ReadPage(job.JobId, ImportPhase.Analyze, 0, 0), Is.Null);
            Assert.That(pager.HasReport(job.JobId, ImportPhase.Execute), Is.True);
            Assert.That(pager.HasReport(job.JobId, ImportPhase.Analyze), Is.False);
        });
    }

    private sealed class NoUserNames : IImportUserNameProvider
    {
        public IReadOnlyDictionary<int, string> GetUserNames(IReadOnlyCollection<int> userIds) => new Dictionary<int, string>();
    }
}
