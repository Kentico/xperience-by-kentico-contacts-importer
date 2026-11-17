using Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.UIPages;

/// <summary>
/// Page command rules for recipient lists, consent and the consent confirmation (attestation): what
/// <c>LOAD_CONFIG</c> offers, what <c>SAVE_CONFIG</c> and <c>SAVE_OPTIONS</c> accept and store, and when
/// <c>CONFIRM_IMPORT</c> is refused. These imports change subscription and consent state of real people, so the
/// server decides every rule again, whatever the client sent.
/// </summary>
internal class ImportConsentCommandTests
{
    private const int UserId = 53;
    private const int ListId = FakeChoiceProvider.NewsletterListId;
    private const int ConsentId = FakeChoiceProvider.SignupConsentId;

    private TempStore temp = null!;
    private MutableTimeProvider time = null!;
    private FakeContactFieldSchemaProvider schema = null!;
    private FakeChoiceProvider choices = null!;
    private FakeUserNames users = null!;
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
        choices = new FakeChoiceProvider();
        users = new FakeUserNames();
        var ingest = new ChunkIngestService(temp.Store, schema, temp.Options, time);
        var cleanup = new ImportJobCleanupService(temp.Store, temp.Options, time, NullLogger<ImportJobCleanupService>.Instance);
        var processor = new ImportProcessor(temp.Store, schema, new FakeContactRepository(), cleanup, new FakeCacheInvalidator(), temp.Options, time, NullLogger<ImportProcessor>.Instance);
        service = new ImportCommandService(
            temp.Store,
            ingest,
            schema,
            cleanup,
            users,
            processor,
            new ImportReportPager(temp.Store),
            new ImportReportReader(temp.Store),
            new ImportProcessGate(),
            new ResourceImportTexts(),
            choices,
            choices,
            temp.Options,
            time);

        jobId = Guid.NewGuid();
        id = jobId.ToString("D");
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    private static SaveConfigRequest Config(string? agreed = null, string? date = null, params (string Source, string Target)[] mapping)
    {
        if (mapping.Length == 0)
        {
            mapping = [("Email", "ContactEmail"), ("First Name", "ContactFirstName")];
        }

        return new SaveConfigRequest
        {
            File = new ImportFileDto { Name = "contacts.csv", Size = 1234, Fingerprint = "0a1b2c3d" },
            KeyField = "ContactEmail",
            Mapping = [.. mapping.Select(m => new ImportMappingDto { Source = m.Source, Target = m.Target })],
            DateFormat = "iso",
            ConsentMapping = agreed is null && date is null ? null : new ImportConsentMappingDto { Agreed = agreed, Date = date },
        };
    }

    /// <summary>Uploads 25 rows through the commands: the job ends Uploaded.</summary>
    private void Upload(string? agreed = null, string? date = null)
    {
        Assert.That(service.SaveConfig(id, Config(agreed, date), UserId).Code, Is.EqualTo(ImportCommandCodes.OK));

        bool hasConsent = agreed is not null || date is not null;
        string[] records = [.. Enumerable.Range(2, 25).Select(r => hasConsent
            ? $$$"""{"r":{{{r}}},"k":"user{{{r}}}@example.com","v":{"ContactEmail":"user{{{r}}}@example.com"},"c":{"a":true}}"""
            : TestData.Record(r, $"user{r}@example.com"))];
        var upload = service.UploadChunk(id, TestData.Payload(jobId, 0, records));
        Assert.That(upload.Code, Is.EqualTo(ImportCommandCodes.STORED), upload.Message);
        var profile = new ImportProfileDto { RowsRead = 25, RowsValid = 25, ChunkCount = 1 };
        Assert.That(service.CompleteUpload(id, new CompleteUploadRequest { Profile = profile }).Code, Is.EqualTo(ImportCommandCodes.OK));
    }

    private static ImportOptionsDto Options(
        int? recipientListId = null,
        string? unsubscribed = "keep",
        string? bouncedAddress = "addAndReport",
        string? consent = "none",
        int? consentId = null,
        string? revokedConsent = "keep") => new()
        {
            NewContacts = "create",
            ExistingContacts = "update",
            EmptyValues = "ignore",
            MultipleMatches = "updateNewest",
            InvalidEmail = "skipRow",
            SourceStamp = "createdOnly",
            RecipientListId = recipientListId,
            Unsubscribed = unsubscribed,
            BouncedAddress = bouncedAddress,
            Consent = consent,
            ConsentId = consentId,
            RevokedConsent = revokedConsent,
        };

    private ImportOptions StoredOptions() => temp.Store.ReadJob(jobId)!.Options;

    private ImportJobState State() => temp.Store.ReadStatus(jobId)!.State;

    /// <summary>
    /// Uploads, analyzes with the default options, then stores <paramref name="options"/> without the reset
    /// <c>SAVE_OPTIONS</c> does: an Analyzed job with these options, as after the analysis of such a job.
    /// </summary>
    private void Analyzed(ImportOptions options)
    {
        Upload();
        string? token = null;
        for (int i = 0; i < 100; i++)
        {
            var progress = service.ProcessNext(id, new ProcessNextRequest { Phase = "analyze", DriverToken = token }, UserId);
            if (progress.Outcome != "processed")
            {
                break;
            }

            token = progress.DriverToken;
        }

        Assert.That(State(), Is.EqualTo(ImportJobState.Analyzed));
        temp.Store.WriteJob(temp.Store.ReadJob(jobId)! with { Options = options });
    }

    private static ConfirmImportRequest Statement(string? version = ImportAttestationPolicy.StatementVersion) => new() { StatementVersion = version };

    [Test]
    public void GetConfig_Offers_The_Recipient_Lists_And_Consents()
    {
        var config = service.GetConfig();

        Assert.Multiple(() =>
        {
            Assert.That(config.RecipientLists, Is.EqualTo(new[] { new ImportChoiceDto(11, "Newsletter"), new ImportChoiceDto(12, "Product news") }));
            Assert.That(config.Consents, Is.EqualTo(new[] { new ImportChoiceDto(21, "Newsletter signup 2025"), new ImportChoiceDto(22, "Webinar signup") }));
        });
    }

    [Test]
    public void A_New_Job_Has_The_Options_That_Change_No_Subscription_And_No_Consent()
    {
        Upload();

        var job = service.GetJob(id).Job!;

        Assert.Multiple(() =>
        {
            Assert.That(job.Options.RecipientListId, Is.Null);
            Assert.That(job.Options.Unsubscribed, Is.EqualTo("keep"));
            Assert.That(job.Options.BouncedAddress, Is.EqualTo("addAndReport"));
            Assert.That(job.Options.Consent, Is.EqualTo("none"));
            Assert.That(job.Options.ConsentId, Is.Null);
            Assert.That(job.Options.RevokedConsent, Is.EqualTo("keep"));
            Assert.That(job.ConsentMapping, Is.Null);
            Assert.That(job.RecipientListName, Is.Null);
            Assert.That(job.ConsentName, Is.Null);
            Assert.That(job.AttestationRequired, Is.False);
            Assert.That(job.Attestation, Is.Null);
            Assert.That(choices.Queries, Is.Zero, "no list and no consent: nothing to look up");
        });
    }

    [Test]
    public void GetJob_Always_Returns_The_Statement_And_Its_Version_From_The_Server()
    {
        Upload();

        var job = service.GetJob(id).Job!;

        Assert.Multiple(() =>
        {
            Assert.That(job.AttestationStatement, Is.EqualTo(new ResourceImportTexts().Get(ImportTextKeys.ATTESTATION_STATEMENT)));
            Assert.That(job.AttestationStatement, Does.StartWith("I confirm").And.Not.Contain("contactsimporter."));
            Assert.That(job.AttestationVersion, Is.EqualTo(ImportAttestationPolicy.StatementVersion));
        });
    }

    [TestCase("Opt in", null)]
    [TestCase("Opt in", "Opt in date")]
    [TestCase(null, "Opt in date")]
    public void SaveConfig_Stores_The_Consent_Columns(string? agreed, string? date)
    {
        var response = service.SaveConfig(id, Config(agreed, date), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), string.Join(' ', response.Errors));
            Assert.That(response.Job!.ConsentMapping, Is.EqualTo(new ImportConsentMappingDto { Agreed = agreed, Date = date }));
            Assert.That(temp.Store.ReadJob(jobId)!.ConsentMapping, Is.EqualTo(new ImportConsentMapping { Agreed = agreed, Date = date }));
            Assert.That(service.GetJob(id).Job!.ConsentMapping, Is.EqualTo(new ImportConsentMappingDto { Agreed = agreed, Date = date }));
        });
    }

    [Test]
    public void SaveConfig_Treats_Empty_Consent_Column_Names_As_No_Column()
    {
        var request = Config() with { ConsentMapping = new ImportConsentMappingDto { Agreed = "", Date = "" } };

        var response = service.SaveConfig(id, request, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Job!.ConsentMapping, Is.Null);
            Assert.That(temp.Store.ReadJob(jobId)!.ConsentMapping, Is.Null);
        });
    }

    [TestCase("Email", null, Description = "also mapped to a contact field")]
    [TestCase("Opt in", "First Name", Description = "date column also mapped to a contact field")]
    [TestCase("Opt in", "Opt in", Description = "one column for both")]
    public void SaveConfig_Refuses_A_Consent_Column_That_Is_Also_Something_Else(string agreed, string? date)
    {
        var response = service.SaveConfig(id, Config(agreed, date), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_CONFIG));
            Assert.That(response.Errors, Is.Not.Empty);
            Assert.That(temp.Store.JobExists(jobId), Is.False);
        });
    }

    [Test]
    public void SaveConfig_With_Other_Consent_Columns_Needs_A_New_Upload()
    {
        Upload(agreed: "Opt in");

        var response = service.SaveConfig(id, Config(agreed: "Consent"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Uploading"));
            Assert.That(temp.Store.HasChunks(jobId), Is.False, "the uploaded rows carry the values of the old column");
        });
    }

    [Test]
    public void SaveConfig_Without_The_Consent_Column_Turns_Consent_Per_Row_Off_Instead_Of_Widening_It()
    {
        Upload(agreed: "Opt in");
        Assert.That(
            service.SaveOptions(id, Options(consent: "blanketWithColumnOverride", consentId: ConsentId, revokedConsent: "reAgree")).Code,
            Is.EqualTo(ImportCommandCodes.OK));

        var response = service.SaveConfig(id, Config(), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), string.Join(' ', response.Errors));
            Assert.That(StoredOptions().Consent, Is.EqualTo(ConsentMode.None), "never falls back to consent for every row");
            Assert.That(StoredOptions().ConsentId, Is.Null);
            Assert.That(StoredOptions().RevokedConsent, Is.EqualTo(RevokedConsentBehavior.Keep));
            Assert.That(response.Job!.AttestationRequired, Is.False);
        });
    }

    [Test]
    public void SaveOptions_Stores_The_Recipient_List_And_Consent_Options()
    {
        Upload(agreed: "Opt in", date: "Opt in date");

        var response = service.SaveOptions(id, Options(
            recipientListId: ListId,
            unsubscribed: "resubscribe",
            bouncedAddress: "skipAndReport",
            consent: "blanketWithColumnOverride",
            consentId: ConsentId,
            revokedConsent: "reAgree"));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), string.Join(' ', response.Errors));
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions
            {
                RecipientListId = ListId,
                Unsubscribed = UnsubscribedBehavior.Resubscribe,
                BouncedAddress = BouncedAddressBehavior.SkipAndReport,
                Consent = ConsentMode.BlanketWithColumnOverride,
                ConsentId = ConsentId,
                RevokedConsent = RevokedConsentBehavior.ReAgree,
            }));
            Assert.That(response.Job!.Options.RecipientListId, Is.EqualTo(ListId));
            Assert.That(response.Job.Options.Unsubscribed, Is.EqualTo("resubscribe"));
            Assert.That(response.Job.Options.BouncedAddress, Is.EqualTo("skipAndReport"));
            Assert.That(response.Job.Options.Consent, Is.EqualTo("blanketWithColumnOverride"));
            Assert.That(response.Job.Options.ConsentId, Is.EqualTo(ConsentId));
            Assert.That(response.Job.Options.RevokedConsent, Is.EqualTo("reAgree"));
            Assert.That(response.Job.RecipientListName, Is.EqualTo("Newsletter"));
            Assert.That(response.Job.ConsentName, Is.EqualTo("Newsletter signup 2025"));
            Assert.That(response.Job.AttestationRequired, Is.True);
            Assert.That(response.Job.Attestation, Is.Null);
        });
    }

    [TestCase(ListId, "none", null, true)]
    [TestCase(null, "blanket", ConsentId, true)]
    [TestCase(null, "none", null, false)]
    public void The_Consent_Confirmation_Is_Required_For_A_List_Or_A_Consent(int? listId, string consent, int? consentId, bool required)
    {
        Upload();

        var response = service.SaveOptions(id, Options(recipientListId: listId, consent: consent, consentId: consentId));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), string.Join(' ', response.Errors));
            Assert.That(response.Job!.AttestationRequired, Is.EqualTo(required));
            Assert.That(response.Job.AttestationRequired, Is.EqualTo(ImportAttestationPolicy.IsRequired(StoredOptions())));
        });
    }

    [Test]
    public void SaveOptions_Stores_Options_That_Do_Not_Apply_As_The_Ones_That_Change_Nothing()
    {
        Upload();

        // "Subscribe again" without a list, "record a new agreement" and a consent without recording consent.
        var response = service.SaveOptions(id, Options(unsubscribed: "resubscribe", bouncedAddress: "skipAndReport", consentId: ConsentId, revokedConsent: "reAgree"));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions()));
            Assert.That(response.Job!.AttestationRequired, Is.False);
        });
    }

    [Test]
    public void SaveOptions_Refuses_A_Recipient_List_That_Does_Not_Exist()
    {
        Upload();

        var response = service.SaveOptions(id, Options(recipientListId: 999));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_CONFIG));
            Assert.That(response.Errors, Has.Some.Contains("recipient list"));
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions()));
        });
    }

    [Test]
    public void SaveOptions_Refuses_A_Recipient_List_That_Was_Deleted_After_The_Page_Loaded()
    {
        Upload();
        choices.RecipientLists.RemoveAll(list => list.Id == ListId);

        var response = service.SaveOptions(id, Options(recipientListId: ListId));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_CONFIG));
            Assert.That(StoredOptions().RecipientListId, Is.Null);
        });
    }

    [TestCase("blanket", null, Description = "no consent chosen")]
    [TestCase("blanket", 999, Description = "consent does not exist")]
    [TestCase("blanketWithColumnOverride", 999)]
    public void SaveOptions_Refuses_Consent_Without_An_Existing_Consent(string consent, int? consentId)
    {
        Upload(agreed: "Opt in");

        var response = service.SaveOptions(id, Options(consent: consent, consentId: consentId));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_CONFIG));
            Assert.That(response.Errors, Is.Not.Empty);
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions()));
        });
    }

    [TestCase(null, null, Description = "no consent column at all")]
    [TestCase(null, "Opt in date", Description = "only a date column: nothing says which rows agreed")]
    public void SaveOptions_Refuses_Consent_Per_Row_Without_A_Consent_Given_Column(string? agreed, string? date)
    {
        Upload(agreed, date);

        var response = service.SaveOptions(id, Options(consent: "blanketWithColumnOverride", consentId: ConsentId));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_CONFIG));
            Assert.That(response.Errors, Is.Not.Empty);
            Assert.That(StoredOptions().Consent, Is.EqualTo(ConsentMode.None));
        });
    }

    [Test]
    public void SaveOptions_Accepts_Consent_For_Every_Row_Without_A_Consent_Column()
    {
        Upload();

        var response = service.SaveOptions(id, Options(consent: "blanket", consentId: ConsentId));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), string.Join(' ', response.Errors));
            Assert.That(StoredOptions().Consent, Is.EqualTo(ConsentMode.Blanket));
            Assert.That(StoredOptions().ConsentId, Is.EqualTo(ConsentId));
        });
    }

    [Test]
    public void SaveOptions_Refuses_Unknown_List_And_Consent_Option_Values()
    {
        Upload(agreed: "Opt in");

        Assert.Multiple(() =>
        {
            // "reportOnly" of the plan is not an option: it had no behavior of its own.
            Assert.That(service.SaveOptions(id, Options(recipientListId: ListId, unsubscribed: "reportOnly")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(recipientListId: ListId, unsubscribed: "Resubscribe")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(recipientListId: ListId, unsubscribed: "1")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(recipientListId: ListId, bouncedAddress: "skip")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(consent: "all", consentId: ConsentId)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(consent: "", consentId: ConsentId)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(consent: "blanket", consentId: ConsentId, revokedConsent: "agree")).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(recipientListId: 0)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(recipientListId: -5)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.SaveOptions(id, Options(consent: "blanket", consentId: 0)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(StoredOptions(), Is.EqualTo(new ImportOptions()));
        });
    }

    [Test]
    public void Confirm_Without_A_List_Or_Consent_Needs_No_Permission_And_Stores_No_Confirmation()
    {
        Analyzed(new ImportOptions());

        var response = service.Confirm(id, null, UserId, canConfirmConsent: false);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(State(), Is.EqualTo(ImportJobState.Confirmed));
            Assert.That(temp.Store.ReadJob(jobId)!.Attestation, Is.Null);
            Assert.That(response.Job!.Attestation, Is.Null);
        });
    }

    private static IEnumerable<ImportOptions> OptionsThatAffectConsent() =>
    [
        new ImportOptions { RecipientListId = ListId },
        new ImportOptions { RecipientListId = ListId, Unsubscribed = UnsubscribedBehavior.Resubscribe },
        new ImportOptions { Consent = ConsentMode.Blanket, ConsentId = ConsentId },
        new ImportOptions { Consent = ConsentMode.Blanket, ConsentId = ConsentId, RevokedConsent = RevokedConsentBehavior.ReAgree },
        new ImportOptions { RecipientListId = ListId, Consent = ConsentMode.Blanket, ConsentId = ConsentId },
    ];

    [TestCaseSource(nameof(OptionsThatAffectConsent))]
    public void Confirm_Is_Refused_Without_The_Confirm_Consent_Permission(ImportOptions options)
    {
        Analyzed(options);

        // The client sends everything a permitted user would send.
        var response = service.Confirm(id, Statement(), UserId, canConfirmConsent: false);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.CONSENT_PERMISSION_REQUIRED));
            Assert.That(response.Message, Does.Contain("Confirm consent for imported contacts"));
            Assert.That(State(), Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(temp.Store.ReadJob(jobId)!.Attestation, Is.Null);
            Assert.That(response.Job!.AttestationRequired, Is.True);
        });
    }

    [Test]
    public void Confirm_Is_Refused_Without_A_Signed_In_User()
    {
        Analyzed(new ImportOptions { RecipientListId = ListId });

        var response = service.Confirm(id, Statement(), userId: 0, canConfirmConsent: true);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.CONSENT_PERMISSION_REQUIRED));
            Assert.That(State(), Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(temp.Store.ReadJob(jobId)!.Attestation, Is.Null);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("0")]
    [TestCase("2")]
    [TestCase(" 1")]
    public void Confirm_Is_Refused_Without_The_Current_Statement_Version(string? version)
    {
        Analyzed(new ImportOptions { RecipientListId = ListId });

        var response = service.Confirm(id, version is null ? null : Statement(version), UserId, canConfirmConsent: true);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.ATTESTATION_REQUIRED));
            Assert.That(response.Message, Is.Not.Empty);
            Assert.That(State(), Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(temp.Store.ReadJob(jobId)!.Attestation, Is.Null);
            Assert.That(response.Job!.AttestationVersion, Is.EqualTo(ImportAttestationPolicy.StatementVersion), "the client gets the current statement to show");
        });
    }

    [TestCaseSource(nameof(OptionsThatAffectConsent))]
    public void Confirm_With_Permission_And_Statement_Stores_Who_Confirmed_And_When(ImportOptions options)
    {
        Analyzed(options);
        users.Names[UserId] = "marketer";
        time.Advance(TimeSpan.FromMinutes(3));

        var response = service.Confirm(id, Statement(), UserId, canConfirmConsent: true);

        var expected = new ImportAttestation { UserId = UserId, Utc = TestData.Now.AddMinutes(3), StatementVersion = ImportAttestationPolicy.StatementVersion };
        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), response.Message);
            Assert.That(State(), Is.EqualTo(ImportJobState.Confirmed));
            Assert.That(temp.Store.ReadJob(jobId)!.Attestation, Is.EqualTo(expected));
            Assert.That(response.Job!.Attestation, Is.EqualTo(new ImportAttestationDto("marketer", expected.Utc, expected.StatementVersion)));
            Assert.That(service.GetJob(id).Job!.Attestation, Is.EqualTo(new ImportAttestationDto("marketer", expected.Utc, expected.StatementVersion)));
        });
    }

    [Test]
    public void The_Stored_Confirmation_Of_A_Deleted_User_Has_No_Name()
    {
        Analyzed(new ImportOptions { RecipientListId = ListId });

        var response = service.Confirm(id, Statement(), UserId, canConfirmConsent: true);

        Assert.That(response.Job!.Attestation!.UserName, Is.Empty);
    }

    [Test]
    public void Changing_The_Options_After_The_Confirmation_Asks_For_It_Again()
    {
        Analyzed(new ImportOptions { RecipientListId = ListId });
        Assert.That(service.Confirm(id, Statement(), UserId, canConfirmConsent: true).Code, Is.EqualTo(ImportCommandCodes.OK));

        var changed = service.SaveOptions(id, Options(recipientListId: ListId, unsubscribed: "resubscribe"));

        Assert.Multiple(() =>
        {
            Assert.That(changed.Code, Is.EqualTo(ImportCommandCodes.OK), string.Join(' ', changed.Errors));
            Assert.That(changed.Status!.State, Is.EqualTo("Uploaded"), "analyze again");
            Assert.That(temp.Store.ReadJob(jobId)!.Attestation, Is.Null);
            Assert.That(changed.Job!.Attestation, Is.Null);
            Assert.That(changed.Job.AttestationRequired, Is.True);
        });
    }

    [Test]
    public void ListImports_Shows_The_Recipient_List_And_Consent_Of_Each_Import()
    {
        Upload();
        Assert.That(service.SaveOptions(id, Options(recipientListId: ListId, consent: "blanket", consentId: ConsentId)).Code, Is.EqualTo(ImportCommandCodes.OK));
        var plainId = Guid.NewGuid();
        Assert.That(service.SaveConfig(plainId.ToString("D"), Config(), UserId).Code, Is.EqualTo(ImportCommandCodes.OK));

        var imports = service.ListImports().Imports.ToDictionary(i => i.JobId);
        choices.RecipientLists.Clear();
        var afterDelete = service.ListImports().Imports.Single(i => i.JobId == id);

        Assert.Multiple(() =>
        {
            Assert.That(imports[id].RecipientListName, Is.EqualTo("Newsletter"));
            Assert.That(imports[id].ConsentName, Is.EqualTo("Newsletter signup 2025"));
            Assert.That(imports[plainId.ToString("D")].RecipientListName, Is.Null);
            Assert.That(imports[plainId.ToString("D")].ConsentName, Is.Null);
            Assert.That(afterDelete.RecipientListName, Is.Null, "a deleted list has no name");
        });
    }

    private sealed class FakeUserNames : IImportUserNameProvider
    {
        public Dictionary<int, string> Names { get; } = [];

        public IReadOnlyDictionary<int, string> GetUserNames(IReadOnlyCollection<int> userIds) =>
            Names.Where(n => userIds.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value);
    }
}
