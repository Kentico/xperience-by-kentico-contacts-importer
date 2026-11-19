using System.Text;
using System.Text.Json;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Names of the tables and columns behind recipient list subscriptions, bounces and consent agreements. The table
/// names and ID / GUID columns come from the Xperience class and type information, the other columns from the
/// properties of the info classes; <see cref="Default"/> has the names of a standard installation, for tests.
/// </summary>
internal sealed record AudienceTables
{
    public static AudienceTables Default { get; } = new();

    public string ContactTable { get; init; } = "OM_Contact";

    public string ContactId { get; init; } = "ContactID";

    public string ContactGuid { get; init; } = "ContactGUID";

    public string ContactEmail { get; init; } = "ContactEmail";

    public string MemberTable { get; init; } = "OM_ContactGroupMember";

    public string MemberGroupId { get; init; } = "ContactGroupMemberContactGroupID";

    public string MemberType { get; init; } = "ContactGroupMemberType";

    public string MemberRelatedId { get; init; } = "ContactGroupMemberRelatedID";

    public string SubscriptionTable { get; init; } = "EmailLibrary_EmailSubscriptionConfirmation";

    public string SubscriptionId { get; init; } = "EmailSubscriptionConfirmationID";

    public string SubscriptionContactId { get; init; } = "EmailSubscriptionConfirmationContactID";

    public string SubscriptionListId { get; init; } = "EmailSubscriptionConfirmationRecipientListID";

    public string SubscriptionApproved { get; init; } = "EmailSubscriptionConfirmationIsApproved";

    public string SubscriptionDate { get; init; } = "EmailSubscriptionConfirmationDate";

    public string SubscriptionGuid { get; init; } = "EmailSubscriptionConfirmationGUID";

    public string BounceTable { get; init; } = "EmailLibrary_EmailBounce";

    public string BounceEmail { get; init; } = "EmailBounceEmailAddress";

    public string BounceHard { get; init; } = "EmailBounceIsHardBounce";

    public string BounceSoftCount { get; init; } = "EmailBounceSoftBounceCount";

    public string AgreementTable { get; init; } = "CMS_ConsentAgreement";

    public string AgreementId { get; init; } = "ConsentAgreementID";

    public string AgreementContactId { get; init; } = "ConsentAgreementContactID";

    public string AgreementConsentId { get; init; } = "ConsentAgreementConsentID";

    public string AgreementRevoked { get; init; } = "ConsentAgreementRevoked";

    public string AgreementTime { get; init; } = "ConsentAgreementTime";

    public string AgreementGuid { get; init; } = "ConsentAgreementGuid";
}

/// <summary>
/// Builds the set-based SQL that reads the recipient list, bounce and consent state of the contacts of a batch.
/// Same rules as <see cref="ContactSqlBuilder"/>: all data travels in JSON parameters, identifiers come from class
/// and type information and are always quoted.
/// </summary>
internal static class AudienceSqlBuilder
{
    public const string TargetsParameter = "@targets";
    public const string ListIdParameter = "@listId";
    public const string ConsentIdParameter = "@consentId";
    public const string PlannedParameter = "@planned";

    public const string IndexColumn = "__i";
    public const string ContactIdColumn = "__id";
    public const string HasEmailColumn = "__he";
    public const string MemberColumn = "__m";
    public const string SubscriptionApprovedColumn = "__sa";
    public const string SubscriptionDateColumn = "__sd";
    public const string SubscriptionGuidColumn = "__sg";
    public const string BounceHardColumn = "__bh";
    public const string BounceSoftColumn = "__bs";
    public const string AgreementRevokedColumn = "__ar";
    public const string AgreementTimeColumn = "__at";
    public const string AgreementGuidColumn = "__ag";

    /// <summary><c>ContactGroupMemberTypeEnum.Contact</c>.</summary>
    private const string ContactMemberType = "0";

    // Longest value the bounce table can hold; an email address is at most 254 characters.
    private const string EmailSqlType = "nvarchar(256)";

    /// <summary>
    /// IDs of the contacts a batch just created, found by the GUIDs the import gave them:
    /// <see cref="ContactSqlBuilder.KeysParameter"/> holds <c>[{"i":0,"k":"guid"},...]</c>.
    /// </summary>
    public static string BuildCreatedContactsSql(AudienceTables tables) =>
        $"SELECT g.[i] AS {Q(IndexColumn)}, c.{Q(tables.ContactId)} AS {Q(ContactIdColumn)}"
        + $" FROM OPENJSON({ContactSqlBuilder.KeysParameter}) WITH ([i] int '$.i', [k] uniqueidentifier '$.k') AS g"
        + $" INNER JOIN {Q(tables.ContactTable)} AS c ON c.{Q(tables.ContactGuid)} = g.[k]";

    /// <summary>
    /// One row per target of <see cref="TargetsParameter"/> with what the import decides from.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Email: the contact's email, or with <see cref="PlannedParameter"/> = 1 the email the batch plans for it
    /// (analysis: the batch has not been written). Non-empty is decided like the send-time procedure of Xperience does.</item>
    /// <item>Membership: a row for the contact (member type 0) in the list; duplicates count once.</item>
    /// <item>Subscription: the newest confirmation of the contact for the list by date, as Xperience reads it; the
    /// highest ID decides between equal dates.</item>
    /// <item>Bounce: the row of the email address, which is unique in the bounce table.</item>
    /// <item>Agreement: the newest agreement of the contact with the consent by time, as Xperience reads it; the
    /// highest ID decides between equal times.</item>
    /// </list>
    /// </remarks>
    public static string BuildStateSql(AudienceTables tables, bool recipientList, bool consent)
    {
        var sql = new StringBuilder();
        sql.Append("SELECT t.[i] AS ").Append(Q(IndexColumn))
            .Append(", c.").Append(Q(tables.ContactId)).Append(" AS ").Append(Q(ContactIdColumn))
            .Append(", CASE WHEN e.[v] IS NOT NULL AND LTRIM(RTRIM(e.[v])) <> N'' THEN 1 ELSE 0 END AS ").Append(Q(HasEmailColumn));

        if (recipientList)
        {
            sql.Append(", CASE WHEN EXISTS (SELECT 1 FROM ").Append(Q(tables.MemberTable)).Append(" AS m WHERE m.").Append(Q(tables.MemberGroupId)).Append(" = ").Append(ListIdParameter)
                .Append(" AND m.").Append(Q(tables.MemberType)).Append(" = ").Append(ContactMemberType)
                .Append(" AND m.").Append(Q(tables.MemberRelatedId)).Append(" = c.").Append(Q(tables.ContactId)).Append(") THEN 1 ELSE 0 END AS ").Append(Q(MemberColumn))
                .Append(", s.").Append(Q(tables.SubscriptionApproved)).Append(" AS ").Append(Q(SubscriptionApprovedColumn))
                .Append(", s.").Append(Q(tables.SubscriptionDate)).Append(" AS ").Append(Q(SubscriptionDateColumn))
                .Append(", s.").Append(Q(tables.SubscriptionGuid)).Append(" AS ").Append(Q(SubscriptionGuidColumn))
                .Append(", b.").Append(Q(tables.BounceHard)).Append(" AS ").Append(Q(BounceHardColumn))
                .Append(", b.").Append(Q(tables.BounceSoftCount)).Append(" AS ").Append(Q(BounceSoftColumn));
        }

        if (consent)
        {
            sql.Append(", a.").Append(Q(tables.AgreementRevoked)).Append(" AS ").Append(Q(AgreementRevokedColumn))
                .Append(", a.").Append(Q(tables.AgreementTime)).Append(" AS ").Append(Q(AgreementTimeColumn))
                .Append(", a.").Append(Q(tables.AgreementGuid)).Append(" AS ").Append(Q(AgreementGuidColumn));
        }

        sql.Append(" FROM OPENJSON(").Append(TargetsParameter).Append(") WITH ([i] int '$.i', [id] int '$.id', [p] bit '$.p', [e] ").Append(EmailSqlType).Append(" '$.e') AS t")
            .Append(" LEFT JOIN ").Append(Q(tables.ContactTable)).Append(" AS c ON c.").Append(Q(tables.ContactId)).Append(" = t.[id]")
            .Append(" CROSS APPLY (SELECT CASE WHEN ").Append(PlannedParameter).Append(" = 1 AND t.[p] = 1 THEN t.[e] ELSE c.").Append(Q(tables.ContactEmail)).Append(" END AS [v]) AS e");

        if (recipientList)
        {
            sql.Append(" OUTER APPLY (SELECT TOP (1) x.").Append(Q(tables.SubscriptionApproved)).Append(", x.").Append(Q(tables.SubscriptionDate)).Append(", x.").Append(Q(tables.SubscriptionGuid))
                .Append(" FROM ").Append(Q(tables.SubscriptionTable)).Append(" AS x WHERE x.").Append(Q(tables.SubscriptionContactId)).Append(" = c.").Append(Q(tables.ContactId))
                .Append(" AND x.").Append(Q(tables.SubscriptionListId)).Append(" = ").Append(ListIdParameter)
                .Append(" ORDER BY x.").Append(Q(tables.SubscriptionDate)).Append(" DESC, x.").Append(Q(tables.SubscriptionId)).Append(" DESC) AS s")
                .Append(" LEFT JOIN ").Append(Q(tables.BounceTable)).Append(" AS b ON b.").Append(Q(tables.BounceEmail)).Append(" = e.[v]");
        }

        if (consent)
        {
            sql.Append(" OUTER APPLY (SELECT TOP (1) x.").Append(Q(tables.AgreementRevoked)).Append(", x.").Append(Q(tables.AgreementTime)).Append(", x.").Append(Q(tables.AgreementGuid))
                .Append(" FROM ").Append(Q(tables.AgreementTable)).Append(" AS x WHERE x.").Append(Q(tables.AgreementContactId)).Append(" = c.").Append(Q(tables.ContactId))
                .Append(" AND x.").Append(Q(tables.AgreementConsentId)).Append(" = ").Append(ConsentIdParameter)
                .Append(" ORDER BY x.").Append(Q(tables.AgreementTime)).Append(" DESC, x.").Append(Q(tables.AgreementId)).Append(" DESC) AS a");
        }

        return sql.ToString();
    }

    /// <summary>
    /// JSON for <see cref="TargetsParameter"/>: one object per target, <c>{"i":0,"id":5,"p":1,"e":"a@b.test"}</c>.
    /// <c>id</c> is left out for a contact that does not exist yet; <c>p</c> = 1 when the batch decides the
    /// contact's email, which is then <c>e</c> (left out = no email).
    /// </summary>
    /// <param name="targets">Targets; <c>i</c> is the position in this list.</param>
    /// <param name="createdContactIds">IDs of the contacts the batch created, by create index; <c>null</c> in the analysis.</param>
    public static string BuildTargetsJson(IReadOnlyList<AudienceTarget> targets, IReadOnlyList<int>? createdContactIds)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                int? contactId = target.ContactId
                    ?? (createdContactIds is not null && target.CreateIndex >= 0 && target.CreateIndex < createdContactIds.Count ? createdContactIds[target.CreateIndex] : null);

                writer.WriteStartObject();
                writer.WriteNumber("i", i);
                if (contactId is { } id)
                {
                    writer.WriteNumber("id", id);
                }

                if (target.HasPlannedEmail)
                {
                    writer.WriteNumber("p", 1);
                    if (target.PlannedEmail is not null)
                    {
                        writer.WriteString("e", target.PlannedEmail);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Q(string identifier) => ContactSqlBuilder.QuoteIdentifier(identifier);
}
