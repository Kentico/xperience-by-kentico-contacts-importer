using System.Data;
using System.Globalization;

using CMS.ContactManagement;
using CMS.DataEngine;
using CMS.DataProtection;
using CMS.EmailMarketing;

using Microsoft.Data.SqlClient;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Database access of the import: key lookups and the writes of one batch.
/// </summary>
internal interface IContactImportRepository
{
    /// <summary>
    /// Finds existing contacts whose key field equals one of <paramref name="keys"/> (database comparison rules).
    /// <see cref="ContactMatch.GroupIndex"/> is the position of the matched key in <paramref name="keys"/>.
    /// </summary>
    public IReadOnlyList<ContactMatch> FindMatches(ImportProcessingContext context, IReadOnlyList<object> keys);

    /// <summary>
    /// For text keys the database treats as equal to an earlier key in <paramref name="keys"/>: the position of the
    /// key and of the first equal key.
    /// </summary>
    public IReadOnlyList<(int Index, int EquivalentIndex)> FindEquivalentKeys(IReadOnlyList<string> keys);

    /// <summary>
    /// For the values of a reference field (<see cref="ImportField.Reference"/>): the positions in
    /// <paramref name="values"/> of those that do not exist in the referenced table. One set-based query.
    /// </summary>
    public IReadOnlyList<int> FindMissingReferences(ImportField field, IReadOnlyList<object> values);

    /// <summary>
    /// Reads the recipient list membership, newest subscription confirmation, bounce state and newest consent
    /// agreement of the targets, as far as the job uses them. One set-based query; writes nothing.
    /// </summary>
    /// <param name="context">Job being processed.</param>
    /// <param name="targets">Contacts to read; <see cref="AudienceState.Index"/> is the position in this list.</param>
    /// <param name="createdContactIds">
    /// IDs of the contacts the batch created, by <see cref="AudienceTarget.CreateIndex"/>; <c>null</c> when they are
    /// not created (analysis): such targets have no state besides their planned email and its bounces.
    /// </param>
    /// <param name="usePlannedEmails">
    /// Use the email the batch plans for a contact (<see cref="AudienceTarget.PlannedEmail"/>) instead of the stored
    /// one. For the analysis; the import reads the emails it has written.
    /// </param>
    public IReadOnlyList<AudienceState> FindAudienceStates(ImportProcessingContext context, IReadOnlyList<AudienceTarget> targets, IReadOnlyList<int>? createdContactIds = null, bool usePlannedEmails = true);

    /// <summary>
    /// Writes one batch in one transaction: creates and updates the contacts and, when <paramref name="audience"/>
    /// is given, adds its contacts to the recipient list and records their consent agreements.
    /// </summary>
    /// <param name="context">Job being processed.</param>
    /// <param name="creates">Contacts to create.</param>
    /// <param name="updates">Contacts to update with the merge rule.</param>
    /// <param name="now">
    /// Creation / last modification time and date of new subscription confirmations (application local time, as
    /// Xperience does).
    /// </param>
    /// <param name="audience">
    /// Contacts the recipient list and consent behaviors apply to and the decision about them. The states are read
    /// inside the transaction, after the contacts are written, so the check for existing memberships, confirmations
    /// and agreements and the inserts cannot be separated by another call of the same job.
    /// </param>
    /// <returns>The decisions about <paramref name="audience"/>; <c>null</c> without it.</returns>
    public AudienceResult? Write(ImportProcessingContext context, IReadOnlyList<ContactCreate> creates, IReadOnlyList<ContactUpdate> updates, DateTime now, AudienceWrite? audience = null);

    /// <summary>
    /// Takes the database lock that lets one call at a time import a job, waiting up to <paramref name="timeout"/>.
    /// Returns <c>null</c> when another call still holds it. While the lock is held, the lookups and writes of the
    /// calling thread use the connection that owns the lock, so the lock cannot be lost without them failing too.
    /// Dispose releases it.
    /// </summary>
    public IDisposable? TryLockJob(Guid jobId, TimeSpan timeout);
}

/// <param name="Targets">Contacts the rows of the batch resolve to.</param>
/// <param name="Decide">Decides from the database state of the targets what to insert (<see cref="AudiencePlanner.Decide"/>).</param>
internal sealed record AudienceWrite(IReadOnlyList<AudienceTarget> Targets, Func<IReadOnlyList<AudienceState>, AudienceResult> Decide);

/// <summary>
/// <see cref="IContactImportRepository"/> over the Xperience database. Reads and the batch update are raw set-based
/// SQL through <see cref="ConnectionHelper"/> (PLAN D22: fast, no per-row events; allowed for bulk operations by the
/// stable customization guidelines and covered by integration tests); creates use <c>BulkInsert</c>, also for recipient
/// list members, subscription confirmations and consent agreements (the same columns Xperience sets when a visitor
/// confirms a subscription or agrees with a consent).
/// </summary>
internal sealed class ContactImportRepository(
    IInfoProvider<ContactInfo> contactProvider,
    IInfoProvider<ContactGroupMemberInfo>? memberProvider = null,
    IInfoProvider<EmailSubscriptionConfirmationInfo>? subscriptionProvider = null,
    IInfoProvider<ConsentAgreementInfo>? agreementProvider = null) : IContactImportRepository
{
    private readonly Lazy<ContactTable> table = new(GetContactTable);
    private readonly Lazy<AudienceTables> audienceTables = new(GetAudienceTables);
    private readonly IInfoProvider<ContactGroupMemberInfo> memberProvider = memberProvider ?? Provider<ContactGroupMemberInfo>.Instance;
    private readonly IInfoProvider<EmailSubscriptionConfirmationInfo> subscriptionProvider = subscriptionProvider ?? Provider<EmailSubscriptionConfirmationInfo>.Instance;
    private readonly IInfoProvider<ConsentAgreementInfo> agreementProvider = agreementProvider ?? Provider<ConsentAgreementInfo>.Instance;

    public IReadOnlyList<ContactMatch> FindMatches(ImportProcessingContext context, IReadOnlyList<object> keys)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var parameters = new QueryDataParameters { { ContactSqlBuilder.KeysParameter, ContactSqlBuilder.BuildKeysJson(context.KeyField, keys) } };
        using var data = ConnectionHelper.ExecuteQuery(ContactSqlBuilder.BuildLookupSql(table.Value, context), parameters, QueryTypeEnum.SQLQuery);

        var matches = new List<ContactMatch>();
        if (data.Tables.Count == 0)
        {
            return matches;
        }

        int fieldCount = context.Fields.Count;
        foreach (DataRow row in data.Tables[0].Rows)
        {
            object?[] values = new object?[fieldCount];
            foreach (int index in context.Fields.Select(field => field.Index).Where(index => index != context.KeyIndex))
            {
                object value = row[ContactSqlBuilder.ValueColumn(index)];
                values[index] = value is DBNull ? null : value;
            }

            object lastModified = row[ContactSqlBuilder.LastModifiedColumn];
            matches.Add(new ContactMatch(
                Convert.ToInt32(row[ContactSqlBuilder.GroupIndexColumn], CultureInfo.InvariantCulture),
                Convert.ToInt32(row[ContactSqlBuilder.ContactIdColumn], CultureInfo.InvariantCulture),
                lastModified is DBNull ? DateTime.MinValue : Convert.ToDateTime(lastModified, CultureInfo.InvariantCulture),
                values));
        }

        return matches;
    }

    public IReadOnlyList<(int Index, int EquivalentIndex)> FindEquivalentKeys(IReadOnlyList<string> keys)
    {
        if (keys.Count < 2)
        {
            return [];
        }

        var keyField = new ImportField(0, "k", ImportValueKind.Text);
        var parameters = new QueryDataParameters { { ContactSqlBuilder.KeysParameter, ContactSqlBuilder.BuildKeysJson(keyField, [.. keys]) } };
        using var data = ConnectionHelper.ExecuteQuery(ContactSqlBuilder.BuildEquivalentKeysSql(), parameters, QueryTypeEnum.SQLQuery);

        var pairs = new List<(int, int)>();
        if (data.Tables.Count == 0)
        {
            return pairs;
        }

        foreach (DataRow row in data.Tables[0].Rows)
        {
            pairs.Add((
                Convert.ToInt32(row[ContactSqlBuilder.GroupIndexColumn], CultureInfo.InvariantCulture),
                Convert.ToInt32(row[ContactSqlBuilder.EquivalentIndexColumn], CultureInfo.InvariantCulture)));
        }

        return pairs;
    }

    public IReadOnlyList<int> FindMissingReferences(ImportField field, IReadOnlyList<object> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var parameters = new QueryDataParameters { { ContactSqlBuilder.KeysParameter, ContactSqlBuilder.BuildKeysJson(field, values) } };
        using var data = ConnectionHelper.ExecuteQuery(ContactSqlBuilder.BuildMissingReferencesSql(field), parameters, QueryTypeEnum.SQLQuery);

        var missing = new List<int>();
        if (data.Tables.Count == 0)
        {
            return missing;
        }

        foreach (DataRow row in data.Tables[0].Rows)
        {
            missing.Add(Convert.ToInt32(row[ContactSqlBuilder.GroupIndexColumn], CultureInfo.InvariantCulture));
        }

        return missing;
    }

    public IReadOnlyList<AudienceState> FindAudienceStates(ImportProcessingContext context, IReadOnlyList<AudienceTarget> targets, IReadOnlyList<int>? createdContactIds = null, bool usePlannedEmails = true)
    {
        if (targets.Count == 0 || !context.HasAudience)
        {
            return [];
        }

        bool list = context.HasRecipientList;
        bool consent = context.RecordsConsent;
        var parameters = new QueryDataParameters
        {
            { AudienceSqlBuilder.TargetsParameter, AudienceSqlBuilder.BuildTargetsJson(targets, createdContactIds) },
            { AudienceSqlBuilder.PlannedParameter, usePlannedEmails },
        };
        if (list)
        {
            parameters.Add(AudienceSqlBuilder.ListIdParameter, context.Audience.RecipientListId!.Value);
        }

        if (consent)
        {
            parameters.Add(AudienceSqlBuilder.ConsentIdParameter, context.Audience.ConsentId!.Value);
        }

        using var data = ConnectionHelper.ExecuteQuery(AudienceSqlBuilder.BuildStateSql(audienceTables.Value, list, consent), parameters, QueryTypeEnum.SQLQuery);

        var states = new List<AudienceState>(targets.Count);
        if (data.Tables.Count == 0)
        {
            return states;
        }

        foreach (DataRow row in data.Tables[0].Rows)
        {
            states.Add(new AudienceState(
                Convert.ToInt32(row[AudienceSqlBuilder.IndexColumn], CultureInfo.InvariantCulture),
                Read<int>(row, AudienceSqlBuilder.ContactIdColumn),
                Read<bool>(row, AudienceSqlBuilder.HasEmailColumn) ?? false,
                list && (Read<bool>(row, AudienceSqlBuilder.MemberColumn) ?? false),
                list ? Read<bool>(row, AudienceSqlBuilder.SubscriptionApprovedColumn) : null,
                list ? Read<DateTime>(row, AudienceSqlBuilder.SubscriptionDateColumn) : null,
                list ? Read<Guid>(row, AudienceSqlBuilder.SubscriptionGuidColumn) : null,
                list && (Read<bool>(row, AudienceSqlBuilder.BounceHardColumn) ?? false),
                list ? Read<int>(row, AudienceSqlBuilder.BounceSoftColumn) ?? 0 : 0,
                consent ? Read<bool>(row, AudienceSqlBuilder.AgreementRevokedColumn) : null,
                consent ? Read<DateTime>(row, AudienceSqlBuilder.AgreementTimeColumn) : null,
                consent ? Read<Guid>(row, AudienceSqlBuilder.AgreementGuidColumn) : null));
        }

        return states;
    }

    public AudienceResult? Write(ImportProcessingContext context, IReadOnlyList<ContactCreate> creates, IReadOnlyList<ContactUpdate> updates, DateTime now, AudienceWrite? audience = null)
    {
        bool hasAudience = audience is { Targets.Count: > 0 } && context.HasAudience;
        if (creates.Count == 0 && updates.Count == 0 && !hasAudience)
        {
            return null;
        }

        using var transaction = new CMSTransactionScope();

        // The contacts get their GUIDs here instead of from BulkInsert, so their new IDs can be read back.
        Guid[]? createdGuids = hasAudience && creates.Count > 0 ? [.. creates.Select(_ => Guid.NewGuid())] : null;
        if (creates.Count > 0)
        {
            contactProvider.BulkInsert(creates.Select((create, index) => ToContact(context, create, now, createdGuids?[index])));
        }

        if (updates.Count > 0)
        {
            var parameters = new QueryDataParameters
            {
                { ContactSqlBuilder.RowsParameter, ContactSqlBuilder.BuildUpdateJson(context, updates) },
                { ContactSqlBuilder.ClearEmptyParameter, context.ClearEmptyValues },
                { ContactSqlBuilder.NowParameter, now },
            };
            if (context.StampUpdatedContacts)
            {
                parameters.Add(ContactSqlBuilder.SourceParameter, context.SourceValue);
            }

            ConnectionHelper.ExecuteNonQuery(ContactSqlBuilder.BuildUpdateSql(table.Value, context), parameters, QueryTypeEnum.SQLQuery);
        }

        AudienceResult? result = null;
        if (hasAudience)
        {
            int[] createdIds = createdGuids is null ? [] : FindCreatedContactIds(createdGuids);
            result = audience!.Decide(FindAudienceStates(context, audience.Targets, createdIds, usePlannedEmails: false));
            WriteAudience(context, result, now);
        }

        transaction.Commit();

        return result;
    }

    /// <summary>
    /// IDs of the contacts just inserted with <paramref name="guids"/>, in the same order.
    /// </summary>
    private int[] FindCreatedContactIds(Guid[] guids)
    {
        var guidField = new ImportField(0, "g", ImportValueKind.Guid);
        var parameters = new QueryDataParameters { { ContactSqlBuilder.KeysParameter, ContactSqlBuilder.BuildKeysJson(guidField, [.. guids.Cast<object>()]) } };
        using var data = ConnectionHelper.ExecuteQuery(AudienceSqlBuilder.BuildCreatedContactsSql(audienceTables.Value), parameters, QueryTypeEnum.SQLQuery);

        int[] ids = new int[guids.Length];
        int found = 0;
        if (data.Tables.Count > 0)
        {
            foreach (DataRow row in data.Tables[0].Rows)
            {
                ids[Convert.ToInt32(row[AudienceSqlBuilder.IndexColumn], CultureInfo.InvariantCulture)] = Convert.ToInt32(row[AudienceSqlBuilder.ContactIdColumn], CultureInfo.InvariantCulture);
                found++;
            }
        }

        // Never guess which contact a membership or agreement belongs to: the transaction is rolled back.
        return found == guids.Length && Array.TrueForAll(ids, id => id > 0)
            ? ids
            : throw new InvalidOperationException("The contacts created by the batch could not be read back.");
    }

    /// <summary>
    /// Inserts what was decided: the columns Xperience sets when a contact is added to a recipient list manually
    /// (member of type contact, added manually), confirms a subscription (approved, dated now) or agrees with a
    /// consent (not revoked, current consent hash). No emails are sent and no per-object events run.
    /// </summary>
    private void WriteAudience(ImportProcessingContext context, AudienceResult result, DateTime now)
    {
        if (result.Memberships.Count > 0 && context.Audience.RecipientListId is { } memberListId)
        {
            memberProvider.BulkInsert(result.Memberships.Select(contactId => ToManualMember(memberListId, contactId)), RowLockBulkInsert(keepNulls: true));
        }

        if (result.Subscriptions.Count > 0 && context.Audience.RecipientListId is { } listId)
        {
            subscriptionProvider.BulkInsert(
                result.Subscriptions.Select(subscription => new EmailSubscriptionConfirmationInfo
                {
                    EmailSubscriptionConfirmationContactID = subscription.ContactId,
                    EmailSubscriptionConfirmationRecipientListID = listId,
                    EmailSubscriptionConfirmationDate = now,
                    EmailSubscriptionConfirmationIsApproved = true,
                    EmailSubscriptionConfirmationGUID = subscription.Guid,
                }),
                RowLockBulkInsert());
        }

        if (result.Agreements.Count > 0 && context.Audience.ConsentId is { } consentId)
        {
            agreementProvider.BulkInsert(
                result.Agreements.Select(agreement => new ConsentAgreementInfo
                {
                    ConsentAgreementContactID = agreement.ContactId,
                    ConsentAgreementConsentID = consentId,
                    ConsentAgreementTime = agreement.Time,
                    ConsentAgreementConsentHash = context.Audience.ConsentHash,
                    ConsentAgreementRevoked = false,
                    ConsentAgreementGuid = agreement.Guid,
                }),
                RowLockBulkInsert());
        }
    }

    /// <summary>
    /// A member of type contact added manually: the columns <c>ContactGroupMemberInfoProviderExtensions.Add(..., Manual)</c>
    /// writes. The other origin flags stay NULL there, so they are NULL here too (with KeepNulls; otherwise the bulk
    /// copy writes the column default, 0).
    /// </summary>
    private static ContactGroupMemberInfo ToManualMember(int listId, int contactId)
    {
        var member = new ContactGroupMemberInfo
        {
            ContactGroupMemberContactGroupID = listId,
            ContactGroupMemberRelatedID = contactId,
            ContactGroupMemberType = ContactGroupMemberTypeEnum.Contact,
            ContactGroupMemberFromManual = true,
        };
        member.SetValue(nameof(ContactGroupMemberInfo.ContactGroupMemberFromCondition), null);
        member.SetValue(nameof(ContactGroupMemberInfo.ContactGroupMemberFromAccount), null);

        return member;
    }

    /// <summary>
    /// Constraints are checked as by default; the table lock of the default settings is left out, so visitors who
    /// subscribe, unsubscribe or agree with a consent are not blocked while a batch is written.
    /// </summary>
    /// <param name="keepNulls">NULL values are written as NULL instead of the column defaults.</param>
    private static BulkInsertSettings RowLockBulkInsert(bool keepNulls = false) =>
        new() { Options = SqlBulkCopyOptions.CheckConstraints | (keepNulls ? SqlBulkCopyOptions.KeepNulls : SqlBulkCopyOptions.Default) };

    private static T? Read<T>(DataRow row, string column) where T : struct
    {
        object value = row[column];

        return value is DBNull ? null : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public IDisposable? TryLockJob(Guid jobId, TimeSpan timeout) => TryAppLock(JobLockResource(jobId), timeout);

    /// <summary>
    /// Takes the exclusive database application lock <paramref name="resource"/> (<c>sp_getapplock</c>, owned by the
    /// session of a connection kept open for this thread), waiting up to <paramref name="timeout"/>. <c>null</c> when
    /// another session holds it. The lock spans all application instances that share the database. Dispose releases it.
    /// </summary>
    internal static IDisposable? TryAppLock(string resource, TimeSpan timeout)
    {
        // The scope keeps one open connection as the current connection of this thread. The lock belongs to its
        // session (not to a transaction), so it spans the batch transactions and the checkpoints between them.
        var scope = new CMSConnectionScope().Open();
        try
        {
            var parameters = new QueryDataParameters
            {
                { "@resource", resource },
                { "@timeout", (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue) },
            };
            using var data = ConnectionHelper.ExecuteQuery(
                "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout; SELECT @result AS [Result];",
                parameters,
                QueryTypeEnum.SQLQuery);

            // 0 and 1 = granted; negative = timeout, cancelled, deadlock victim or error.
            if (Convert.ToInt32(data.Tables[0].Rows[0][0], CultureInfo.InvariantCulture) >= 0)
            {
                return new JobLock(scope, resource);
            }
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        scope.Dispose();

        return null;
    }

    internal static string JobLockResource(Guid jobId) => "Kentico.Xperience.Labs.ContactsImporter:" + ImportJobId.Format(jobId);

    private static ContactInfo ToContact(ImportProcessingContext context, ContactCreate create, DateTime now, Guid? guid)
    {
        var contact = new ContactInfo { ContactCreated = now };
        if (guid is { } contactGuid)
        {
            contact.ContactGUID = contactGuid;
        }

        foreach (var field in context.Fields)
        {
            if (create.Values[field.Index] is { } value)
            {
                contact.SetValue(field.Name, value);
            }
        }

        if (context.SourceField is not null)
        {
            contact.SetValue(context.SourceField, context.SourceValue);
        }

        return contact;
    }

    private static ContactTable GetContactTable()
    {
        var typeInfo = ContactInfo.TYPEINFO;
        var dataClass = DataClassInfoProvider.GetDataClassInfo(typeInfo.ObjectClassName)
            ?? throw new InvalidOperationException("The contact class was not found.");

        return new ContactTable(dataClass.ClassTableName, typeInfo.IDColumn, typeInfo.TimeStampColumn);
    }

    private static AudienceTables GetAudienceTables()
    {
        var contact = ContactInfo.TYPEINFO;
        var subscription = EmailSubscriptionConfirmationInfo.TYPEINFO;
        var agreement = ConsentAgreementInfo.TYPEINFO;

        return new AudienceTables
        {
            ContactTable = TableName(contact),
            ContactId = contact.IDColumn,
            ContactGuid = contact.GUIDColumn,
            ContactEmail = nameof(ContactInfo.ContactEmail),
            MemberTable = TableName(ContactGroupMemberInfo.TYPEINFOCONTACT),
            MemberGroupId = nameof(ContactGroupMemberInfo.ContactGroupMemberContactGroupID),
            MemberType = nameof(ContactGroupMemberInfo.ContactGroupMemberType),
            MemberRelatedId = nameof(ContactGroupMemberInfo.ContactGroupMemberRelatedID),
            SubscriptionTable = TableName(subscription),
            SubscriptionId = subscription.IDColumn,
            SubscriptionContactId = nameof(EmailSubscriptionConfirmationInfo.EmailSubscriptionConfirmationContactID),
            SubscriptionListId = nameof(EmailSubscriptionConfirmationInfo.EmailSubscriptionConfirmationRecipientListID),
            SubscriptionApproved = nameof(EmailSubscriptionConfirmationInfo.EmailSubscriptionConfirmationIsApproved),
            SubscriptionDate = nameof(EmailSubscriptionConfirmationInfo.EmailSubscriptionConfirmationDate),
            SubscriptionGuid = subscription.GUIDColumn,
            BounceTable = TableName(EmailBounceInfo.TYPEINFO),
            BounceEmail = nameof(EmailBounceInfo.EmailBounceEmailAddress),
            BounceHard = nameof(EmailBounceInfo.EmailBounceIsHardBounce),
            BounceSoftCount = nameof(EmailBounceInfo.EmailBounceSoftBounceCount),
            AgreementTable = TableName(agreement),
            AgreementId = agreement.IDColumn,
            AgreementContactId = nameof(ConsentAgreementInfo.ConsentAgreementContactID),
            AgreementConsentId = nameof(ConsentAgreementInfo.ConsentAgreementConsentID),
            AgreementRevoked = nameof(ConsentAgreementInfo.ConsentAgreementRevoked),
            AgreementTime = nameof(ConsentAgreementInfo.ConsentAgreementTime),
            AgreementGuid = agreement.GUIDColumn,
        };
    }

    private static string TableName(ObjectTypeInfo typeInfo) =>
        DataClassInfoProvider.GetDataClassInfo(typeInfo.ObjectClassName)?.ClassTableName
            ?? throw new InvalidOperationException($"The class {typeInfo.ObjectClassName} was not found.");

    private sealed class JobLock(CMSConnectionScope scope, string resource) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                ConnectionHelper.ExecuteNonQuery(
                    "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';",
                    new QueryDataParameters { { "@resource", resource } },
                    QueryTypeEnum.SQLQuery);
            }
            catch (Exception)
            {
                // The connection is gone, and the lock of its session with it.
            }
            finally
            {
                scope.Dispose();
            }
        }
    }
}
