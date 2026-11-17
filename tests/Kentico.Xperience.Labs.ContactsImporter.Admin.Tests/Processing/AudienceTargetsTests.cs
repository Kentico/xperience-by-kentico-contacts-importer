using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

/// <summary>
/// Which contacts of a batch the recipient list and consent behaviors apply to (spec 4 "Which contacts"), and the
/// SQL and JSON that read their state.
/// </summary>
internal class AudienceTargetsTests
{
    private static readonly DateTime older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime newer = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly string[] emailTargets = [Email, FirstName];
    private static readonly string[] customerTargets = [CustomerId, Email, FirstName];

    private static ImportOptions List(ImportOptions? options = null) => (options ?? new ImportOptions()) with { RecipientListId = 7 };

    private static BatchPlan Plan(ImportProcessingContext context, ImportRecord[] records, params ContactMatch[] matches)
    {
        var planner = new BatchPlanner(context);

        return planner.Classify(planner.Prepare(records), matches);
    }

    private static ContactMatch Match(int groupIndex, int contactId, DateTime lastModified, params object?[] values) => new(groupIndex, contactId, lastModified, values);

    [Test]
    public void Job_Without_List_And_Consent_Has_No_Targets()
    {
        var plan = Plan(Context(Email, emailTargets), [Record(1, "a@x.test", "a@x.test", "Ann")]);

        Assert.That(plan.Targets, Is.Empty);
    }

    [Test]
    public void Created_Updated_And_Unchanged_Contacts_Are_Targets()
    {
        var plan = Plan(
            Context(Email, emailTargets, List()),
            [
                Record(1, "new@x.test", "new@x.test", "New"),
                Record(2, "upd@x.test", "upd@x.test", "Changed"),
                Record(3, "same@x.test", "same@x.test", "Same"),
            ],
            Match(1, 20, older, null, "Old"),
            Match(2, 30, older, null, "Same"));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows.Select(r => r.Outcome), Is.EqualTo(new[] { ImportRowOutcome.Created, ImportRowOutcome.Updated, ImportRowOutcome.Unchanged }));
            Assert.That(plan.Targets, Is.EqualTo(new[]
            {
                new AudienceTarget(null, 0, 1, 0, true, "new@x.test", []),
                new AudienceTarget(20, -1, 2, 1, false, null, []),
                new AudienceTarget(30, -1, 3, 2, false, null, []),
            }).Using<AudienceTarget>(SameTarget));
        });
    }

    [Test]
    public void Existing_Contacts_That_Are_Not_Updated_Are_Still_Targets()
    {
        var plan = Plan(
            Context(Email, emailTargets, List(new ImportOptions { ExistingContacts = ExistingContactsBehavior.Skip })),
            [Record(1, "a@x.test", "a@x.test", "Changed")],
            Match(0, 20, older, null, "Old"));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Rows.Single().Reason, Is.EqualTo(ImportRowReasons.ExistingContactsNotUpdated));
            Assert.That(plan.Updates, Is.Empty);
            Assert.That(plan.Targets.Single().ContactId, Is.EqualTo(20));
        });
    }

    [Test]
    public void Skipped_Rows_Have_No_Targets()
    {
        var notCreated = Plan(Context(Email, emailTargets, List(new ImportOptions { NewContacts = NewContactsBehavior.Skip })), [Record(1, "a@x.test", "a@x.test", "Ann")]);
        var invalid = Plan(Context(Email, [Email, "Age"], List()), [Record(1, "a@x.test", "a@x.test", "not a number")]);
        var invalidEmail = Plan(Context(CustomerId, customerTargets, List()), [Record(1, "C1", "C1", "not-an-email", "Ann")]);

        Assert.Multiple(() =>
        {
            Assert.That(notCreated.Targets, Is.Empty);
            Assert.That(invalid.Targets, Is.Empty);
            Assert.That(invalidEmail.Targets, Is.Empty);
        });
    }

    [TestCase(MultipleMatchesBehavior.UpdateNewest, ExistingContactsBehavior.Update, new[] { 21 })]
    [TestCase(MultipleMatchesBehavior.UpdateAll, ExistingContactsBehavior.Update, new[] { 20, 21 })]
    [TestCase(MultipleMatchesBehavior.Skip, ExistingContactsBehavior.Update, new int[0])]
    [TestCase(MultipleMatchesBehavior.UpdateNewest, ExistingContactsBehavior.Skip, new[] { 21 })]
    [TestCase(MultipleMatchesBehavior.UpdateAll, ExistingContactsBehavior.Skip, new[] { 20, 21 })]
    [TestCase(MultipleMatchesBehavior.Skip, ExistingContactsBehavior.Skip, new int[0])]
    public void Several_Matches_Give_The_Contacts_The_Multiple_Matches_Option_Chooses(MultipleMatchesBehavior multiple, ExistingContactsBehavior existing, int[] expected)
    {
        var plan = Plan(
            Context(Email, emailTargets, List(new ImportOptions { MultipleMatches = multiple, ExistingContacts = existing })),
            [Record(1, "a@x.test", "a@x.test", "Ann")],
            Match(0, 20, older, null, "Ann"),
            Match(0, 21, newer, null, "Ann"));

        Assert.That(plan.Targets.Select(t => t.ContactId), Is.EqualTo(expected));
    }

    [Test]
    public void Rows_With_The_Same_Key_Are_One_Target_At_The_Last_Row_With_All_Their_Consent_Columns()
    {
        var context = Context(
            Email,
            emailTargets,
            new ImportOptions { Consent = ConsentMode.BlanketWithColumnOverride, ConsentId = 9 },
            new ImportConsentMapping { Agreed = "consent" });
        var yes = new ImportRecordConsent(true, null);
        var no = new ImportRecordConsent(false, null);

        var plan = Plan(context,
        [
            Record(1, "a@x.test", "a@x.test", "A1") with { Consent = yes },
            Record(2, "b@x.test", "b@x.test", "B") with { Consent = no },
            Record(3, "a@x.test", "a@x.test", "A2") with { Consent = no },
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Creates, Has.Count.EqualTo(2));
            Assert.That(plan.Targets, Has.Count.EqualTo(2));
            Assert.That(plan.Targets[0].Row, Is.EqualTo(3));
            Assert.That(plan.Targets[0].Position, Is.EqualTo(2));
            Assert.That(plan.Targets[0].Consents, Is.EqualTo(new[] { yes, no }));
            Assert.That(plan.Targets[1].Consents, Is.EqualTo(new[] { no }));
        });
    }

    [Test]
    public void Consent_Columns_Are_Not_Carried_For_Blanket_Consent()
    {
        var context = Context(Email, emailTargets, new ImportOptions { Consent = ConsentMode.Blanket, ConsentId = 9 }, new ImportConsentMapping { Agreed = "consent" });

        var plan = Plan(context, [Record(1, "a@x.test", "a@x.test", "A") with { Consent = new ImportRecordConsent(false, null) }]);

        Assert.That(plan.Targets.Single().Consents, Is.Empty);
    }

    // The email a contact has after the batch, which the analysis uses instead of the stored one.

    [Test]
    public void New_Contact_Has_Exactly_The_Email_Of_The_Batch()
    {
        var withEmail = Plan(Context(CustomerId, customerTargets, List()), [Record(1, "C1", "C1", "Ann@X.test", "Ann")]);
        var emptyEmail = Plan(Context(CustomerId, customerTargets, List()), [Record(1, "C1", "C1", "", "Ann")]);
        var droppedEmail = Plan(Context(CustomerId, customerTargets, List(new ImportOptions { InvalidEmail = InvalidEmailBehavior.ImportWithoutEmail })), [Record(1, "C1", "C1", "nope", "Ann")]);
        var notMapped = Plan(Context(CustomerId, [CustomerId, FirstName], List()), [Record(1, "C1", "C1", "Ann")]);

        Assert.Multiple(() =>
        {
            Assert.That((withEmail.Targets.Single().HasPlannedEmail, withEmail.Targets.Single().PlannedEmail), Is.EqualTo((true, "ann@x.test")));
            Assert.That((emptyEmail.Targets.Single().HasPlannedEmail, emptyEmail.Targets.Single().PlannedEmail), Is.EqualTo((true, (string?)null)));
            Assert.That((droppedEmail.Targets.Single().HasPlannedEmail, droppedEmail.Targets.Single().PlannedEmail), Is.EqualTo((true, (string?)null)));
            Assert.That((notMapped.Targets.Single().HasPlannedEmail, notMapped.Targets.Single().PlannedEmail), Is.EqualTo((true, (string?)null)));
        });
    }

    [Test]
    public void Existing_Contact_Gets_A_Planned_Email_Only_When_The_Batch_Writes_The_Email_Field()
    {
        static (bool, string?) Planned(BatchPlan plan) => (plan.Targets.Single().HasPlannedEmail, plan.Targets.Single().PlannedEmail);
        var match = Match(0, 20, older, null, "old@x.test", "Ann");
        ImportRecord[] value = [Record(1, "C1", "C1", "new@x.test", "Ann")];
        ImportRecord[] empty = [Record(1, "C1", "C1", "", "Ann")];
        ImportRecord[] absent = [Record(1, "C1", "C1", null, "Ann")];

        Assert.Multiple(() =>
        {
            Assert.That(Planned(Plan(Context(CustomerId, customerTargets, List()), value, match)), Is.EqualTo((true, "new@x.test")));
            Assert.That(Planned(Plan(Context(CustomerId, customerTargets, List()), empty, match)), Is.EqualTo((false, (string?)null)), "An empty value keeps the stored email.");
            Assert.That(Planned(Plan(Context(CustomerId, customerTargets, List(new ImportOptions { EmptyValues = EmptyValuesBehavior.Clear })), empty, match)), Is.EqualTo((true, (string?)null)), "An empty value clears the email.");
            Assert.That(Planned(Plan(Context(CustomerId, customerTargets, List()), absent, match)), Is.EqualTo((false, (string?)null)));
            Assert.That(
                Planned(Plan(Context(CustomerId, customerTargets, List(new ImportOptions { ExistingContacts = ExistingContactsBehavior.Skip })), value, match)),
                Is.EqualTo((false, (string?)null)),
                "A contact that is not updated keeps the stored email.");
            Assert.That(
                Planned(Plan(Context(Email, emailTargets, List()), [Record(1, "a@x.test", "a@x.test", "Ann")], Match(0, 20, older, null, "Ann"))),
                Is.EqualTo((false, (string?)null)),
                "The key is what matched and is never rewritten.");
        });
    }

    // State query.

    [Test]
    public void State_Query_Reads_Membership_Newest_Confirmation_Bounce_And_Newest_Agreement()
    {
        string sql = AudienceSqlBuilder.BuildStateSql(AudienceTables.Default, recipientList: true, consent: true);

        Assert.That(sql, Is.EqualTo(
            "SELECT t.[i] AS [__i], c.[ContactID] AS [__id], CASE WHEN e.[v] IS NOT NULL AND LTRIM(RTRIM(e.[v])) <> N'' THEN 1 ELSE 0 END AS [__he]"
            + ", CASE WHEN EXISTS (SELECT 1 FROM [OM_ContactGroupMember] AS m WHERE m.[ContactGroupMemberContactGroupID] = @listId AND m.[ContactGroupMemberType] = 0 AND m.[ContactGroupMemberRelatedID] = c.[ContactID]) THEN 1 ELSE 0 END AS [__m]"
            + ", s.[EmailSubscriptionConfirmationIsApproved] AS [__sa], s.[EmailSubscriptionConfirmationDate] AS [__sd], s.[EmailSubscriptionConfirmationGUID] AS [__sg]"
            + ", b.[EmailBounceIsHardBounce] AS [__bh], b.[EmailBounceSoftBounceCount] AS [__bs]"
            + ", a.[ConsentAgreementRevoked] AS [__ar], a.[ConsentAgreementTime] AS [__at], a.[ConsentAgreementGuid] AS [__ag]"
            + " FROM OPENJSON(@targets) WITH ([i] int '$.i', [id] int '$.id', [p] bit '$.p', [e] nvarchar(256) '$.e') AS t"
            + " LEFT JOIN [OM_Contact] AS c ON c.[ContactID] = t.[id]"
            + " CROSS APPLY (SELECT CASE WHEN @planned = 1 AND t.[p] = 1 THEN t.[e] ELSE c.[ContactEmail] END AS [v]) AS e"
            + " OUTER APPLY (SELECT TOP (1) x.[EmailSubscriptionConfirmationIsApproved], x.[EmailSubscriptionConfirmationDate], x.[EmailSubscriptionConfirmationGUID]"
            + " FROM [EmailLibrary_EmailSubscriptionConfirmation] AS x WHERE x.[EmailSubscriptionConfirmationContactID] = c.[ContactID] AND x.[EmailSubscriptionConfirmationRecipientListID] = @listId"
            + " ORDER BY x.[EmailSubscriptionConfirmationDate] DESC, x.[EmailSubscriptionConfirmationID] DESC) AS s"
            + " LEFT JOIN [EmailLibrary_EmailBounce] AS b ON b.[EmailBounceEmailAddress] = e.[v]"
            + " OUTER APPLY (SELECT TOP (1) x.[ConsentAgreementRevoked], x.[ConsentAgreementTime], x.[ConsentAgreementGuid]"
            + " FROM [CMS_ConsentAgreement] AS x WHERE x.[ConsentAgreementContactID] = c.[ContactID] AND x.[ConsentAgreementConsentID] = @consentId"
            + " ORDER BY x.[ConsentAgreementTime] DESC, x.[ConsentAgreementID] DESC) AS a"));
    }

    [Test]
    public void State_Query_Leaves_Out_What_The_Job_Does_Not_Use_And_Quotes_Every_Identifier()
    {
        string listOnly = AudienceSqlBuilder.BuildStateSql(AudienceTables.Default, recipientList: true, consent: false);
        string consentOnly = AudienceSqlBuilder.BuildStateSql(AudienceTables.Default, recipientList: false, consent: true);
        string odd = AudienceSqlBuilder.BuildStateSql(AudienceTables.Default with { MemberTable = "Odd]Table", AgreementTime = "x]; DROP TABLE OM_Contact; --" }, true, true);

        Assert.Multiple(() =>
        {
            Assert.That(listOnly, Does.Not.Contain("ConsentAgreement").And.Not.Contain("@consentId"));
            Assert.That(consentOnly, Does.Not.Contain("ContactGroupMember").And.Not.Contain("EmailBounce").And.Not.Contain("@listId"));
            Assert.That(odd, Does.Contain("FROM [Odd]]Table] AS m").And.Contain("x.[x]]; DROP TABLE OM_Contact; --] DESC"));
        });
    }

    [Test]
    public void Created_Contacts_Are_Found_By_Their_Guids() =>
        Assert.That(
            AudienceSqlBuilder.BuildCreatedContactsSql(AudienceTables.Default),
            Is.EqualTo("SELECT g.[i] AS [__i], c.[ContactID] AS [__id] FROM OPENJSON(@keys) WITH ([i] int '$.i', [k] uniqueidentifier '$.k') AS g INNER JOIN [OM_Contact] AS c ON c.[ContactGUID] = g.[k]"));

    [Test]
    public void Targets_Json_Carries_Ids_And_Planned_Emails_As_Data()
    {
        AudienceTarget[] targets =
        [
            new(5, -1, 1, 0, false, null, []),
            new(null, 0, 2, 1, true, "o'brien\"@x.test", []),
            new(null, 1, 3, 2, true, null, []),
            new(6, -1, 4, 3, true, null, []),
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                AudienceSqlBuilder.BuildTargetsJson(targets, null),
                Is.EqualTo("[{\"i\":0,\"id\":5},{\"i\":1,\"p\":1,\"e\":\"o\\u0027brien\\u0022@x.test\"},{\"i\":2,\"p\":1},{\"i\":3,\"id\":6,\"p\":1}]"));
            Assert.That(
                AudienceSqlBuilder.BuildTargetsJson(targets, [70, 71]),
                Is.EqualTo("[{\"i\":0,\"id\":5},{\"i\":1,\"id\":70,\"p\":1,\"e\":\"o\\u0027brien\\u0022@x.test\"},{\"i\":2,\"id\":71,\"p\":1},{\"i\":3,\"id\":6,\"p\":1}]"));
        });
    }

    private static bool SameTarget(AudienceTarget x, AudienceTarget y) => x with { Consents = [] } == y with { Consents = [] } && x.Consents.SequenceEqual(y.Consents);
}
