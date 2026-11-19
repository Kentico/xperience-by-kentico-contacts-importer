using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

internal class AudiencePlannerTests
{
    private const int ListId = 7;
    private const int ConsentId = 9;
    private const int ContactId = 41;
    private const long Row = 12;

    private static readonly DateTime now = new(2026, 11, 16, 13, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime earlier = now.AddDays(-30);

    private static ImportProcessingContext ListContext(UnsubscribedBehavior unsubscribed = UnsubscribedBehavior.Keep, BouncedAddressBehavior bounced = BouncedAddressBehavior.AddAndReport, int softBounceLimit = 5) =>
        Context(Email, [Email, FirstName], new ImportOptions { RecipientListId = ListId, Unsubscribed = unsubscribed, BouncedAddress = bounced }, softBounceLimit: softBounceLimit);

    private static ImportProcessingContext ConsentContext(ConsentMode mode = ConsentMode.Blanket, RevokedConsentBehavior revoked = RevokedConsentBehavior.Keep, ImportConsentMapping? mapping = null) =>
        Context(
            Email,
            [Email, FirstName],
            new ImportOptions { Consent = mode, ConsentId = ConsentId, RevokedConsent = revoked },
            mode == ConsentMode.BlanketWithColumnOverride ? mapping ?? new ImportConsentMapping { Agreed = "consent", Date = "consent date" } : mapping);

    private static AudienceTarget Target(int? contactId = ContactId, params ImportRecordConsent?[] consents) =>
        new(contactId, contactId is null ? 0 : -1, Row, 0, false, null, consents);

    private static AudienceResult Decide(ImportProcessingContext context, AudienceState state, AudienceTarget? target = null) =>
        AudiencePlanner.Decide(context, [target ?? Target(state.ContactId)], [state], now);

    private static Guid WriteGuid(ImportProcessingContext context, AudienceWriteKind kind, int targetId) =>
        AudiencePlanner.CreateWriteGuid(context.JobId, Row, ContactId, targetId, kind);

    private static Dictionary<string, long> Counts(AudienceResult result) => new(result.Counts.Values);

    // Recipient list: membership x newest confirmation x unsubscribed option.

    private static IEnumerable<TestCaseData> ListCases()
    {
        // member, newest confirmation approved (null = none), option, expected outcome, membership written, confirmation written
        yield return new TestCaseData(false, null, UnsubscribedBehavior.Keep, "added", true, true).SetName("Not member, no confirmation: added");
        yield return new TestCaseData(false, null, UnsubscribedBehavior.Resubscribe, "added", true, true).SetName("Not member, no confirmation, resubscribe: added");
        yield return new TestCaseData(false, true, UnsubscribedBehavior.Keep, "added", true, true).SetName("Not member, old approved confirmation: added");
        yield return new TestCaseData(true, true, UnsubscribedBehavior.Keep, "alreadySubscribed", false, false).SetName("Member, approved: already subscribed");
        yield return new TestCaseData(true, true, UnsubscribedBehavior.Resubscribe, "alreadySubscribed", false, false).SetName("Member, approved, resubscribe: already subscribed");
        yield return new TestCaseData(true, false, UnsubscribedBehavior.Keep, "keptUnsubscribed", false, false).SetName("Member, unsubscribed, keep: kept");
        yield return new TestCaseData(true, false, UnsubscribedBehavior.Resubscribe, "resubscribed", false, true).SetName("Member, unsubscribed, resubscribe: resubscribed");
        yield return new TestCaseData(true, null, UnsubscribedBehavior.Keep, "keptUnsubscribed", false, false).SetName("Member without confirmation, keep: kept");
        yield return new TestCaseData(true, null, UnsubscribedBehavior.Resubscribe, "resubscribed", false, true).SetName("Member without confirmation, resubscribe: resubscribed");
        yield return new TestCaseData(false, false, UnsubscribedBehavior.Keep, "keptUnsubscribed", false, false).SetName("Unsubscribed and removed from the list, keep: kept, not added");
        yield return new TestCaseData(false, false, UnsubscribedBehavior.Resubscribe, "resubscribed", true, true).SetName("Unsubscribed and removed from the list, resubscribe: added again");
    }

    [TestCaseSource(nameof(ListCases))]
    public void List_Decision_Follows_Membership_Confirmation_And_Option(bool member, bool? approved, UnsubscribedBehavior option, string expected, bool writesMembership, bool writesConfirmation)
    {
        var context = ListContext(option);
        var state = new AudienceState(0, ContactId, HasEmail: true, IsMember: member, SubscriptionApproved: approved, SubscriptionDate: approved is null ? null : earlier, SubscriptionGuid: approved is null ? null : Guid.NewGuid());

        var result = Decide(context, state);

        string key = "list" + char.ToUpperInvariant(expected[0]) + expected[1..];
        var kind = expected == "resubscribed" ? AudienceWriteKind.ListResubscribed : AudienceWriteKind.ListAdded;
        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Single(), Is.EqualTo(new AudienceRowNote(0, expected, null)));
            Assert.That(Counts(result), Is.EqualTo(new Dictionary<string, long> { [key] = 1 }));
            Assert.That(result.Memberships, Is.EqualTo(writesMembership ? new[] { ContactId } : []));
            Assert.That(result.Subscriptions, Is.EqualTo(writesConfirmation ? new[] { new SubscriptionWrite(ContactId, WriteGuid(context, kind, ListId)) } : []));
            Assert.That(result.Agreements, Is.Empty);
        });
    }

    [Test]
    public void Defaults_Never_Change_An_Unsubscribed_Contact()
    {
        var options = new ImportOptions { RecipientListId = ListId };
        var context = Context(Email, [Email, FirstName], options);

        var result = Decide(context, new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: false, SubscriptionDate: earlier));

        Assert.Multiple(() =>
        {
            Assert.That(options.Unsubscribed, Is.EqualTo(UnsubscribedBehavior.Keep));
            Assert.That(result.Subscriptions, Is.Empty);
            Assert.That(result.Memberships, Is.Empty);
            Assert.That(result.Counts.Get(ImportAggregateKeys.ListKeptUnsubscribed), Is.EqualTo(1));
        });
    }

    [Test]
    public void Contact_Without_Email_Is_Not_Added_And_Not_Counted_As_Bounced()
    {
        var result = Decide(ListContext(), new AudienceState(0, ContactId, HasEmail: false, BounceHard: true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Single().List, Is.EqualTo("skippedNoEmail"));
            Assert.That(Counts(result), Is.EqualTo(new Dictionary<string, long> { [ImportAggregateKeys.ListSkippedNoEmail] = 1 }));
            Assert.That(result.Memberships, Is.Empty);
            Assert.That(result.Subscriptions, Is.Empty);
        });
    }

    private static IEnumerable<TestCaseData> BounceCases()
    {
        // hard, soft count, option, expected list code, bounce count key, written
        yield return new TestCaseData(true, 0, BouncedAddressBehavior.AddAndReport, "added;bouncedHard", ImportAggregateKeys.BouncedHard, true).SetName("Hard bounce, add and report");
        yield return new TestCaseData(true, 0, BouncedAddressBehavior.SkipAndReport, "skippedBounced;bouncedHard", ImportAggregateKeys.BouncedHard, false).SetName("Hard bounce, skip");
        yield return new TestCaseData(true, 9, BouncedAddressBehavior.SkipAndReport, "skippedBounced;bouncedHard", ImportAggregateKeys.BouncedHard, false).SetName("Hard and soft bounces count as hard");
        yield return new TestCaseData(false, 3, BouncedAddressBehavior.AddAndReport, "added;bouncedSoft", ImportAggregateKeys.BouncedSoftOverLimit, true).SetName("Soft bounces at the limit, add and report");
        yield return new TestCaseData(false, 4, BouncedAddressBehavior.SkipAndReport, "skippedBounced;bouncedSoft", ImportAggregateKeys.BouncedSoftOverLimit, false).SetName("Soft bounces over the limit, skip");
        yield return new TestCaseData(false, 2, BouncedAddressBehavior.SkipAndReport, "added", ImportAggregateKeys.BouncedSoftUnderLimit, true).SetName("Soft bounces under the limit are added also with skip");
        yield return new TestCaseData(false, 0, BouncedAddressBehavior.SkipAndReport, "added", null, true).SetName("No bounce");
    }

    [TestCaseSource(nameof(BounceCases))]
    public void Bounced_Addresses_Follow_The_Send_Time_Rule_And_The_Option(bool hard, int soft, BouncedAddressBehavior option, string expected, string? bounceKey, bool written)
    {
        var context = ListContext(bounced: option, softBounceLimit: 3);

        var result = Decide(context, new AudienceState(0, ContactId, HasEmail: true, BounceHard: hard, BounceSoftCount: soft));

        var expectedCounts = new Dictionary<string, long> { [written ? ImportAggregateKeys.ListAdded : ImportAggregateKeys.ListSkippedBounced] = 1 };
        if (bounceKey is not null)
        {
            expectedCounts[bounceKey] = 1;
        }

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Single().List, Is.EqualTo(expected));
            Assert.That(Counts(result), Is.EqualTo(expectedCounts));
            Assert.That(result.Memberships, Has.Count.EqualTo(written ? 1 : 0));
            Assert.That(result.Subscriptions, Has.Count.EqualTo(written ? 1 : 0));
        });
    }

    [Test]
    public void Bounce_Option_Skips_A_Resubscribe_But_Not_What_Needs_No_Write()
    {
        var context = ListContext(UnsubscribedBehavior.Resubscribe, BouncedAddressBehavior.SkipAndReport);

        var resubscribe = Decide(context, new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: false, SubscriptionDate: earlier, BounceHard: true));
        var subscribed = Decide(context, new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: true, SubscriptionDate: earlier, BounceHard: true));
        var kept = Decide(ListContext(UnsubscribedBehavior.Keep, BouncedAddressBehavior.SkipAndReport), new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: false, SubscriptionDate: earlier, BounceHard: true));

        Assert.Multiple(() =>
        {
            Assert.That(resubscribe.Rows.Single().List, Is.EqualTo("skippedBounced;bouncedHard"));
            Assert.That(resubscribe.Subscriptions, Is.Empty);
            Assert.That(subscribed.Rows.Single().List, Is.EqualTo("alreadySubscribed;bouncedHard"));
            Assert.That(kept.Rows.Single().List, Is.EqualTo("keptUnsubscribed;bouncedHard"));
            Assert.That(kept.Counts.Get(ImportAggregateKeys.BouncedHard), Is.EqualTo(1));
        });
    }

    [Test]
    public void Resubscribe_Is_Refused_When_The_Unsubscription_Is_Not_Older_Than_The_Import_Time()
    {
        var context = ListContext(UnsubscribedBehavior.Resubscribe);

        // A confirmation dated now would not be the newest one: inserting it would change nothing, on every run.
        var same = Decide(context, new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: false, SubscriptionDate: now));
        var later = Decide(context, new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: false, SubscriptionDate: now.AddMinutes(1)));
        var before = Decide(context, new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: false, SubscriptionDate: now.AddTicks(-1)));

        Assert.Multiple(() =>
        {
            Assert.That(same.Rows.Single().List, Is.EqualTo("keptUnsubscribed"));
            Assert.That(same.Subscriptions, Is.Empty);
            Assert.That(later.Rows.Single().List, Is.EqualTo("keptUnsubscribed"));
            Assert.That(before.Rows.Single().List, Is.EqualTo("resubscribed"));
        });
    }

    [TestCase(AudienceWriteKind.ListAdded, "added", ImportAggregateKeys.ListAdded)]
    [TestCase(AudienceWriteKind.ListResubscribed, "resubscribed", ImportAggregateKeys.ListResubscribed)]
    public void Repeated_Batch_Recognizes_Its_Own_Confirmation_And_Reports_The_Same_Outcome_Without_Writing(AudienceWriteKind kind, string expected, string key)
    {
        var context = ListContext(UnsubscribedBehavior.Resubscribe);
        var own = new AudienceState(0, ContactId, HasEmail: true, IsMember: true, SubscriptionApproved: true, SubscriptionDate: now, SubscriptionGuid: WriteGuid(context, kind, ListId));

        var result = Decide(context, own);
        var otherRow = AudiencePlanner.Decide(context, [Target() with { Row = Row + 1 }], [own], now);

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Single().List, Is.EqualTo(expected));
            Assert.That(Counts(result), Is.EqualTo(new Dictionary<string, long> { [key] = 1 }));
            Assert.That(result.Memberships, Is.Empty);
            Assert.That(result.Subscriptions, Is.Empty);
            Assert.That(otherRow.Rows.Single().List, Is.EqualTo("alreadySubscribed"), "A later row with the same key is not the row that wrote the confirmation.");
        });
    }

    // Consent: newest agreement x revoked option.

    private static IEnumerable<TestCaseData> ConsentCases()
    {
        // newest agreement revoked (null = none), option, expected, written
        yield return new TestCaseData(null, RevokedConsentBehavior.Keep, "recorded", true).SetName("No agreement: recorded");
        yield return new TestCaseData(null, RevokedConsentBehavior.ReAgree, "recorded", true).SetName("No agreement, re-agree: recorded");
        yield return new TestCaseData(false, RevokedConsentBehavior.Keep, "alreadyAgreed", false).SetName("Agreed: already agreed");
        yield return new TestCaseData(false, RevokedConsentBehavior.ReAgree, "alreadyAgreed", false).SetName("Agreed, re-agree: already agreed");
        yield return new TestCaseData(true, RevokedConsentBehavior.Keep, "keptRevoked", false).SetName("Revoked, keep: kept revoked");
        yield return new TestCaseData(true, RevokedConsentBehavior.ReAgree, "reAgreed", true).SetName("Revoked, re-agree: agreed again");
    }

    [TestCaseSource(nameof(ConsentCases))]
    public void Blanket_Consent_Follows_The_Newest_Agreement_And_The_Option(bool? revoked, RevokedConsentBehavior option, string expected, bool written)
    {
        var context = ConsentContext(revoked: option);
        var state = new AudienceState(0, ContactId, AgreementRevoked: revoked, AgreementTime: revoked is null ? null : earlier, AgreementGuid: revoked is null ? null : Guid.NewGuid());

        var result = Decide(context, state);

        string key = "consent" + char.ToUpperInvariant(expected[0]) + expected[1..];
        var kind = expected == "reAgreed" ? AudienceWriteKind.ConsentReAgreed : AudienceWriteKind.ConsentRecorded;
        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Single(), Is.EqualTo(new AudienceRowNote(0, null, expected)));
            Assert.That(Counts(result), Is.EqualTo(new Dictionary<string, long> { [key] = 1 }));
            Assert.That(result.Agreements, Is.EqualTo(written ? new[] { new AgreementWrite(ContactId, now, WriteGuid(context, kind, ConsentId)) } : []));
            Assert.That(result.Memberships, Is.Empty);
            Assert.That(result.Subscriptions, Is.Empty);
        });
    }

    [Test]
    public void Defaults_Never_Change_A_Revoked_Consent()
    {
        var options = new ImportOptions { Consent = ConsentMode.Blanket, ConsentId = ConsentId };

        var result = Decide(Context(Email, [Email, FirstName], options), new AudienceState(0, ContactId, AgreementRevoked: true, AgreementTime: earlier));

        Assert.Multiple(() =>
        {
            Assert.That(options.RevokedConsent, Is.EqualTo(RevokedConsentBehavior.Keep));
            Assert.That(result.Agreements, Is.Empty);
            Assert.That(result.Counts.Get(ImportAggregateKeys.ConsentKeptRevoked), Is.EqualTo(1));
        });
    }

    [Test]
    public void Blanket_Consent_Ignores_The_Consent_Columns()
    {
        var context = ConsentContext(mapping: new ImportConsentMapping { Agreed = "consent", Date = "date" });

        var result = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(false, earlier)));

        Assert.That(result.Agreements.Single(), Is.EqualTo(new AgreementWrite(ContactId, now, WriteGuid(context, AudienceWriteKind.ConsentRecorded, ConsentId))));
    }

    [Test]
    public void Consent_From_Columns_Is_Recorded_Only_For_Rows_That_Say_Yes_With_The_Row_Date_Or_The_Import_Time()
    {
        var context = ConsentContext(ConsentMode.BlanketWithColumnOverride);

        var dated = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(true, earlier)));
        var undated = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(true, null)));
        var no = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(false, earlier)));
        var missing = Decide(context, new AudienceState(0, ContactId), Target(ContactId));

        Assert.Multiple(() =>
        {
            Assert.That(dated.Agreements.Single().Time, Is.EqualTo(earlier));
            Assert.That(dated.Rows.Single().Consent, Is.EqualTo("recorded"));
            Assert.That(undated.Agreements.Single().Time, Is.EqualTo(now));
            Assert.That(no.Agreements, Is.Empty);
            Assert.That(no.Rows.Single().Consent, Is.EqualTo("notGiven"));
            Assert.That(Counts(no), Is.EqualTo(new Dictionary<string, long> { [ImportAggregateKeys.ConsentNotGiven] = 1 }));
            Assert.That(missing.Rows.Single().Consent, Is.EqualTo("notGiven"));
        });
    }

    [Test]
    public void Consent_Date_In_The_Future_Records_Nothing()
    {
        var context = ConsentContext(ConsentMode.BlanketWithColumnOverride);

        var future = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(true, now.AddSeconds(1))));
        var today = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(true, now)));
        var futureNo = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(false, now.AddDays(1))));

        Assert.Multiple(() =>
        {
            Assert.That(future.Agreements, Is.Empty);
            Assert.That(future.Rows.Single().Consent, Is.EqualTo("skippedFutureDate"));
            Assert.That(Counts(future), Is.EqualTo(new Dictionary<string, long> { [ImportAggregateKeys.ConsentSkippedFutureDate] = 1 }));
            Assert.That(today.Agreements.Single().Time, Is.EqualTo(now));
            Assert.That(futureNo.Rows.Single().Consent, Is.EqualTo("notGiven"));
        });
    }

    [Test]
    public void Rows_With_The_Same_Key_Give_Consent_Through_The_First_Row_That_Says_Yes_With_A_Usable_Date()
    {
        var context = ConsentContext(ConsentMode.BlanketWithColumnOverride);
        var first = earlier;
        var second = earlier.AddDays(1);

        var yesThenNo = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(true, first), new ImportRecordConsent(false, null)));
        var noThenYes = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(false, null), new ImportRecordConsent(true, second)));
        var futureThenYes = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(true, now.AddDays(1)), new ImportRecordConsent(true, second)));
        var yesTwice = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(true, first), new ImportRecordConsent(true, second)));

        Assert.Multiple(() =>
        {
            Assert.That(yesThenNo.Agreements.Single().Time, Is.EqualTo(first));
            Assert.That(noThenYes.Agreements.Single().Time, Is.EqualTo(second));
            Assert.That(futureThenYes.Agreements.Single().Time, Is.EqualTo(second));
            Assert.That(yesTwice.Agreements.Single().Time, Is.EqualTo(first));
            Assert.That(yesTwice.Counts.Get(ImportAggregateKeys.ConsentRecorded), Is.EqualTo(1), "One contact, one agreement.");
        });
    }

    [Test]
    public void Consent_Date_Column_Without_A_Consent_Given_Column_Counts_Every_Row_As_Given()
    {
        var context = ConsentContext(ConsentMode.BlanketWithColumnOverride, mapping: new ImportConsentMapping { Date = "consent date" });

        var result = Decide(context, new AudienceState(0, ContactId), Target(ContactId, new ImportRecordConsent(false, earlier)));

        Assert.That(result.Agreements.Single().Time, Is.EqualTo(earlier));
    }

    [Test]
    public void Backdated_Agreement_That_Is_Not_Newer_Than_The_Revocation_Is_Not_Written_And_Counts_As_Kept_Revoked()
    {
        var context = ConsentContext(ConsentMode.BlanketWithColumnOverride, RevokedConsentBehavior.ReAgree);
        var revokedAt = earlier;

        var older = Decide(context, new AudienceState(0, ContactId, AgreementRevoked: true, AgreementTime: revokedAt), Target(ContactId, new ImportRecordConsent(true, revokedAt.AddDays(-1))));
        var same = Decide(context, new AudienceState(0, ContactId, AgreementRevoked: true, AgreementTime: revokedAt), Target(ContactId, new ImportRecordConsent(true, revokedAt)));
        var newer = Decide(context, new AudienceState(0, ContactId, AgreementRevoked: true, AgreementTime: revokedAt), Target(ContactId, new ImportRecordConsent(true, revokedAt.AddDays(1))));

        Assert.Multiple(() =>
        {
            Assert.That(older.Agreements, Is.Empty, "The newest row decides, so the agreement would stay revoked and be inserted again by every run.");
            Assert.That(older.Rows.Single().Consent, Is.EqualTo("keptRevoked"));
            Assert.That(Counts(older), Is.EqualTo(new Dictionary<string, long> { [ImportAggregateKeys.ConsentKeptRevoked] = 1 }));
            Assert.That(same.Agreements, Is.Empty);
            Assert.That(newer.Agreements.Single(), Is.EqualTo(new AgreementWrite(ContactId, revokedAt.AddDays(1), WriteGuid(context, AudienceWriteKind.ConsentReAgreed, ConsentId))));
            Assert.That(newer.Rows.Single().Consent, Is.EqualTo("reAgreed"));
        });
    }

    [TestCase(AudienceWriteKind.ConsentRecorded, "recorded", ImportAggregateKeys.ConsentRecorded)]
    [TestCase(AudienceWriteKind.ConsentReAgreed, "reAgreed", ImportAggregateKeys.ConsentReAgreed)]
    public void Repeated_Batch_Recognizes_Its_Own_Agreement_And_Reports_The_Same_Outcome_Without_Writing(AudienceWriteKind kind, string expected, string key)
    {
        var context = ConsentContext(revoked: RevokedConsentBehavior.ReAgree);
        var own = new AudienceState(0, ContactId, AgreementRevoked: false, AgreementTime: now, AgreementGuid: WriteGuid(context, kind, ConsentId));

        var result = Decide(context, own);

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Single().Consent, Is.EqualTo(expected));
            Assert.That(Counts(result), Is.EqualTo(new Dictionary<string, long> { [key] = 1 }));
            Assert.That(result.Agreements, Is.Empty);
        });
    }

    // Both, analysis, several contacts.

    [Test]
    public void Contact_That_Does_Not_Exist_Yet_Is_Decided_And_Counted_But_Nothing_Is_Written()
    {
        var context = Context(Email, [Email, FirstName], new ImportOptions { RecipientListId = ListId, Consent = ConsentMode.Blanket, ConsentId = ConsentId });

        var result = Decide(context, new AudienceState(0, null, HasEmail: true), Target(null));

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Single(), Is.EqualTo(new AudienceRowNote(0, "added", "recorded")));
            Assert.That(Counts(result), Is.EqualTo(new Dictionary<string, long> { [ImportAggregateKeys.ListAdded] = 1, [ImportAggregateKeys.ConsentRecorded] = 1 }));
            Assert.That(result.Memberships, Is.Empty);
            Assert.That(result.Subscriptions, Is.Empty);
            Assert.That(result.Agreements, Is.Empty);
        });
    }

    [Test]
    public void Job_Without_List_And_Consent_Decides_Nothing()
    {
        var context = Context(Email, [Email, FirstName]);

        var result = Decide(context, new AudienceState(0, ContactId, HasEmail: true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Counts.Values, Is.Empty);
            Assert.That(result.Rows.Single(), Is.EqualTo(new AudienceRowNote(0, null, null)));
        });
    }

    [Test]
    public void Target_Without_A_State_Is_An_Unknown_Contact_And_A_Contact_Is_Decided_Once()
    {
        var context = ListContext();
        AudienceTarget[] targets = [Target(1) with { Position = 0 }, Target(2) with { Position = 1 }, Target(2) with { Position = 2 }];

        var result = AudiencePlanner.Decide(context, targets, [new AudienceState(1, 2, HasEmail: true), new AudienceState(2, 2, HasEmail: true), new AudienceState(99, 5, HasEmail: true)], now);

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows, Is.EqualTo(new[] { new AudienceRowNote(0, "skippedNoEmail", null), new AudienceRowNote(1, "added", null) }));
            Assert.That(result.Memberships, Is.EqualTo(new[] { 2 }));
        });
    }

    [Test]
    public void Annotate_Fills_The_Report_Columns_And_Joins_The_Outcomes_Of_Several_Contacts_Of_A_Row()
    {
        var result = new AudienceResult();
        result.Rows.Add(new AudienceRowNote(1, "added", "recorded"));
        result.Rows.Add(new AudienceRowNote(1, "alreadySubscribed", "recorded"));
        result.Rows.Add(new AudienceRowNote(1, "added", null));
        result.Rows.Add(new AudienceRowNote(7, "added", null));
        ImportRowResult[] rows = [new(1, ImportRowOutcome.Created, null, null, "a"), new(2, ImportRowOutcome.Updated, null, null, "b")];

        var annotated = result.Annotate(rows);

        Assert.Multiple(() =>
        {
            Assert.That(annotated[0], Is.EqualTo(rows[0]));
            Assert.That(annotated[1], Is.EqualTo(rows[1] with { List = "added|alreadySubscribed", Consent = "recorded" }));
            Assert.That(new AudienceResult().Annotate(rows), Is.SameAs(rows));
        });
    }

    [Test]
    public void Write_Guid_Is_Stable_And_Different_For_Every_Part_Of_Its_Identity()
    {
        var jobId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var guid = AudiencePlanner.CreateWriteGuid(jobId, 12, 41, 7, AudienceWriteKind.ListAdded);
        Guid[] others =
        [
            AudiencePlanner.CreateWriteGuid(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964fe"), 12, 41, 7, AudienceWriteKind.ListAdded),
            AudiencePlanner.CreateWriteGuid(jobId, 13, 41, 7, AudienceWriteKind.ListAdded),
            AudiencePlanner.CreateWriteGuid(jobId, 12, 42, 7, AudienceWriteKind.ListAdded),
            AudiencePlanner.CreateWriteGuid(jobId, 12, 41, 8, AudienceWriteKind.ListAdded),
            AudiencePlanner.CreateWriteGuid(jobId, 12, 41, 7, AudienceWriteKind.ListResubscribed),
            AudiencePlanner.CreateWriteGuid(jobId, 12, 41, 7, AudienceWriteKind.ConsentRecorded),
            AudiencePlanner.CreateWriteGuid(jobId, 12, 41, 7, AudienceWriteKind.ConsentReAgreed),
        ];

        Assert.Multiple(() =>
        {
            Assert.That(AudiencePlanner.CreateWriteGuid(jobId, 12, 41, 7, AudienceWriteKind.ListAdded), Is.EqualTo(guid));
            Assert.That(guid, Is.EqualTo(Guid.Parse("f0f79a21-9c6d-8c0e-a0c4-000000000000")).Or.Not.EqualTo(Guid.Empty));
            Assert.That(others, Has.None.EqualTo(guid));
            Assert.That(others.Distinct().Count(), Is.EqualTo(others.Length));
            Assert.That(guid.Version, Is.EqualTo(8));
        });
    }
}
