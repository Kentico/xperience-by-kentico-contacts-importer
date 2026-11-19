using CMS.Base;
using CMS.ContactManagement;
using CMS.Core;
using CMS.DataEngine;
using CMS.DataProtection;
using CMS.EmailMarketing;
using CMS.EmailMarketing.Internal;
using CMS.Helpers;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// Recipient lists, bounces and consent agreements on the real database. End states are checked the way Xperience
/// reads them: its own services (<see cref="IEmailSubscriptionConfirmationService.IsConfirmed"/>,
/// <see cref="IConsentAgreementService.IsAgreed"/>) and the recipient rule of its send-time procedure.
/// </summary>
public class AudienceTests : ImportIntegrationTest
{
    private const string Domain = "@audience.test";

    private static readonly string[] emailTargets = [Email, FirstName];

    private int listId;
    private int otherListId;
    private int consentId;
    private string consentHash = "";

    private static IEmailSubscriptionConfirmationService Subscriptions => Service.Resolve<IEmailSubscriptionConfirmationService>();

    private static IConsentAgreementService Agreements => Service.Resolve<IConsentAgreementService>();

    [OneTimeSetUp]
    public void CreateListsAndConsent()
    {
        RemoveTestObjects();
        listId = CreateRecipientList("ImporterTestList");
        otherListId = CreateRecipientList("ImporterTestOtherList");
        var consent = new ConsentInfo { ConsentName = "ImporterTestConsent", ConsentDisplayName = "Importer test consent", ConsentContent = "I agree to the newsletter." };
        Provider<ConsentInfo>.Instance.Set(consent);
        consentId = consent.ConsentID;
        consentHash = IntegrationDatabase.Scalar<string>("SELECT [ConsentHash] FROM [CMS_Consent] WHERE [ConsentID] = @id", ("@id", consentId));
        Assert.That(consentHash, Is.Not.Empty, "Xperience computes the hash of the consent text when the consent is saved.");
    }

    [OneTimeTearDown]
    public void RemoveTestObjects()
    {
        IntegrationDatabase.DeleteContacts();
        IntegrationDatabase.Execute(
            """
            DELETE FROM [EmailLibrary_EmailBounce] WHERE [EmailBounceEmailAddress] LIKE N'%@audience.test';
            DELETE m FROM [OM_ContactGroupMember] m INNER JOIN [OM_ContactGroup] g ON g.[ContactGroupID] = m.[ContactGroupMemberContactGroupID] WHERE g.[ContactGroupName] LIKE N'ImporterTest%';
            DELETE s FROM [EmailLibrary_RecipientListSettings] s INNER JOIN [OM_ContactGroup] g ON g.[ContactGroupID] = s.[RecipientListSettingsRecipientListID] WHERE g.[ContactGroupName] LIKE N'ImporterTest%';
            DELETE FROM [OM_ContactGroup] WHERE [ContactGroupName] LIKE N'ImporterTest%';
            DELETE a FROM [CMS_ConsentArchive] a INNER JOIN [CMS_Consent] c ON c.[ConsentID] = a.[ConsentArchiveConsentID] WHERE c.[ConsentName] LIKE N'ImporterTest%';
            DELETE FROM [CMS_Consent] WHERE [ConsentName] LIKE N'ImporterTest%';
            """);
    }

    [SetUp]
    public void DeleteBounces() => IntegrationDatabase.Execute("DELETE FROM [EmailLibrary_EmailBounce] WHERE [EmailBounceEmailAddress] LIKE N'%@audience.test'");

    private static int CreateRecipientList(string name)
    {
        var list = new ContactGroupInfo { ContactGroupName = name, ContactGroupDisplayName = name, ContactGroupIsRecipientList = true, ContactGroupEnabled = true };
        Provider<ContactGroupInfo>.Instance.Set(list);

        return list.ContactGroupID;
    }

    private ImportOptions ListOptions => new() { RecipientListId = listId };

    private ImportOptions ConsentOptions => new() { Consent = ConsentMode.Blanket, ConsentId = consentId };

    private static string Address(string name) => name + Domain;

    private static ContactInfo Contact(int contactId) => Provider<ContactInfo>.Instance.Get(contactId);

    private static int ContactId(string name) => (int)ContactByEmail(Address(name))["ContactID"]!;

    private static ContactGroupInfo List(int id) => Provider<ContactGroupInfo>.Instance.Get(id);

    private ConsentInfo Consent() => Provider<ConsentInfo>.Instance.Get(consentId);

    // Existing state, written the way Xperience writes it.

    /// <summary>A contact that subscribed: manual member plus approved confirmation, as the confirmation link does.</summary>
    private int SeedSubscribed(string name, int? list = null)
    {
        int id = Seed(Address(name));
        var group = List(list ?? listId);
        Provider<ContactGroupMemberInfo>.Instance.Add(group.ContactGroupID, id, ContactGroupMemberTypeEnum.Contact, MemberAddedHowEnum.Manual);
        Subscriptions.Confirm(Contact(id), group);

        return id;
    }

    /// <summary>A contact that subscribed and then unsubscribed: it stays a member, the newest confirmation is not approved.</summary>
    private int SeedUnsubscribed(string name)
    {
        int id = SeedSubscribed(name);
        Subscriptions.Revoke(Contact(id), List(listId));

        return id;
    }

    private int SeedAgreed(string name)
    {
        int id = Seed(Address(name));
        Agreements.Agree(Contact(id), Consent());

        return id;
    }

    private int SeedRevoked(string name)
    {
        int id = SeedAgreed(name);
        Agreements.Revoke(Contact(id), Consent());

        return id;
    }

    private static void SeedBounce(string email, bool hard, int soft) =>
        Provider<EmailBounceInfo>.Instance.Set(new EmailBounceInfo { EmailBounceEmailAddress = email, EmailBounceIsHardBounce = hard, EmailBounceSoftBounceCount = soft });

    // End state as Xperience reads it.

    private bool IsSubscribed(int contactId, int? list = null) => Subscriptions.IsConfirmed(Contact(contactId), List(list ?? listId));

    private bool IsAgreed(int contactId) => Agreements.IsAgreed(Contact(contactId), Consent());

    /// <summary>
    /// Contacts Xperience would email for the list: the recipient rule of <c>Proc_EmailLibrary_EmailMarketingRecipient_GenerateRecipients</c>
    /// (members of type contact with an email, without a newest confirmation that is not approved, address not bounced).
    /// </summary>
    private int[] Recipients(int softBounceLimit = 5) =>
        [.. IntegrationDatabase.Query(
            """
            WITH unsubscribed AS (
                SELECT t.[EmailSubscriptionConfirmationContactID] AS [ContactID] FROM (
                    SELECT [EmailSubscriptionConfirmationContactID], [EmailSubscriptionConfirmationIsApproved],
                        ROW_NUMBER() OVER (PARTITION BY [EmailSubscriptionConfirmationContactID] ORDER BY [EmailSubscriptionConfirmationDate] DESC) AS rn
                    FROM [EmailLibrary_EmailSubscriptionConfirmation] WHERE [EmailSubscriptionConfirmationRecipientListID] = @list) t
                WHERE t.rn = 1 AND t.[EmailSubscriptionConfirmationIsApproved] = 0)
            SELECT DISTINCT cgm.[ContactGroupMemberRelatedID] AS [ContactID]
            FROM [OM_ContactGroupMember] cgm LEFT JOIN [OM_Contact] c ON c.[ContactID] = cgm.[ContactGroupMemberRelatedID]
            WHERE cgm.[ContactGroupMemberContactGroupID] = @list AND cgm.[ContactGroupMemberType] = 0
                AND c.[ContactEmail] IS NOT NULL AND LTRIM(RTRIM(c.[ContactEmail])) != ''
                AND cgm.[ContactGroupMemberRelatedID] NOT IN (SELECT [ContactID] FROM unsubscribed)
                AND NOT EXISTS (SELECT 1 FROM [EmailLibrary_EmailBounce] b WHERE b.[EmailBounceEmailAddress] = c.[ContactEmail]
                    AND (b.[EmailBounceIsHardBounce] = 1 OR b.[EmailBounceSoftBounceCount] >= @limit))
            """,
            ("@list", listId),
            ("@limit", softBounceLimit)).Select(row => (int)row["ContactID"]!).Order()];

    private static int Count(string table) => IntegrationDatabase.Scalar<int>($"SELECT COUNT(*) FROM [{table}]");

    private static (int Members, int Confirmations, int Agreements) Rows() =>
        (IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [OM_ContactGroupMember] WHERE [ContactGroupMemberType] = 0"), Count("EmailLibrary_EmailSubscriptionConfirmation"), Count("CMS_ConsentAgreement"));

    private static Dictionary<string, long> AudienceCounts(IReadOnlyDictionary<string, long> aggregates) =>
        aggregates.Where(a => a.Key.StartsWith("list", StringComparison.Ordinal) || a.Key.StartsWith("consent", StringComparison.Ordinal) || a.Key.StartsWith("bounced", StringComparison.Ordinal))
            .ToDictionary(a => a.Key, a => a.Value);

    private static string?[] Row(string name, string? firstName = "Imported") => [Address(name), firstName];

    [Test]
    public void Recipient_List_Import_Adds_Subscribes_And_Keeps_Unsubscribed_Contacts_As_Xperience_Reads_Them()
    {
        int existing = Seed(Address("existing"));
        int subscribed = SeedSubscribed("subscribed");
        int unsubscribed = SeedUnsubscribed("unsubscribed");
        int otherList = SeedSubscribed("otherlist", otherListId);
        int bounced = Seed(Address("bounced"));
        SeedBounce(Address("bounced"), hard: true, soft: 0);
        var before = Rows();
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, emailTargets,
        [
            Row("new"), Row("existing"), Row("subscribed"), Row("unsubscribed"), Row("otherlist"), Row("bounced"),
        ], ListOptions with { BouncedAddress = BouncedAddressBehavior.AddAndReport });

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        var afterAnalysis = Rows();
        Assert.That(harness.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
        var progress = harness.Run(jobId, ImportPhase.Execute);

        int created = ContactId("new");
        var status = harness.Store.ReadStatus(jobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), progress.Error?.ToString());
            Assert.That(afterAnalysis, Is.EqualTo(before), "The analysis writes nothing.");
            Assert.That(AudienceCounts(status.Aggregates), Is.EquivalentTo(new Dictionary<string, long>
            {
                [ImportAggregateKeys.ListAdded] = 4,
                [ImportAggregateKeys.ListAlreadySubscribed] = 1,
                [ImportAggregateKeys.ListKeptUnsubscribed] = 1,
                [ImportAggregateKeys.BouncedHard] = 1,
            }));
            Assert.That(AudienceCounts(status.Analysis!.Aggregates), Is.EquivalentTo(AudienceCounts(status.Aggregates)), "The analysis predicts the import.");
            Assert.That(analysis.Aggregates.ListAdded, Is.EqualTo(4));

            // Xperience's own reading.
            Assert.That(IsSubscribed(created), Is.True);
            Assert.That(IsSubscribed(existing), Is.True);
            Assert.That(IsSubscribed(subscribed), Is.True);
            Assert.That(IsSubscribed(unsubscribed), Is.False, "Unsubscribed contacts stay unsubscribed by default.");
            Assert.That(IsSubscribed(otherList), Is.True);
            Assert.That(IsSubscribed(otherList, otherListId), Is.True, "Other lists are not touched.");
            Assert.That(IsSubscribed(bounced), Is.True, "Bounced addresses are added and reported by default.");
            Assert.That(Recipients(), Is.EqualTo(new[] { existing, subscribed, otherList, created }.Order()), "Xperience sends to everyone but the unsubscribed and the bounced contact.");

            // No second membership, no confirmation for contacts that needed none.
            Assert.That(Rows(), Is.EqualTo((before.Members + 4, before.Confirmations + 4, before.Agreements)));
            Assert.That(IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [EmailLibrary_EmailBounce] WHERE [EmailBounceEmailAddress] LIKE N'%@audience.test'"), Is.EqualTo(1), "Bounces are never written.");
            Assert.That(harness.ReadReport(jobId, ImportPhase.Execute), Is.EqualTo(new[]
            {
                "row,outcome,reason,field,list",
                "1,created,,,added",
                "2,updated,,,added",
                "3,updated,,,alreadySubscribed",
                "4,updated,,,keptUnsubscribed",
                "5,updated,,,added",
                "6,updated,,,added;bouncedHard",
            }));
        });
    }

    [Test]
    public void Written_Rows_Have_The_Values_Xperience_Writes_For_A_Manual_Member_And_A_Confirmation()
    {
        int reference = SeedSubscribed("reference");
        using var harness = new ImportHarness();
        var start = DateTime.Now.AddSeconds(-1);
        var jobId = harness.Upload(Email, emailTargets, [Row("imported")], ListOptions);

        harness.Import(jobId);

        int imported = ContactId("imported");
        const string memberColumns = "[ContactGroupMemberContactGroupID], [ContactGroupMemberType], [ContactGroupMemberFromCondition], [ContactGroupMemberFromAccount], [ContactGroupMemberFromManual], [ContactGroupMemberProfileID]";
        var expectedMember = IntegrationDatabase.Query($"SELECT {memberColumns} FROM [OM_ContactGroupMember] WHERE [ContactGroupMemberRelatedID] = @id", ("@id", reference)).Single();
        var member = IntegrationDatabase.Query($"SELECT {memberColumns} FROM [OM_ContactGroupMember] WHERE [ContactGroupMemberRelatedID] = @id", ("@id", imported)).Single();
        var confirmation = IntegrationDatabase.Query(
            "SELECT [EmailSubscriptionConfirmationRecipientListID] AS [List], [EmailSubscriptionConfirmationIsApproved] AS [Approved], [EmailSubscriptionConfirmationDate] AS [Date], [EmailSubscriptionConfirmationGUID] AS [Guid] FROM [EmailLibrary_EmailSubscriptionConfirmation] WHERE [EmailSubscriptionConfirmationContactID] = @id",
            ("@id", imported)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(member, Is.EquivalentTo(expectedMember), "Same columns as ContactGroupMemberInfoProvider.Add(..., Manual).");
            Assert.That(confirmation["List"], Is.EqualTo(listId));
            Assert.That(confirmation["Approved"], Is.EqualTo(true));
            Assert.That((DateTime)confirmation["Date"]!, Is.GreaterThan(start).And.LessThanOrEqualTo(DateTime.Now), "Dated at import time, local time like Xperience.");
            Assert.That(confirmation["Guid"], Is.Not.EqualTo(Guid.Empty));
        });
    }

    [Test]
    public void Resubscribe_Gives_Unsubscribed_Contacts_A_New_Approved_Confirmation_And_Leaves_The_Old_Rows()
    {
        int unsubscribed = SeedUnsubscribed("unsubscribed");
        int removed = SeedUnsubscribed("removed");
        IntegrationDatabase.Execute("DELETE FROM [OM_ContactGroupMember] WHERE [ContactGroupMemberRelatedID] = @id", ("@id", removed));
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, emailTargets, [Row("unsubscribed"), Row("removed")], ListOptions with { Unsubscribed = UnsubscribedBehavior.Resubscribe });

        var progress = harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.ListResubscribed, Is.EqualTo(2));
            Assert.That(IsSubscribed(unsubscribed), Is.True);
            Assert.That(IsSubscribed(removed), Is.True);
            Assert.That(Recipients(), Is.EqualTo(new[] { unsubscribed, removed }.Order()));
            Assert.That(IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [EmailLibrary_EmailSubscriptionConfirmation] WHERE [EmailSubscriptionConfirmationContactID] = @id", ("@id", unsubscribed)), Is.EqualTo(3), "Subscribe, unsubscribe, resubscribe: history kept.");
            Assert.That(IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [OM_ContactGroupMember] WHERE [ContactGroupMemberRelatedID] = @id", ("@id", unsubscribed)), Is.EqualTo(1));
            Assert.That(IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [OM_ContactGroupMember] WHERE [ContactGroupMemberRelatedID] = @id", ("@id", removed)), Is.EqualTo(1));
        });
    }

    [Test]
    public void Bounced_Addresses_Are_Skipped_With_The_Option_By_The_Send_Time_Rule_And_The_Soft_Bounce_Limit()
    {
        SeedBounce(Address("hard"), hard: true, soft: 0);
        SeedBounce(Address("softover"), hard: false, soft: 3);
        SeedBounce(Address("softunder"), hard: false, soft: 2);
        using var harness = new ImportHarness();
        harness.Bounces.CurrentValue = new BouncedEmailsGlobalOptions { SoftBounceLimit = 3 };
        var jobId = harness.Upload(Email, emailTargets, [Row("hard"), Row("softover"), Row("softunder"), Row("clean")], ListOptions with { BouncedAddress = BouncedAddressBehavior.SkipAndReport });

        var progress = harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(AudienceCounts(harness.Store.ReadStatus(jobId)!.Aggregates), Is.EquivalentTo(new Dictionary<string, long>
            {
                [ImportAggregateKeys.ListAdded] = 2,
                [ImportAggregateKeys.ListSkippedBounced] = 2,
                [ImportAggregateKeys.BouncedHard] = 1,
                [ImportAggregateKeys.BouncedSoftOverLimit] = 1,
                [ImportAggregateKeys.BouncedSoftUnderLimit] = 1,
            }));
            Assert.That(Recipients(softBounceLimit: 3), Is.EqualTo(new[] { ContactId("softunder"), ContactId("clean") }.Order()));
            Assert.That(IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [OM_ContactGroupMember] WHERE [ContactGroupMemberType] = 0"), Is.EqualTo(2));
            Assert.That(progress.Aggregates.Created, Is.EqualTo(4), "The contacts are imported; only the list add is skipped.");
        });
    }

    [Test]
    public void Contacts_Without_Email_Are_Not_Added_And_The_Email_Is_Read_After_It_Is_Written()
    {
        int noEmail = Seed(null, code: "C-NOEMAIL");
        int gainsEmail = Seed(null, code: "C-GAINS");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Code, [Code, Email], [["C-NOEMAIL", null], ["C-GAINS", Address("gains")], ["C-NEW", ""]], ListOptions);

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        harness.Confirm(jobId);
        var progress = harness.Run(jobId, ImportPhase.Execute);

        Assert.Multiple(() =>
        {
            Assert.That(analysis.Aggregates.ListAdded, Is.EqualTo(1), "The analysis uses the email the import will write.");
            Assert.That(analysis.Aggregates.ListSkippedNoEmail, Is.EqualTo(2));
            Assert.That(progress.Aggregates.ListAdded, Is.EqualTo(1));
            Assert.That(progress.Aggregates.ListSkippedNoEmail, Is.EqualTo(2));
            Assert.That(Recipients(), Is.EqualTo(new[] { gainsEmail }));
            Assert.That(IsSubscribed(noEmail), Is.False);
        });
    }

    [Test]
    public void Blanket_Consent_Records_Agreements_As_Xperience_Reads_Them_And_Keeps_Revoked_Ones()
    {
        int agreed = SeedAgreed("agreed");
        int revoked = SeedRevoked("revoked");
        int existing = Seed(Address("existing"));
        using var harness = new ImportHarness();
        var start = DateTime.Now.AddSeconds(-1);
        var jobId = harness.Upload(Email, emailTargets, [Row("new"), Row("agreed"), Row("revoked"), Row("existing")], ConsentOptions);

        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        int agreementsAfterAnalysis = Count("CMS_ConsentAgreement");
        harness.Confirm(jobId);
        var progress = harness.Run(jobId, ImportPhase.Execute);

        int created = ContactId("new");
        var rows = IntegrationDatabase.Query(
            "SELECT [ConsentAgreementContactID] AS [Contact], [ConsentAgreementConsentHash] AS [Hash], [ConsentAgreementRevoked] AS [Revoked], [ConsentAgreementTime] AS [Time], [ConsentAgreementGuid] AS [Guid] FROM [CMS_ConsentAgreement] WHERE [ConsentAgreementContactID] IN (@a, @b)",
            ("@a", created),
            ("@b", existing));
        Assert.Multiple(() =>
        {
            Assert.That(agreementsAfterAnalysis, Is.EqualTo(3), "The analysis writes nothing.");
            Assert.That(analysis.Aggregates.ConsentRecorded, Is.EqualTo(2));
            Assert.That(AudienceCounts(harness.Store.ReadStatus(jobId)!.Aggregates), Is.EquivalentTo(new Dictionary<string, long>
            {
                [ImportAggregateKeys.ConsentRecorded] = 2,
                [ImportAggregateKeys.ConsentAlreadyAgreed] = 1,
                [ImportAggregateKeys.ConsentKeptRevoked] = 1,
            }));
            Assert.That(progress.Aggregates.ConsentKeptRevoked, Is.EqualTo(1));
            Assert.That(IsAgreed(created), Is.True);
            Assert.That(IsAgreed(existing), Is.True);
            Assert.That(IsAgreed(agreed), Is.True);
            Assert.That(IsAgreed(revoked), Is.False, "Revoked consent stays revoked by default.");
            Assert.That(Count("CMS_ConsentAgreement"), Is.EqualTo(5), "Agreed and revoked contacts get no new row.");
            Assert.That(rows.Select(r => r["Hash"]), Is.All.EqualTo(consentHash));
            Assert.That(rows.Select(r => r["Revoked"]), Is.All.EqualTo(false));
            Assert.That(rows.Select(r => (DateTime)r["Time"]!), Is.All.GreaterThan(start));
            Assert.That(rows.Select(r => r["Guid"]), Is.All.Not.EqualTo(Guid.Empty));
        });
    }

    [Test]
    public void ReAgree_Records_A_New_Agreement_For_Revoked_Consent()
    {
        int revoked = SeedRevoked("revoked");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, emailTargets, [Row("revoked")], ConsentOptions with { RevokedConsent = RevokedConsentBehavior.ReAgree });

        var progress = harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.ConsentReAgreed, Is.EqualTo(1));
            Assert.That(IsAgreed(revoked), Is.True);
            Assert.That(Count("CMS_ConsentAgreement"), Is.EqualTo(3));
        });
    }

    [Test]
    public void Consent_Columns_Record_Backdated_Agreements_But_Never_One_Older_Than_A_Revocation()
    {
        int revokedOld = SeedRevoked("revokedold");
        int revokedNew = SeedRevoked("revokednew");
        var revokedAt = IntegrationDatabase.Scalar<DateTime>("SELECT MAX([ConsentAgreementTime]) FROM [CMS_ConsentAgreement] WHERE [ConsentAgreementContactID] = @id", ("@id", revokedOld));
        string tomorrow = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var harness = new ImportHarness();
        var jobId = harness.Upload(
            Email,
            emailTargets,
            [Row("dated"), Row("undated"), Row("no"), Row("future"), Row("revokedold"), Row("revokednew")],
            new ImportOptions { Consent = ConsentMode.BlanketWithColumnOverride, ConsentId = consentId, RevokedConsent = RevokedConsentBehavior.ReAgree },
            consents:
            [
                """{"a":true,"d":"2025-03-01"}""",
                """{"a":true}""",
                """{"a":false,"d":"2025-03-01"}""",
                $$"""{"a":true,"d":"{{tomorrow}}"}""",
                """{"a":true,"d":"2025-03-01"}""",
                """{"a":true}""",
            ]);

        var progress = harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Aggregates.Created, Is.EqualTo(4), "Rows are imported whatever their consent columns say.");
            Assert.That(AudienceCounts(harness.Store.ReadStatus(jobId)!.Aggregates), Is.EquivalentTo(new Dictionary<string, long>
            {
                [ImportAggregateKeys.ConsentRecorded] = 2,
                [ImportAggregateKeys.ConsentNotGiven] = 1,
                [ImportAggregateKeys.ConsentSkippedFutureDate] = 1,
                [ImportAggregateKeys.ConsentKeptRevoked] = 1,
                [ImportAggregateKeys.ConsentReAgreed] = 1,
            }));
            Assert.That(
                IntegrationDatabase.Scalar<DateTime>("SELECT [ConsentAgreementTime] FROM [CMS_ConsentAgreement] WHERE [ConsentAgreementContactID] = @id", ("@id", ContactId("dated"))),
                Is.EqualTo(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Unspecified)));
            Assert.That(IsAgreed(ContactId("dated")), Is.True);
            Assert.That(IsAgreed(ContactId("undated")), Is.True);
            Assert.That(IsAgreed(ContactId("no")), Is.False);
            Assert.That(IsAgreed(ContactId("future")), Is.False);
            Assert.That(revokedAt, Is.GreaterThan(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Unspecified)));
            Assert.That(IsAgreed(revokedOld), Is.False, "An agreement dated before the revocation would not be the newest row: withdrawal wins.");
            Assert.That(IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM [CMS_ConsentAgreement] WHERE [ConsentAgreementContactID] = @id", ("@id", revokedOld)), Is.EqualTo(2), "Nothing written for it.");
            Assert.That(IsAgreed(revokedNew), Is.True);
        });
    }

    [Test]
    public void Batch_Run_Again_After_A_Lost_Checkpoint_Writes_No_Second_Membership_Confirmation_Or_Agreement()
    {
        SeedUnsubscribed("u4");
        SeedRevoked("u6");
        using var harness = new ImportHarness(options =>
        {
            options.BatchSize = 4;
            options.ProcessingTimeBudgetSeconds = 0;
        });
        var options = ListOptions with { Consent = ConsentMode.Blanket, ConsentId = consentId, Unsubscribed = UnsubscribedBehavior.Resubscribe, RevokedConsent = RevokedConsentBehavior.ReAgree };
        var jobId = harness.Upload(Email, emailTargets, [.. Enumerable.Range(1, 10).Select(i => Row("u" + i))], options);
        harness.Run(jobId, ImportPhase.Analyze);
        harness.Confirm(jobId);

        var first = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, null, false);
        var afterFirst = harness.Store.ReadStatus(jobId)!;
        var second = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, first.DriverToken, false);
        var rowsAfterSecond = Rows();
        harness.Store.WriteStatus(jobId, afterFirst);

        string? token = first.DriverToken;
        ImportProgress final;
        do
        {
            final = harness.Processor.ProcessNext(jobId, ImportPhase.Execute, token, false);
            token = final.DriverToken;
        }
        while (final.Outcome == ImportProcessOutcome.Processed);

        int[] all = [.. Enumerable.Range(1, 10).Select(i => ContactId("u" + i))];
        Assert.Multiple(() =>
        {
            Assert.That(second.RowsProcessed, Is.EqualTo(8));
            Assert.That(final.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), final.Error?.ToString());
            Assert.That(Rows(), Is.EqualTo((10, rowsAfterSecond.Confirmations + 2, rowsAfterSecond.Agreements + 2)), "Only rows 9-10 add rows after the re-run of rows 5-8.");
            Assert.That(Rows(), Is.EqualTo((10, 2 + 10, 2 + 10)));
            Assert.That(IntegrationDatabase.Scalar<int>("SELECT COUNT(*) FROM (SELECT [ContactGroupMemberRelatedID] FROM [OM_ContactGroupMember] GROUP BY [ContactGroupMemberRelatedID] HAVING COUNT(*) > 1) d"), Is.Zero);
            Assert.That(AudienceCounts(harness.Store.ReadStatus(jobId)!.Aggregates), Is.EquivalentTo(new Dictionary<string, long>
            {
                [ImportAggregateKeys.ListAdded] = 9,
                [ImportAggregateKeys.ListResubscribed] = 1,
                [ImportAggregateKeys.ConsentRecorded] = 9,
                [ImportAggregateKeys.ConsentReAgreed] = 1,
            }), "The re-run recognizes its own rows and counts them as the first run would have.");
            Assert.That(Recipients(), Is.EqualTo(all.Order()));
            Assert.That(all.Select(IsAgreed), Is.All.True);
        });
    }

    [Test]
    public void Importing_The_Same_File_Again_Writes_Nothing_New()
    {
        using var harness = new ImportHarness();
        var options = ListOptions with { Consent = ConsentMode.Blanket, ConsentId = consentId };
        harness.Import(harness.Upload(Email, emailTargets, [Row("a"), Row("b")], options));
        var rows = Rows();

        var second = harness.Import(harness.Upload(Email, emailTargets, [Row("a"), Row("b")], options));

        Assert.Multiple(() =>
        {
            Assert.That(Rows(), Is.EqualTo(rows));
            Assert.That(second.Aggregates.ListAlreadySubscribed, Is.EqualTo(2));
            Assert.That(second.Aggregates.ConsentAlreadyAgreed, Is.EqualTo(2));
        });
    }

    [Test]
    public void Completed_Import_Clears_The_Subscription_And_Consent_Caches_Xperience_Reads()
    {
        int contact = SeedUnsubscribed("cached");
        Agreements.Agree(Contact(contact), Consent());
        Agreements.Revoke(Contact(contact), Consent());
        Assert.Multiple(() =>
        {
            Assert.That(IsSubscribed(contact), Is.False, "Cached as unsubscribed.");
            Assert.That(IsAgreed(contact), Is.False, "Cached as not agreed.");
        });
        string listKey = CacheItemDependingOn($"om.recipientlist|byid|{listId}");
        string agreementsKey = CacheItemDependingOn("cms.consentagreement|all");
        using var harness = new ImportHarness();
        var jobId = harness.Upload(Email, emailTargets, [Row("cached")], ListOptions with
        {
            Unsubscribed = UnsubscribedBehavior.Resubscribe,
            Consent = ConsentMode.Blanket,
            ConsentId = consentId,
            RevokedConsent = RevokedConsentBehavior.ReAgree,
        });

        harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(CacheHelper.TryGetItem(listKey, out string? _), Is.False);
            Assert.That(CacheHelper.TryGetItem(agreementsKey, out string? _), Is.False);
            Assert.That(IsSubscribed(contact), Is.True, "IsConfirmed is not served from its 10-minute cache.");
            Assert.That(IsAgreed(contact), Is.True, "IsAgreed is not served from its 10-minute cache.");
        });
    }

    [Test]
    public void Bulk_Inserts_Touch_The_Type_Keys_Of_Their_Rows_But_Not_The_Recipient_List_Key()
    {
        string listKey = CacheItemDependingOn($"om.recipientlist|byid|{listId}");
        string membersKey = CacheItemDependingOn("om.contactgroupmembercontact|all");
        string confirmationsKey = CacheItemDependingOn("emaillibrary.emailsubscriptionconfirmation|all");
        string agreementsKey = CacheItemDependingOn("cms.consentagreement|all");
        using var harness = new ImportHarness(cacheInvalidator: new NoCacheInvalidation());
        var jobId = harness.Upload(Email, emailTargets, [Row("bulk")], ListOptions with { Consent = ConsentMode.Blanket, ConsentId = consentId });

        harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(CacheHelper.TryGetItem(membersKey, out string? _), Is.False);
            Assert.That(CacheHelper.TryGetItem(confirmationsKey, out string? _), Is.False);
            Assert.That(CacheHelper.TryGetItem(agreementsKey, out string? _), Is.False);
            Assert.That(CacheHelper.TryGetItem(listKey, out string? _), Is.True, "Only the completed import touches it (ImportCacheInvalidator).");
        });
    }

    [Test]
    public void Event_Is_Raised_With_The_Payload_After_Analysis_And_Import()
    {
        var handler = new RecordingHandler();
        using var harness = new ImportHarness();
        harness.Handlers.Add(handler);
        var options = ListOptions with { Consent = ConsentMode.Blanket, ConsentId = consentId, SourceField = IntegrationDatabase.SourceField, SourceValue = "audience-test" };
        var jobId = harness.Upload(Email, emailTargets, [Row("event")], options);

        harness.Import(jobId);

        Assert.Multiple(() =>
        {
            Assert.That(handler.Received.Select(d => (d.Status, d.IsDryRun)), Is.EqualTo(new[] { (ContactImportStatus.Analyzed, true), (ContactImportStatus.Completed, false) }));
            var data = handler.Received[^1];
            Assert.That(data.JobID, Is.EqualTo(jobId));
            Assert.That(data.UserID, Is.EqualTo(ImportHarness.UserId));
            Assert.That(data.RecipientListID, Is.EqualTo(listId));
            Assert.That(data.ConsentID, Is.EqualTo(consentId));
            Assert.That(data.ConsentHash, Is.EqualTo(consentHash));
            Assert.That(data.AttestationUserID, Is.EqualTo(ImportHarness.UserId));
            Assert.That(data.AttestationStatementVersion, Is.EqualTo(ImportAttestationPolicy.StatementVersion));
            Assert.That(data.ImportSourceFieldName, Is.EqualTo(IntegrationDatabase.SourceField));
            Assert.That(data.Counts.Rows, Is.EqualTo(1));
            Assert.That(data.Counts.Created, Is.EqualTo(1));
            Assert.That(data.Counts.ListAdded, Is.EqualTo(1));
            Assert.That(data.Counts.ConsentRecorded, Is.EqualTo(1));
            Assert.That(handler.Received[0].Counts.ListAdded, Is.EqualTo(1), "The analysis has the expected counts.");
            Assert.That(data.Options.Unsubscribed, Is.EqualTo(ContactImportUnsubscribed.Keep));
            Assert.That(data.Options.Consent, Is.EqualTo(ContactImportConsent.Blanket));
            Assert.That(data.Options.SourceValue, Is.EqualTo("audience-test"));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(handler.Received), Does.Not.Contain(Domain), "No contact data in the event.");
        });
    }

    [Test]
    public void Missing_List_Or_Consent_Is_Refused_Before_Anything_Is_Written()
    {
        int segmentLike = CreateRecipientList("ImporterTestNotAList");
        IntegrationDatabase.Execute("UPDATE [OM_ContactGroup] SET [ContactGroupIsRecipientList] = 0 WHERE [ContactGroupID] = @id", ("@id", segmentLike));
        int deleting = CreateRecipientList("ImporterTestDeleting");
        IntegrationDatabase.Execute("UPDATE [OM_ContactGroup] SET [ContactGroupStatus] = 3 WHERE [ContactGroupID] = @id", ("@id", deleting));
        using var harness = new ImportHarness();
        var resolver = harness.Resolver;

        Assert.Multiple(() =>
        {
            Assert.That(resolver.Resolve(ListOptions).Audience, Is.EqualTo(new ImportAudience(listId)));
            Assert.That(resolver.Resolve(ConsentOptions).Audience, Is.EqualTo(new ImportAudience(null, consentId, consentHash)));
            Assert.That(resolver.Resolve(new ImportOptions { RecipientListId = segmentLike }).Error, Is.EqualTo(ImportAudienceResolver.RecipientListMissingError));
            Assert.That(resolver.Resolve(new ImportOptions { RecipientListId = deleting }).Error, Is.EqualTo(ImportAudienceResolver.RecipientListMissingError));
            Assert.That(resolver.Resolve(new ImportOptions { RecipientListId = int.MaxValue }).Error, Is.EqualTo(ImportAudienceResolver.RecipientListMissingError));
            Assert.That(resolver.Resolve(new ImportOptions { Consent = ConsentMode.Blanket, ConsentId = int.MaxValue }).Error, Is.EqualTo(ImportAudienceResolver.ConsentMissingError));
        });

        var jobId = harness.Upload(Email, emailTargets, [Row("refused")], new ImportOptions { RecipientListId = segmentLike });
        var progress = harness.Run(jobId, ImportPhase.Analyze);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.Failed));
            Assert.That(progress.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(progress.Error, Is.EqualTo(ImportAudienceResolver.RecipientListMissingError));
            Assert.That(ContactCount(), Is.Zero);
        });
    }

    private static string CacheItemDependingOn(string dependencyKey)
    {
        string key = "contactsimporter|audiencetest|" + Guid.NewGuid().ToString("N");
        CacheHelper.Add(key, "cached", CacheHelper.GetCacheDependency(dependencyKey), DateTimeOffset.Now.AddHours(1), TimeSpan.Zero);
        Assert.That(CacheHelper.TryGetItem(key, out string? _), Is.True, "The item must be cached before the import.");

        return key;
    }

    private sealed class NoCacheInvalidation : IImportCacheInvalidator
    {
        public void InvalidateAfterImport(ImportJob job)
        {
            // Leaves the cache alone, so a test sees what the writes themselves do to it.
        }
    }

    private sealed class RecordingHandler : IAsyncEventHandler<AfterContactImportEvent>
    {
        public List<ContactImportEventData> Received { get; } = [];

        public Task HandleAsync(AfterContactImportEvent asyncEvent, CancellationToken cancellationToken)
        {
            Received.Add(asyncEvent.Data);

            return Task.CompletedTask;
        }
    }
}
