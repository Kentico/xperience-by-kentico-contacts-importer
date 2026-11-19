using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Codes of the <c>list</c> and <c>consent</c> columns of the row report. Codes only, never values from the file.
/// </summary>
internal static class ImportAudienceOutcomes
{
    /// <summary>The contact was added to the recipient list as subscribed.</summary>
    public const string ListAdded = "added";

    /// <summary>The contact is a subscribed member of the recipient list; nothing was written.</summary>
    public const string ListAlreadySubscribed = "alreadySubscribed";

    /// <summary>The contact unsubscribed from the recipient list (or is a member without a confirmation) and was left that way.</summary>
    public const string ListKeptUnsubscribed = "keptUnsubscribed";

    /// <summary>The contact had unsubscribed and got a new approved confirmation.</summary>
    public const string ListResubscribed = "resubscribed";

    /// <summary>The address is bounced and bounced addresses are not added; nothing was written.</summary>
    public const string ListSkippedBounced = "skippedBounced";

    /// <summary>The contact has no email address; nothing was written.</summary>
    public const string ListSkippedNoEmail = "skippedNoEmail";

    /// <summary>Appended (after <c>;</c>) when the address has a hard bounce: Xperience does not send to it.</summary>
    public const string BouncedHard = "bouncedHard";

    /// <summary>Appended (after <c>;</c>) when the address has reached the soft bounce limit: Xperience does not send to it.</summary>
    public const string BouncedSoft = "bouncedSoft";

    /// <summary>A consent agreement was recorded.</summary>
    public const string ConsentRecorded = "recorded";

    /// <summary>The contact's newest agreement is not revoked; nothing was written.</summary>
    public const string ConsentAlreadyAgreed = "alreadyAgreed";

    /// <summary>The contact revoked the consent and was left that way.</summary>
    public const string ConsentKeptRevoked = "keptRevoked";

    /// <summary>The contact had revoked the consent and got a new agreement.</summary>
    public const string ConsentReAgreed = "reAgreed";

    /// <summary>The consent column of the row does not say yes; nothing was written.</summary>
    public const string ConsentNotGiven = "notGiven";

    /// <summary>The consent date of the row is in the future; nothing was written.</summary>
    public const string ConsentSkippedFutureDate = "skippedFutureDate";

    /// <summary>Separates the outcomes of several contacts of one row (a key with several matches that are all updated).</summary>
    public const char ContactSeparator = '|';

    /// <summary>Separates the list outcome from the bounce note.</summary>
    public const char NoteSeparator = ';';
}

/// <summary>
/// A contact a row resolves to: the recipient list and consent behaviors apply to it.
/// </summary>
/// <param name="ContactId">Existing contact; <c>null</c> for a contact the batch creates.</param>
/// <param name="CreateIndex">Index in <see cref="BatchPlan.Creates"/> for a contact the batch creates, otherwise -1.</param>
/// <param name="Row">Source file row number of the last row with the contact's key; part of the identity of what the import writes for it.</param>
/// <param name="Position">Record position of that row in the batch; its report line gets the outcome.</param>
/// <param name="HasPlannedEmail">The batch decides the contact's email (a new contact, or the email field is written).</param>
/// <param name="PlannedEmail">Email the contact has after the batch when <paramref name="HasPlannedEmail"/>; <c>null</c> = none.</param>
/// <param name="Consents">Consent columns of the rows with the contact's key, in file order; empty without consent columns.</param>
internal sealed record AudienceTarget(int? ContactId, int CreateIndex, long Row, int Position, bool HasPlannedEmail, string? PlannedEmail, IReadOnlyList<ImportRecordConsent?> Consents);

/// <summary>
/// What the database says about a target right before the import decides about it.
/// </summary>
/// <param name="Index">Index of the target in the list that was asked about.</param>
/// <param name="ContactId">Contact ID; <c>null</c> for a contact the analysis would create.</param>
/// <param name="HasEmail">The contact has (analysis: will have) a non-empty email address.</param>
/// <param name="IsMember">The contact is a member of the recipient list.</param>
/// <param name="SubscriptionApproved">Whether the newest subscription confirmation for the list is approved; <c>null</c> = no confirmation.</param>
/// <param name="SubscriptionDate">Date of that confirmation.</param>
/// <param name="SubscriptionGuid">GUID of that confirmation.</param>
/// <param name="BounceHard">The address has a hard bounce.</param>
/// <param name="BounceSoftCount">Soft bounce count of the address.</param>
/// <param name="AgreementRevoked">Whether the newest agreement with the consent is a revocation; <c>null</c> = no agreement.</param>
/// <param name="AgreementTime">Time of that agreement.</param>
/// <param name="AgreementGuid">GUID of that agreement.</param>
internal sealed record AudienceState(
    int Index,
    int? ContactId = null,
    bool HasEmail = false,
    bool IsMember = false,
    bool? SubscriptionApproved = null,
    DateTime? SubscriptionDate = null,
    Guid? SubscriptionGuid = null,
    bool BounceHard = false,
    int BounceSoftCount = 0,
    bool? AgreementRevoked = null,
    DateTime? AgreementTime = null,
    Guid? AgreementGuid = null);

/// <param name="ContactId">Contact that gets a new approved subscription confirmation.</param>
/// <param name="Guid">GUID of the confirmation (<see cref="AudiencePlanner.CreateWriteGuid"/>).</param>
internal readonly record struct SubscriptionWrite(int ContactId, Guid Guid);

/// <param name="ContactId">Contact that gets a consent agreement.</param>
/// <param name="Time">Agreement time: the import time, or the consent date of the row.</param>
/// <param name="Guid">GUID of the agreement (<see cref="AudiencePlanner.CreateWriteGuid"/>).</param>
internal readonly record struct AgreementWrite(int ContactId, DateTime Time, Guid Guid);

/// <param name="Position">Record position in the batch.</param>
/// <param name="List">Value of the <c>list</c> report column.</param>
/// <param name="Consent">Value of the <c>consent</c> report column.</param>
internal readonly record struct AudienceRowNote(int Position, string? List, string? Consent);

/// <summary>
/// Decisions about the recipient list and consent for the contacts of one batch: what to insert, the counts and the
/// report columns. In the analysis the same decisions are made and nothing is inserted.
/// </summary>
internal sealed class AudienceResult
{
    /// <summary>Contacts that become members of the recipient list.</summary>
    public List<int> Memberships { get; } = [];

    /// <summary>Approved subscription confirmations to insert.</summary>
    public List<SubscriptionWrite> Subscriptions { get; } = [];

    /// <summary>Consent agreements to insert.</summary>
    public List<AgreementWrite> Agreements { get; } = [];

    public ImportCounts Counts { get; } = new();

    public List<AudienceRowNote> Rows { get; } = [];

    /// <summary>
    /// Returns <paramref name="rows"/> with the list and consent columns filled in.
    /// </summary>
    public IReadOnlyList<ImportRowResult> Annotate(IReadOnlyList<ImportRowResult> rows)
    {
        if (Rows.Count == 0)
        {
            return rows;
        }

        ImportRowResult[] annotated = [.. rows];
        foreach (var note in Rows)
        {
            if (note.Position < 0 || note.Position >= annotated.Length)
            {
                continue;
            }

            var row = annotated[note.Position];
            annotated[note.Position] = row with { List = Join(row.List, note.List), Consent = Join(row.Consent, note.Consent) };
        }

        return annotated;
    }

    private static string? Join(string? current, string? added)
    {
        if (added is null)
        {
            return current;
        }

        return current is null || current.Split(ImportAudienceOutcomes.ContactSeparator).Contains(added, StringComparer.Ordinal)
            ? current ?? added
            : current + ImportAudienceOutcomes.ContactSeparator + added;
    }
}

/// <summary>
/// What a row written by the import is, as part of its GUID.
/// </summary>
internal enum AudienceWriteKind : byte
{
    ListAdded = 1,
    ListResubscribed = 2,
    ConsentRecorded = 3,
    ConsentReAgreed = 4,
}

/// <summary>
/// Pure decision logic for recipient list subscriptions and consent agreements (PLAN §2, §8, §9). The same code runs
/// for the analysis and for the import; only the inserts differ.
/// </summary>
/// <remarks>
/// <para>
/// Nothing changes a subscription or consent state unless the options say so: by default an unsubscribed contact
/// stays unsubscribed and a revoked consent stays revoked. A contact counts as unsubscribed when the newest
/// confirmation for the list is not approved, whether or not it still is a member, and when it is a member without
/// any confirmation.
/// </para>
/// <para>
/// Every decision can be repeated: a batch that is processed again (the application stopped between the database
/// commit and the checkpoint) finds what it wrote and inserts nothing twice. Confirmations and agreements get a GUID
/// computed from the job, the row, the contact and what the row is; the second run recognizes its own row by it and
/// reports the same outcome as the first run.
/// </para>
/// </remarks>
internal static class AudiencePlanner
{
    /// <summary>
    /// Decides about every target.
    /// </summary>
    /// <param name="context">Job being processed.</param>
    /// <param name="targets">Contacts the rows of the batch resolve to.</param>
    /// <param name="states">Database state of the targets, any order; a target without a state is treated as an unknown contact.</param>
    /// <param name="now">Import time (application local time, as Xperience stores confirmation and agreement times).</param>
    public static AudienceResult Decide(ImportProcessingContext context, IReadOnlyList<AudienceTarget> targets, IReadOnlyList<AudienceState> states, DateTime now)
    {
        var result = new AudienceResult();
        var statesByIndex = new AudienceState?[targets.Count];
        foreach (var state in states)
        {
            if (state.Index >= 0 && state.Index < targets.Count)
            {
                statesByIndex[state.Index] = state;
            }
        }

        var decided = new HashSet<int>();
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var state = statesByIndex[i] ?? new AudienceState(i);

            // One contact is decided about once per batch, whatever the rows say.
            if (state.ContactId is { } contactId && !decided.Add(contactId))
            {
                continue;
            }

            string? list = context.HasRecipientList ? DecideList(context, target, state, now, result) : null;
            string? consent = context.RecordsConsent ? DecideConsent(context, target, state, now, result) : null;
            result.Rows.Add(new AudienceRowNote(target.Position, list, consent));
        }

        return result;
    }

    /// <summary>
    /// GUID of a confirmation or agreement the import writes: the same for the same job, row, contact, list or
    /// consent, and kind of write, and different from everything else. Version 8 (custom) of RFC 9562.
    /// </summary>
    public static Guid CreateWriteGuid(Guid jobId, long row, int contactId, int targetId, AudienceWriteKind kind)
    {
        Span<byte> input = stackalloc byte[16 + sizeof(long) + sizeof(int) + sizeof(int) + 1];
        jobId.TryWriteBytes(input, bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], row);
        BinaryPrimitives.WriteInt32BigEndian(input[24..], contactId);
        BinaryPrimitives.WriteInt32BigEndian(input[28..], targetId);
        input[32] = (byte)kind;

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16], bigEndian: true);
    }

    private static string DecideList(ImportProcessingContext context, AudienceTarget target, AudienceState state, DateTime now, AudienceResult result)
    {
        var counts = result.Counts;
        if (!state.HasEmail)
        {
            // Xperience never sends to a member without an email address; such a contact is not added.
            counts.Add(ImportAggregateKeys.ListSkippedNoEmail);

            return ImportAudienceOutcomes.ListSkippedNoEmail;
        }

        // The send-time rule of Xperience: a hard bounce, or as many soft bounces as the limit.
        bool softOverLimit = !state.BounceHard && state.BounceSoftCount >= context.Audience.SoftBounceLimit;
        bool bounced = state.BounceHard || softOverLimit;
        if (state.BounceHard)
        {
            counts.Add(ImportAggregateKeys.BouncedHard);
        }
        else if (softOverLimit)
        {
            counts.Add(ImportAggregateKeys.BouncedSoftOverLimit);
        }
        else if (state.BounceSoftCount > 0)
        {
            counts.Add(ImportAggregateKeys.BouncedSoftUnderLimit);
        }

        string outcome = DecideSubscription(context, target, state, now, bounced, result);
        counts.Add(outcome switch
        {
            ImportAudienceOutcomes.ListAdded => ImportAggregateKeys.ListAdded,
            ImportAudienceOutcomes.ListAlreadySubscribed => ImportAggregateKeys.ListAlreadySubscribed,
            ImportAudienceOutcomes.ListResubscribed => ImportAggregateKeys.ListResubscribed,
            ImportAudienceOutcomes.ListSkippedBounced => ImportAggregateKeys.ListSkippedBounced,
            _ => ImportAggregateKeys.ListKeptUnsubscribed,
        });

        if (!bounced)
        {
            return outcome;
        }

        return outcome + ImportAudienceOutcomes.NoteSeparator + (state.BounceHard ? ImportAudienceOutcomes.BouncedHard : ImportAudienceOutcomes.BouncedSoft);
    }

    private static string DecideSubscription(ImportProcessingContext context, AudienceTarget target, AudienceState state, DateTime now, bool bounced, AudienceResult result)
    {
        int listId = context.Audience.RecipientListId!.Value;
        Guid? addedGuid = state.ContactId is { } id ? CreateWriteGuid(context.JobId, target.Row, id, listId, AudienceWriteKind.ListAdded) : null;
        Guid? resubscribedGuid = state.ContactId is { } id2 ? CreateWriteGuid(context.JobId, target.Row, id2, listId, AudienceWriteKind.ListResubscribed) : null;

        if (state.IsMember && state.SubscriptionApproved == true)
        {
            // This batch ran before and wrote the confirmation: same outcome as then, nothing to write.
            if (state.SubscriptionGuid is { } newest && newest == addedGuid)
            {
                return ImportAudienceOutcomes.ListAdded;
            }

            return state.SubscriptionGuid is { } guid && guid == resubscribedGuid
                ? ImportAudienceOutcomes.ListResubscribed
                : ImportAudienceOutcomes.ListAlreadySubscribed;
        }

        // Unsubscribed: the newest confirmation is not approved, also when the contact was removed from the list
        // afterwards; or a member without any confirmation (the administration shows it as unsubscribed).
        bool unsubscribed = state.SubscriptionApproved == false || (state.IsMember && state.SubscriptionApproved is null);
        if (unsubscribed)
        {
            // A new confirmation dated now would not be the newest: the contact stays unsubscribed.
            bool wouldNotCount = state.SubscriptionDate is { } date && date >= now;
            if (context.Options.Unsubscribed != UnsubscribedBehavior.Resubscribe || wouldNotCount)
            {
                return ImportAudienceOutcomes.ListKeptUnsubscribed;
            }
        }

        if (bounced && context.Options.BouncedAddress == BouncedAddressBehavior.SkipAndReport)
        {
            return ImportAudienceOutcomes.ListSkippedBounced;
        }

        if (state.ContactId is { } contactId)
        {
            if (!state.IsMember)
            {
                result.Memberships.Add(contactId);
            }

            result.Subscriptions.Add(new SubscriptionWrite(contactId, (unsubscribed ? resubscribedGuid : addedGuid)!.Value));
        }

        return unsubscribed ? ImportAudienceOutcomes.ListResubscribed : ImportAudienceOutcomes.ListAdded;
    }

    private static string DecideConsent(ImportProcessingContext context, AudienceTarget target, AudienceState state, DateTime now, AudienceResult result)
    {
        var counts = result.Counts;
        var time = now;
        if (context.ConsentFromColumns && !TryGetConsentTime(context, target, now, out time, out bool futureDate))
        {
            counts.Add(futureDate ? ImportAggregateKeys.ConsentSkippedFutureDate : ImportAggregateKeys.ConsentNotGiven);

            return futureDate ? ImportAudienceOutcomes.ConsentSkippedFutureDate : ImportAudienceOutcomes.ConsentNotGiven;
        }

        int consentId = context.Audience.ConsentId!.Value;
        Guid? recordedGuid = state.ContactId is { } id ? CreateWriteGuid(context.JobId, target.Row, id, consentId, AudienceWriteKind.ConsentRecorded) : null;
        Guid? reAgreedGuid = state.ContactId is { } id2 ? CreateWriteGuid(context.JobId, target.Row, id2, consentId, AudienceWriteKind.ConsentReAgreed) : null;

        if (state.AgreementRevoked == false)
        {
            // This batch ran before and wrote the agreement: same outcome as then, nothing to write.
            if (state.AgreementGuid is { } newest && newest == recordedGuid)
            {
                counts.Add(ImportAggregateKeys.ConsentRecorded);

                return ImportAudienceOutcomes.ConsentRecorded;
            }

            if (state.AgreementGuid is { } guid && guid == reAgreedGuid)
            {
                counts.Add(ImportAggregateKeys.ConsentReAgreed);

                return ImportAudienceOutcomes.ConsentReAgreed;
            }

            counts.Add(ImportAggregateKeys.ConsentAlreadyAgreed);

            return ImportAudienceOutcomes.ConsentAlreadyAgreed;
        }

        bool revoked = state.AgreementRevoked == true;
        if (revoked)
        {
            // The newest row decides: an agreement that is not newer than the revocation (a consent date before it)
            // would change nothing, so it is not written and the consent stays revoked.
            bool wouldNotCount = state.AgreementTime is { } revokedAt && revokedAt >= time;
            if (context.Options.RevokedConsent != RevokedConsentBehavior.ReAgree || wouldNotCount)
            {
                counts.Add(ImportAggregateKeys.ConsentKeptRevoked);

                return ImportAudienceOutcomes.ConsentKeptRevoked;
            }
        }

        if (state.ContactId is { } contactId)
        {
            result.Agreements.Add(new AgreementWrite(contactId, time, (revoked ? reAgreedGuid : recordedGuid)!.Value));
        }

        counts.Add(revoked ? ImportAggregateKeys.ConsentReAgreed : ImportAggregateKeys.ConsentRecorded);

        return revoked ? ImportAudienceOutcomes.ConsentReAgreed : ImportAudienceOutcomes.ConsentRecorded;
    }

    /// <summary>
    /// The first row of the contact's key that says consent was given with a date that is not in the future decides,
    /// as it would when the rows were in different batches. Without a date the agreement gets the import time.
    /// </summary>
    private static bool TryGetConsentTime(ImportProcessingContext context, AudienceTarget target, DateTime now, out DateTime time, out bool futureDate)
    {
        time = now;
        futureDate = false;
        foreach (var consent in target.Consents)
        {
            if (consent is null || (context.ConsentAgreedMapped && !consent.Agreed))
            {
                continue;
            }

            if (consent.Date is { } date && date > now)
            {
                futureDate = true;
                continue;
            }

            time = consent.Date ?? now;
            futureDate = false;

            return true;
        }

        return false;
    }
}
