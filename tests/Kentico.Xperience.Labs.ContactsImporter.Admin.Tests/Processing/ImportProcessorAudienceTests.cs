using Microsoft.Extensions.Logging.Abstractions;

using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

/// <summary>
/// Recipient list, consent, attestation and the import event in the processor, over an in-memory contact table.
/// </summary>
public class ImportProcessorAudienceTests
{
    private const int ListId = 7;
    private const int ConsentId = 9;
    private const int UserId = 77;

    private static readonly ImportOptions listAndConsent = new() { RecipientListId = ListId, BouncedAddress = BouncedAddressBehavior.AddAndReport, Consent = ConsentMode.Blanket, ConsentId = ConsentId };

    private sealed class Fixture : IDisposable
    {
        public Fixture(Action<ContactsImporterOptions>? configure = null, bool withResolver = true)
        {
            Temp = new TempStore(options =>
            {
                options.BatchSize = 2;
                configure?.Invoke(options);
            });
            Files = new HookedImportFileSystem(new PhysicalImportFileSystem());
            Store = Temp.NewStore(Files);
            Cleanup = new ImportJobCleanupService(Store, Temp.Options, Time, NullLogger<ImportJobCleanupService>.Instance, Events);
            Processor = new ImportProcessor(Store, Schema, Repository, Cleanup, new FakeCacheInvalidator(), Temp.Options, Time, new ListLogger<ImportProcessor>(Log), withResolver ? Resolver : null, Events);
        }

        public TempStore Temp { get; }

        public HookedImportFileSystem Files { get; }

        public ImportJobStore Store { get; }

        public MutableTimeProvider Time { get; } = new(TestData.Now);

        public FakeContactFieldSchemaProvider Schema { get; } = new(Fields);

        public FakeContactRepository Repository { get; } = new();

        public FakeAudienceResolver Resolver { get; } = new();

        public FakeEventPublisher Events { get; } = new();

        public ImportJobCleanupService Cleanup { get; }

        public List<string> Log { get; } = [];

        public ImportProcessor Processor { get; }

        public DateTime LocalNow => Time.GetLocalNow().DateTime;

        public ImportAttestation Attestation(int userId = UserId, string version = ImportAttestationPolicy.StatementVersion) =>
            new() { UserId = userId, Utc = Time.UtcNow, StatementVersion = version };

        /// <summary>
        /// Stores an Uploaded job whose rows are <paramref name="emails"/> with first name <c>Name {row}</c>;
        /// <paramref name="consents"/> gives the <c>c</c> JSON of each row when consent columns are mapped.
        /// </summary>
        public Guid Upload(string[] emails, ImportOptions options, string[]? consents = null)
        {
            var job = TestData.Job(null, Email, Email, FirstName) with
            {
                Options = options,
                ConsentMapping = consents is null ? null : new ImportConsentMapping { Agreed = "consent", Date = "consent date" },
            };
            Store.CreateJob(job);

            string[] lines = [.. emails.Select((email, i) =>
                "{\"r\":" + (i + 1) + ",\"k\":\"" + email + "\",\"v\":{\"ContactEmail\":\"" + email + "\",\"ContactFirstName\":\"Name " + (i + 1) + "\"}"
                + (consents is null ? "" : ",\"c\":" + consents[i]) + "}")];
            byte[] gzipped = TestData.Gzip(string.Join('\n', lines));
            var chunk = new ImportChunkInfo { Index = 0, Sha256 = TestData.Sha256Hex(gzipped), Rows = lines.Length, FirstRow = 1, LastRow = lines.Length };
            Store.WriteChunk(job.JobId, chunk, gzipped);
            Store.WriteStatus(job.JobId, new ImportJobStatus
            {
                State = ImportJobState.Uploaded,
                Chunks = [chunk],
                Profile = new ImportProfile { RowsRead = lines.Length, RowsValid = lines.Length, ChunkCount = 1 },
                UpdatedUtc = TestData.Now,
            });

            return job.JobId;
        }

        public ImportProgress Run(Guid jobId, ImportPhase phase, int? userId = UserId)
        {
            string? token = null;
            ImportProgress progress;
            do
            {
                progress = Processor.ProcessNext(jobId, phase, token, false, default, userId);
                token = progress.DriverToken;
            }
            while (progress.Outcome == ImportProcessOutcome.Processed);

            return progress;
        }

        public ImportProgress AnalyzeAndConfirm(Guid jobId)
        {
            var analysis = Run(jobId, ImportPhase.Analyze);
            Assert.That(analysis.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), analysis.Error?.ToString());
            Assert.That(Processor.Confirm(jobId, Attestation()).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));

            return analysis;
        }

        /// <summary>Adds an existing contact (email, first name) and returns its ID.</summary>
        public int Seed(string email)
        {
            var contact = new FakeContact(1000 + Repository.Contacts.Count, [email, "Old"]) { LastModified = TestData.Now.AddYears(-1) };
            Repository.Contacts.Add(contact);

            return contact.Id;
        }

        public IReadOnlyDictionary<string, long> Aggregates(Guid jobId) => Store.ReadStatus(jobId)!.Aggregates;

        public string[] Report(Guid jobId, ImportPhase phase)
        {
            using var buffer = new MemoryStream();
            new ImportReportReader(Store).CopyTo(jobId, phase, buffer);
            buffer.Position = 0;
            using var reader = new StreamReader(new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionMode.Decompress));

            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        public void Dispose() => Temp.Dispose();
    }

    private static Dictionary<string, long> AudienceCounts(IReadOnlyDictionary<string, long> aggregates) =>
        aggregates.Where(a => a.Key.StartsWith("list", StringComparison.Ordinal) || a.Key.StartsWith("consent", StringComparison.Ordinal) || a.Key.StartsWith("bounced", StringComparison.Ordinal))
            .ToDictionary(a => a.Key, a => a.Value);

    [Test]
    public void Analysis_Counts_What_The_Import_Would_Do_And_Writes_Nothing()
    {
        using var fixture = new Fixture();
        int subscribed = fixture.Seed("subscribed@x.test");
        int unsubscribed = fixture.Seed("unsubscribed@x.test");
        int revoked = fixture.Seed("revoked@x.test");
        fixture.Repository.Members.AddRange([subscribed, unsubscribed]);
        fixture.Repository.Subscriptions.Add(new FakeSubscription(subscribed, true, fixture.LocalNow.AddDays(-9), Guid.NewGuid()));
        fixture.Repository.Subscriptions.Add(new FakeSubscription(unsubscribed, true, fixture.LocalNow.AddDays(-9), Guid.NewGuid()));
        fixture.Repository.Subscriptions.Add(new FakeSubscription(unsubscribed, false, fixture.LocalNow.AddDays(-2), Guid.NewGuid()));
        fixture.Repository.Agreements.Add(new FakeAgreement(revoked, true, fixture.LocalNow.AddDays(-2), Guid.NewGuid()));
        fixture.Repository.Agreements.Add(new FakeAgreement(subscribed, false, fixture.LocalNow.AddDays(-2), Guid.NewGuid()));
        fixture.Repository.Bounces["new@x.test"] = (true, 0);
        var jobId = fixture.Upload(["new@x.test", "subscribed@x.test", "unsubscribed@x.test", "revoked@x.test", "new2@x.test"], listAndConsent);

        var progress = fixture.Run(jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(AudienceCounts(fixture.Aggregates(jobId)), Is.EquivalentTo(new Dictionary<string, long>
            {
                [ImportAggregateKeys.ListAdded] = 3,
                [ImportAggregateKeys.ListAlreadySubscribed] = 1,
                [ImportAggregateKeys.ListKeptUnsubscribed] = 1,
                [ImportAggregateKeys.BouncedHard] = 1,
                [ImportAggregateKeys.ConsentRecorded] = 3,
                [ImportAggregateKeys.ConsentAlreadyAgreed] = 1,
                [ImportAggregateKeys.ConsentKeptRevoked] = 1,
            }));
            Assert.That(progress.Aggregates.ListAdded, Is.EqualTo(3));
            Assert.That(progress.Aggregates.ConsentKeptRevoked, Is.EqualTo(1));
            Assert.That(fixture.Repository.Writes, Is.Zero);
            Assert.That(fixture.Repository.Members, Has.Count.EqualTo(2));
            Assert.That(fixture.Repository.Subscriptions, Has.Count.EqualTo(3));
            Assert.That(fixture.Repository.Agreements, Has.Count.EqualTo(2));
            Assert.That(fixture.Report(jobId, ImportPhase.Analyze), Is.EqualTo(new[]
            {
                "row,outcome,reason,field,list,consent",
                "1,created,,,added;bouncedHard,recorded",
                "2,updated,,,alreadySubscribed,alreadyAgreed",
                "3,updated,,,keptUnsubscribed,recorded",
                "4,updated,,,added,keptRevoked",
                "5,created,,,added,recorded",
            }));
        });
    }

    [Test]
    public void Import_Writes_What_The_Analysis_Counted()
    {
        using var fixture = new Fixture();
        int unsubscribed = fixture.Seed("unsubscribed@x.test");
        int revoked = fixture.Seed("revoked@x.test");
        fixture.Repository.Members.Add(unsubscribed);
        fixture.Repository.Subscriptions.Add(new FakeSubscription(unsubscribed, false, fixture.LocalNow.AddDays(-2), Guid.NewGuid()));
        fixture.Repository.Agreements.Add(new FakeAgreement(revoked, true, fixture.LocalNow.AddDays(-2), Guid.NewGuid()));
        var options = listAndConsent with { Unsubscribed = UnsubscribedBehavior.Resubscribe, RevokedConsent = RevokedConsentBehavior.ReAgree };
        var jobId = fixture.Upload(["new@x.test", "unsubscribed@x.test", "revoked@x.test"], options);

        var analysis = fixture.AnalyzeAndConfirm(jobId);
        var progress = fixture.Run(jobId, ImportPhase.Execute);

        int created = fixture.Repository.Contacts.Single(c => (string)c.Values[0]! == "new@x.test").Id;
        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), progress.Error?.ToString());
            Assert.That(AudienceCounts(fixture.Aggregates(jobId)), Is.EquivalentTo(AudienceCounts(fixture.Store.ReadStatus(jobId)!.Analysis!.Aggregates)));
            Assert.That(progress.Aggregates.ListAdded, Is.EqualTo(2));
            Assert.That(progress.Aggregates.ListResubscribed, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ConsentRecorded, Is.EqualTo(2));
            Assert.That(progress.Aggregates.ConsentReAgreed, Is.EqualTo(1));
            Assert.That(analysis.Aggregates.ListResubscribed, Is.EqualTo(1));
            Assert.That(fixture.Repository.Members, Is.EquivalentTo(new[] { unsubscribed, created, revoked }));
            Assert.That(fixture.Repository.Subscriptions.Where(s => s.Approved).Select(s => s.ContactId), Is.EquivalentTo(new[] { unsubscribed, created, revoked }));
            Assert.That(fixture.Repository.Subscriptions.Where(s => s.Approved).Select(s => s.Date), Is.All.EqualTo(fixture.LocalNow));
            Assert.That(fixture.Repository.Agreements.Where(a => !a.Revoked).Select(a => a.ContactId), Is.EquivalentTo(new[] { unsubscribed, created, revoked }));
            Assert.That(fixture.Repository.Agreements.Where(a => !a.Revoked).Select(a => a.Hash), Is.All.EqualTo(ConsentHash));
            Assert.That(fixture.Report(jobId, ImportPhase.Execute), Is.EqualTo(new[]
            {
                "row,outcome,reason,field,list,consent",
                "1,created,,,added,recorded",
                "2,updated,,,resubscribed,recorded",
                "3,updated,,,added,reAgreed",
            }));
        });
    }

    [Test]
    public void Contacts_That_Are_Unchanged_Or_Not_Updated_Are_Still_Added_To_The_List()
    {
        using var fixture = new Fixture();
        int existing = fixture.Seed("existing@x.test");
        var jobId = fixture.Upload(["existing@x.test"], new ImportOptions { RecipientListId = ListId, ExistingContacts = ExistingContactsBehavior.Skip });

        fixture.AnalyzeAndConfirm(jobId);
        var progress = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.SkippedExisting, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ListAdded, Is.EqualTo(1));
            Assert.That(fixture.Repository.Contacts.Single().Values[1], Is.EqualTo("Old"), "The contact itself is not updated.");
            Assert.That(fixture.Repository.Members, Is.EqualTo(new[] { existing }));
        });
    }

    [Test]
    public void Batch_Processed_Again_After_A_Lost_Checkpoint_Inserts_Nothing_Twice_And_Counts_The_Same()
    {
        using var fixture = new Fixture(options => options.BatchSize = 10);
        int unsubscribed = fixture.Seed("unsubscribed@x.test");
        int revoked = fixture.Seed("revoked@x.test");
        fixture.Repository.Members.Add(unsubscribed);
        fixture.Repository.Subscriptions.Add(new FakeSubscription(unsubscribed, false, fixture.LocalNow.AddDays(-2), Guid.NewGuid()));
        fixture.Repository.Agreements.Add(new FakeAgreement(revoked, true, fixture.LocalNow.AddDays(-2), Guid.NewGuid()));
        var options = listAndConsent with { Unsubscribed = UnsubscribedBehavior.Resubscribe, RevokedConsent = RevokedConsentBehavior.ReAgree };
        var jobId = fixture.Upload(["new@x.test", "unsubscribed@x.test", "revoked@x.test"], options);
        fixture.AnalyzeAndConfirm(jobId);

        // The batch is written, then storing the checkpoint fails: the application stopped between commit and checkpoint.
        bool storageDown = false;
        fixture.Repository.OnAudienceWrite = () => storageDown = true;
        fixture.Files.BeforeReplace = name =>
        {
            if (storageDown && name == "status.json")
            {
                throw new IOException("storage unavailable");
            }
        };

        var first = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        storageDown = false;
        fixture.Repository.OnAudienceWrite = null;
        int members = fixture.Repository.Members.Count;
        int subscriptions = fixture.Repository.Subscriptions.Count;
        int agreements = fixture.Repository.Agreements.Count;
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        var second = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, first.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.TransientError));
            Assert.That(first.RowsProcessed, Is.Zero, "The checkpoint of the written batch was lost.");
            Assert.That((members, subscriptions, agreements), Is.EqualTo((3, 4, 4)));
            Assert.That(second.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), second.Error?.ToString());
            Assert.That(fixture.Repository.Writes, Is.EqualTo(2));
            Assert.That(fixture.Repository.Members, Has.Count.EqualTo(members));
            Assert.That(fixture.Repository.Subscriptions, Has.Count.EqualTo(subscriptions));
            Assert.That(fixture.Repository.Agreements, Has.Count.EqualTo(agreements));
            Assert.That(fixture.Repository.Members, Is.Unique);
            Assert.That(AudienceCounts(fixture.Aggregates(jobId)), Is.EquivalentTo(new Dictionary<string, long>
            {
                [ImportAggregateKeys.ListAdded] = 2,
                [ImportAggregateKeys.ListResubscribed] = 1,
                [ImportAggregateKeys.ConsentRecorded] = 2,
                [ImportAggregateKeys.ConsentReAgreed] = 1,
            }), "The second run recognizes what the first one wrote and reports the same outcome.");
            Assert.That(fixture.Report(jobId, ImportPhase.Execute).Skip(1), Is.EqualTo(new[]
            {
                "1,unchanged,,,added,recorded",
                "2,unchanged,,,resubscribed,recorded",
                "3,unchanged,,,added,reAgreed",
            }), "The contacts were written by the first run; what the import did for the list and the consent is reported as then.");
        });
    }

    [Test]
    public void Same_Contact_In_A_Later_Batch_Is_Not_Written_Or_Counted_As_Written_Again()
    {
        using var fixture = new Fixture(options => options.BatchSize = 1);
        var jobId = fixture.Upload(["a@x.test", "b@x.test", "a@x.test"], listAndConsent);
        fixture.AnalyzeAndConfirm(jobId);

        var progress = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.ListAdded, Is.EqualTo(2));
            Assert.That(progress.Aggregates.ListAlreadySubscribed, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ConsentRecorded, Is.EqualTo(2));
            Assert.That(progress.Aggregates.ConsentAlreadyAgreed, Is.EqualTo(1));
            Assert.That(fixture.Repository.Members, Has.Count.EqualTo(2));
            Assert.That(fixture.Repository.Subscriptions, Has.Count.EqualTo(2));
            Assert.That(fixture.Repository.Agreements, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void Failure_While_List_And_Consent_Are_Written_Fails_The_Job_Without_Counting_The_Batch()
    {
        using var fixture = new Fixture(options => options.BatchSize = 10);
        var jobId = fixture.Upload(["secret.person@x.test"], listAndConsent);
        fixture.AnalyzeAndConfirm(jobId);
        fixture.Repository.OnAudienceWrite = () => throw new InvalidOperationException("Cannot insert secret.person@x.test");

        var progress = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.Error, Is.EqualTo(ImportProcessor.UnexpectedError));
            Assert.That(AudienceCounts(fixture.Aggregates(jobId)), Is.Empty);
            Assert.That(fixture.Log, Has.None.Contains("secret.person"));
            Assert.That(fixture.Events.Events[^1].Outcome, Is.EqualTo(ContactImportStatus.Failed));
            Assert.That(fixture.Events.Events[^1].IsDryRun, Is.False);
        });
    }

    [Test]
    public void Consent_From_Columns_Uses_The_Row_Columns()
    {
        using var fixture = new Fixture(options => options.BatchSize = 10);
        var options = new ImportOptions { Consent = ConsentMode.BlanketWithColumnOverride, ConsentId = ConsentId };
        var jobId = fixture.Upload(
            ["yes@x.test", "dated@x.test", "no@x.test", "future@x.test"],
            options,
            ["""{"a":true}""", """{"a":true,"d":"2025-03-01"}""", """{"a":false,"d":"2025-03-01"}""", """{"a":true,"d":"2999-01-01"}"""]);

        fixture.AnalyzeAndConfirm(jobId);
        var progress = fixture.Run(jobId, ImportPhase.Execute);

        var agreements = fixture.Repository.Agreements.ToDictionary(a => (string)fixture.Repository.Contacts.Single(c => c.Id == a.ContactId).Values[0]!, a => a.Time);
        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), progress.Error?.ToString());
            Assert.That(agreements, Is.EquivalentTo(new Dictionary<string, DateTime>
            {
                ["yes@x.test"] = fixture.LocalNow,
                ["dated@x.test"] = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Unspecified),
            }));
            Assert.That(progress.Aggregates.ConsentRecorded, Is.EqualTo(2));
            Assert.That(progress.Aggregates.ConsentNotGiven, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ConsentSkippedFutureDate, Is.EqualTo(1));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(4), "The rows are imported whatever their consent columns say.");
            Assert.That(fixture.Report(jobId, ImportPhase.Execute), Is.EqualTo(new[]
            {
                "row,outcome,reason,field,consent",
                "1,created,,,recorded",
                "2,created,,,recorded",
                "3,created,,,notGiven",
                "4,created,,,skippedFutureDate",
            }));
        });
    }

    // Attestation.

    [Test]
    public void Confirm_Needs_An_Attestation_Of_The_Current_Statement_By_A_Known_User()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(["a@x.test"], new ImportOptions { RecipientListId = ListId });
        fixture.Run(jobId, ImportPhase.Analyze);

        var none = fixture.Processor.Confirm(jobId);
        var oldStatement = fixture.Processor.Confirm(jobId, fixture.Attestation(version: "0"));
        var unknownUser = fixture.Processor.Confirm(jobId, fixture.Attestation(userId: 0));
        var stateAfterRefusals = fixture.Store.ReadStatus(jobId)!.State;
        var attestationAfterRefusals = fixture.Store.ReadJob(jobId)!.Attestation;
        var attestation = fixture.Attestation();
        var confirmed = fixture.Processor.Confirm(jobId, attestation);
        var again = fixture.Processor.Confirm(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(none.Outcome, Is.EqualTo(ImportConfirmOutcome.AttestationRequired));
            Assert.That(oldStatement.Outcome, Is.EqualTo(ImportConfirmOutcome.AttestationRequired));
            Assert.That(unknownUser.Outcome, Is.EqualTo(ImportConfirmOutcome.AttestationRequired));
            Assert.That(stateAfterRefusals, Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(attestationAfterRefusals, Is.Null);
            Assert.That(confirmed.Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
            Assert.That(confirmed.Status!.State, Is.EqualTo(ImportJobState.Confirmed));
            Assert.That(fixture.Store.ReadJob(jobId)!.Attestation, Is.EqualTo(attestation));
            Assert.That(again.Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed), "Confirming a confirmed job changes nothing.");
            Assert.That(fixture.Store.ReadJob(jobId)!.Attestation, Is.EqualTo(attestation));
            Assert.That(fixture.Log.Count(l => l.Contains("CONTACT_IMPORT_ATTESTATION")), Is.EqualTo(1));
            Assert.That(fixture.Log.Single(l => l.Contains("CONTACT_IMPORT_ATTESTATION")), Does.Contain($"user {UserId} confirmed").And.Contain("statement version 1").And.Contain($"Recipient list {ListId}"));
        });
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public void Attestation_Is_Required_For_A_Recipient_List_Or_Consent_Only(bool list, bool consent, bool neither)
    {
        var options = new ImportOptions
        {
            RecipientListId = list ? ListId : null,
            Consent = consent ? ConsentMode.Blanket : ConsentMode.None,
            ConsentId = consent ? ConsentId : null,
            Unsubscribed = UnsubscribedBehavior.Resubscribe,
            RevokedConsent = RevokedConsentBehavior.ReAgree,
        };

        Assert.Multiple(() =>
        {
            Assert.That(ImportAttestationPolicy.IsRequired(options), Is.EqualTo(!neither));
            Assert.That(ImportAttestationPolicy.IsSatisfied(options, null), Is.EqualTo(neither));
            Assert.That(ImportAttestationPolicy.IsSatisfied(options, new ImportAttestation { UserId = 5, Utc = TestData.Now, StatementVersion = ImportAttestationPolicy.StatementVersion }), Is.True);
        });
    }

    [Test]
    public void Job_Without_List_And_Consent_Is_Confirmed_Without_An_Attestation()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(["a@x.test"], new ImportOptions());
        fixture.Run(jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
            Assert.That(fixture.Run(jobId, ImportPhase.Execute).Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
            Assert.That(fixture.Log, Has.None.Contains("CONTACT_IMPORT_ATTESTATION"));
        });
    }

    [Test]
    public void Attestation_Is_Not_Left_Behind_When_The_Job_Cannot_Be_Confirmed()
    {
        using var fixture = new Fixture(options => options.AnalysisValidHours = 1);
        var jobId = fixture.Upload(["a@x.test"], new ImportOptions { RecipientListId = ListId });
        var notAnalyzed = fixture.Processor.Confirm(jobId, fixture.Attestation());
        fixture.Run(jobId, ImportPhase.Analyze);
        fixture.Time.Advance(TimeSpan.FromHours(2));

        var expired = fixture.Processor.Confirm(jobId, fixture.Attestation());

        Assert.Multiple(() =>
        {
            Assert.That(notAnalyzed.Outcome, Is.EqualTo(ImportConfirmOutcome.InvalidState));
            Assert.That(expired.Outcome, Is.EqualTo(ImportConfirmOutcome.AnalysisExpired));
            Assert.That(fixture.Store.ReadJob(jobId)!.Attestation, Is.Null);
            Assert.That(fixture.Log, Has.None.Contains("CONTACT_IMPORT_ATTESTATION"));
        });
    }

    [TestCase(null)]
    [TestCase("0")]
    public void Import_Does_Not_Start_Without_A_Valid_Attestation_Even_When_The_Job_Is_Confirmed(string? storedVersion)
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(["a@x.test"], listAndConsent);
        fixture.AnalyzeAndConfirm(jobId);

        // The stored configuration loses the attestation, or has one for an older statement.
        var job = fixture.Store.ReadJob(jobId)!;
        fixture.Store.WriteJob(job with { Attestation = storedVersion is null ? null : fixture.Attestation(version: storedVersion) });

        var progress = fixture.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Uploaded), "Nothing was written: the job returns to the options.");
            Assert.That(progress.Error, Is.EqualTo(ImportAttestationPolicy.MissingError));
            Assert.That(fixture.Repository.Writes, Is.Zero);
            Assert.That(fixture.Repository.Members, Is.Empty);
            Assert.That(fixture.Repository.Agreements, Is.Empty);
        });
    }

    // The recipient list and the consent must exist.

    [Test]
    public void Analysis_Does_Not_Start_When_The_List_Or_Consent_Is_Gone()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(["a@x.test"], listAndConsent);
        fixture.Resolver.Error = ImportAudienceResolver.RecipientListMissingError;

        var progress = fixture.Run(jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(progress.Error, Is.EqualTo(ImportAudienceResolver.RecipientListMissingError));
            Assert.That(fixture.Store.HasChunks(jobId), Is.True);
            Assert.That(fixture.Events.Events, Is.Empty, "The job is not finished.");
        });
    }

    [Test]
    public void Import_Fails_When_The_List_Or_Consent_Disappears_While_It_Runs()
    {
        using var fixture = new Fixture(options =>
        {
            options.BatchSize = 1;
            options.ProcessingTimeBudgetSeconds = 1;
        });
        var jobId = fixture.Upload(["a@x.test", "b@x.test"], listAndConsent);
        fixture.AnalyzeAndConfirm(jobId);
        fixture.Repository.OnWrite = _ => fixture.Time.Advance(TimeSpan.FromSeconds(2));

        var first = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        fixture.Resolver.Error = ImportAudienceResolver.ConsentMissingError;
        var second = fixture.Processor.ProcessNext(jobId, ImportPhase.Execute, first.DriverToken, false);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ImportProcessOutcome.Processed));
            Assert.That(second.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(second.State, Is.EqualTo(ImportJobState.Failed));
            Assert.That(second.Error, Is.EqualTo(ImportAudienceResolver.ConsentMissingError));
            Assert.That(fixture.Repository.Members, Has.Count.EqualTo(1), "Only the batch before the consent disappeared.");
        });
    }

    [Test]
    public void Processor_Without_A_Resolver_Refuses_Jobs_With_A_List_Or_Consent_And_Runs_The_Others()
    {
        using var fixture = new Fixture(withResolver: false);
        var withList = fixture.Upload(["a@x.test"], new ImportOptions { RecipientListId = ListId });
        var plain = fixture.Upload(["b@x.test"], new ImportOptions());

        var refused = fixture.Run(withList, ImportPhase.Analyze);
        var analyzed = fixture.Run(plain, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(refused.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(refused.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(refused.Error?.ToString(), Does.Contain("could not be checked"));
            Assert.That(analyzed.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished));
        });
    }

    // Event.

    [Test]
    public void Event_Is_Raised_Once_When_The_Analysis_Finishes_And_Once_When_The_Import_Completes()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(["a@x.test", "b@x.test", "c@x.test"], listAndConsent);
        var statesAtPublish = new List<ImportJobState>();
        var locksAtPublish = new List<int>();
        fixture.Events.OnPublish = () =>
        {
            statesAtPublish.Add(fixture.Store.ReadStatus(jobId)!.State);
            locksAtPublish.Add(fixture.Repository.LockedJobs.Count);
        };

        fixture.Run(jobId, ImportPhase.Analyze, userId: 5);
        fixture.Run(jobId, ImportPhase.Analyze, userId: 5);
        Assert.That(fixture.Processor.Confirm(jobId, fixture.Attestation()).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
        fixture.Run(jobId, ImportPhase.Execute, userId: 6);
        fixture.Run(jobId, ImportPhase.Execute, userId: 6);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Events.Events.Select(e => (e.Outcome, e.IsDryRun, e.UserId)), Is.EqualTo(new[]
            {
                (ContactImportStatus.Analyzed, true, 5),
                (ContactImportStatus.Completed, false, (int?)6),
            }));
            Assert.That(statesAtPublish, Is.EqualTo(new[] { ImportJobState.Analyzed, ImportJobState.Completed }), "The outcome is stored before the event.");
            Assert.That(locksAtPublish, Is.All.Zero, "The job lock is released before the event.");
            Assert.That(fixture.Events.Events[0].Status.Aggregates[ImportAggregateKeys.ListAdded], Is.EqualTo(3));
            Assert.That(fixture.Events.Events[1].Status.Aggregates[ImportAggregateKeys.ConsentRecorded], Is.EqualTo(3));
            Assert.That(fixture.Events.Events[1].Job.Attestation!.UserId, Is.EqualTo(UserId));
            Assert.That(fixture.Events.Events[1].Audience, Is.EqualTo(new ImportAudience(ListId, ConsentId, ConsentHash)));
        });
    }

    [Test]
    public void Event_Is_Raised_When_A_Job_Fails_Or_Is_Cancelled()
    {
        using var fixture = new Fixture();
        var failedAnalysis = fixture.Upload(["a@x.test"], new ImportOptions());
        fixture.Store.DeleteChunks(failedAnalysis);
        var cancelledBeforeImport = fixture.Upload(["b@x.test"], new ImportOptions());
        var cancelledImport = fixture.Upload(["c@x.test", "d@x.test", "e@x.test"], new ImportOptions());

        fixture.Run(failedAnalysis, ImportPhase.Analyze);
        fixture.Run(failedAnalysis, ImportPhase.Analyze);
        fixture.Run(cancelledBeforeImport, ImportPhase.Analyze);
        fixture.Processor.Cancel(cancelledBeforeImport, 8);
        fixture.Processor.Cancel(cancelledBeforeImport, 8);
        fixture.Run(cancelledImport, ImportPhase.Analyze);
        fixture.Processor.Confirm(cancelledImport);
        fixture.Repository.OnWrite = _ => fixture.Processor.Cancel(cancelledImport, 9);
        var interrupted = fixture.Run(cancelledImport, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(interrupted.Outcome, Is.EqualTo(ImportProcessOutcome.Interrupted));
            Assert.That(fixture.Events.Events.Select(e => (e.Job.JobId, e.Outcome, e.IsDryRun, e.UserId)), Is.EqualTo(new[]
            {
                (failedAnalysis, ContactImportStatus.Failed, true, UserId),
                (cancelledBeforeImport, ContactImportStatus.Analyzed, true, UserId),
                (cancelledBeforeImport, ContactImportStatus.Cancelled, true, 8),
                (cancelledImport, ContactImportStatus.Analyzed, true, UserId),
                (cancelledImport, ContactImportStatus.Cancelled, false, (int?)9),
            }));
        });
    }

    [Test]
    public void Cancelling_Before_A_Delete_And_The_Cleanup_Raise_The_Event_Once()
    {
        using var fixture = new Fixture(options => options.AbandonedJobRetentionDays = 1);
        var deleted = fixture.Upload(["a@x.test"], new ImportOptions());
        var finished = fixture.Upload(["b@x.test"], new ImportOptions());
        fixture.Run(finished, ImportPhase.Analyze);
        fixture.Processor.Confirm(finished);
        fixture.Run(finished, ImportPhase.Execute);
        fixture.Events.Events.Clear();

        fixture.Processor.CancelIfIdle(deleted, null, 4);
        fixture.Processor.CancelIfIdle(deleted, null, 4);
        fixture.Processor.CancelIfIdle(finished, null, 4);
        var abandoned = fixture.Upload(["c@x.test"], new ImportOptions());
        fixture.Time.Advance(TimeSpan.FromDays(3));
        fixture.Cleanup.Sweep();

        Assert.That(fixture.Events.Events.Select(e => (e.Job.JobId, e.Outcome, e.IsDryRun, e.UserId)), Is.EqualTo(new[]
        {
            (deleted, ContactImportStatus.Cancelled, true, 4),
            (abandoned, ContactImportStatus.Cancelled, true, (int?)null),
        }));
    }

    [Test]
    public void Event_Log_Names_The_Users_And_No_Contact_Data()
    {
        using var fixture = new Fixture();
        var jobId = fixture.Upload(["secret.person@x.test"], listAndConsent);

        fixture.Run(jobId, ImportPhase.Analyze, userId: 5);
        fixture.Processor.Confirm(jobId, fixture.Attestation());
        fixture.Run(jobId, ImportPhase.Execute, userId: 6);
        var cancelled = fixture.Upload(["other.person@x.test"], new ImportOptions());
        fixture.Processor.Cancel(cancelled, 8);
        fixture.Processor.Cancel(fixture.Upload(["third.person@x.test"], new ImportOptions()));

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Log, Has.None.Contains("person@"));
            Assert.That(fixture.Log.Where(l => l.Contains("CONTACT_IMPORT_START")), Has.Exactly(1).Contains("Analyze started by user 5 (import created by user 53)").And.Exactly(1).Contains("Execute started by user 6"));
            Assert.That(fixture.Log.Where(l => l.Contains("CONTACT_IMPORT_START")), Has.All.Contains($"recipient list {ListId}, consent {ConsentId}"));
            Assert.That(fixture.Log.Where(l => l.Contains("CONTACT_IMPORT_FINISH")), Has.Exactly(1).Contains("Execute finished by user 6 (import created by user 53)"));
            Assert.That(fixture.Log.Single(l => l.Contains("Execute finished")), Does.Contain($"Recipient list {ListId}: added 1").And.Contain($"Consent {ConsentId}: recorded 1"));
            Assert.That(fixture.Log.Where(l => l.Contains("CONTACT_IMPORT_CANCEL")), Has.Exactly(1).Contains("cancelled by user 8").And.Exactly(1).Contains("cancelled by user unknown"));
        });
    }
}
