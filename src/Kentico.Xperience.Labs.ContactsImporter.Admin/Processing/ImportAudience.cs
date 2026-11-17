using System.Globalization;

using CMS.ContactManagement;
using CMS.DataEngine;
using CMS.DataProtection;
using CMS.EmailMarketing;

using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// The recipient list and consent of an import, checked against the database, with what the processing needs to
/// know about them.
/// </summary>
/// <param name="RecipientListId">Recipient list (contact group ID) contacts are added to; <c>null</c> = no list changes.</param>
/// <param name="ConsentId">Consent agreements are recorded for; <c>null</c> = no agreements.</param>
/// <param name="ConsentHash">Current hash of the consent text, stored with every agreement like Xperience does.</param>
/// <param name="SoftBounceLimit">
/// Number of soft bounces from which Xperience stops sending to an address
/// (<see cref="BouncedEmailsGlobalOptions.SoftBounceLimit"/>).
/// </param>
internal sealed record ImportAudience(int? RecipientListId = null, int? ConsentId = null, string? ConsentHash = null, int SoftBounceLimit = ImportAudience.DefaultSoftBounceLimit)
{
    /// <summary>Default of <see cref="BouncedEmailsGlobalOptions.SoftBounceLimit"/>.</summary>
    public const int DefaultSoftBounceLimit = 5;

    /// <summary>An import that changes no recipient list and records no consent.</summary>
    public static ImportAudience None { get; } = new();
}

/// <param name="Audience">The checked recipient list and consent; <c>null</c> when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the options cannot be used, without contact data; <c>null</c> when they can.</param>
internal sealed record ImportAudienceResolution(ImportAudience? Audience, string? Error = null);

/// <summary>
/// Checks the recipient list and consent selected in the import options against the database.
/// </summary>
internal interface IImportAudienceResolver
{
    /// <summary>
    /// Reads the recipient list and the consent of <paramref name="options"/>. Fails when the list does not exist or
    /// is not a recipient list, or when the consent does not exist. Options without a list and without consent need
    /// no database query.
    /// </summary>
    public ImportAudienceResolution Resolve(ImportOptions options);
}

/// <summary>
/// <see cref="IImportAudienceResolver"/> over the Xperience database: two single-row queries by primary key.
/// </summary>
internal sealed class ImportAudienceResolver(IOptionsMonitor<BouncedEmailsGlobalOptions> bounceOptions) : IImportAudienceResolver
{
    internal const string RecipientListMissingError = "The selected recipient list does not exist, is not a recipient list or is being deleted. Select another recipient list.";
    internal const string ConsentMissingError = "The selected consent does not exist. Select another consent.";
    internal const string ConsentNotSelectedError = "A consent must be selected when consent agreements are recorded.";

    public ImportAudienceResolution Resolve(ImportOptions options)
    {
        bool recordsConsent = options.Consent != ConsentMode.None;
        if (options.RecipientListId is null && !recordsConsent)
        {
            return new ImportAudienceResolution(ImportAudience.None);
        }

        if (options.RecipientListId is { } listId && !IsRecipientList(listId))
        {
            return new ImportAudienceResolution(null, RecipientListMissingError);
        }

        string? consentHash = null;
        if (recordsConsent)
        {
            if (options.ConsentId is not { } consentId)
            {
                return new ImportAudienceResolution(null, ConsentNotSelectedError);
            }

            if (!TryGetConsentHash(consentId, out consentHash))
            {
                return new ImportAudienceResolution(null, ConsentMissingError);
            }
        }

        return new ImportAudienceResolution(new ImportAudience(
            options.RecipientListId,
            recordsConsent ? options.ConsentId : null,
            consentHash,
            bounceOptions.CurrentValue.SoftBounceLimit));
    }

    /// <summary>
    /// The group exists, is a recipient list and is not being deleted (Xperience adds no members to a group in
    /// deletion either: <c>ContactGroupMemberInfoProviderExtensions.Add</c>).
    /// </summary>
    private static bool IsRecipientList(int contactGroupId)
    {
        string status = Q(nameof(ContactGroupInfo.ContactGroupStatus));
        string sql = $"SELECT CASE WHEN {Q(nameof(ContactGroupInfo.ContactGroupIsRecipientList))} = 1 AND ({status} IS NULL OR {status} <> {(int)ContactGroupStatusEnum.Deleting}) THEN 1 ELSE 0 END"
            + $" FROM {Q(TableName(ContactGroupInfo.TYPEINFO_RECIPIENT_LIST))} WHERE {Q(ContactGroupInfo.TYPEINFO_RECIPIENT_LIST.IDColumn)} = @id";
        object? value = ReadSingleValue(sql, contactGroupId);

        return value is not (null or DBNull) && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private static bool TryGetConsentHash(int consentId, out string? hash)
    {
        string sql = $"SELECT {Q(nameof(ConsentInfo.ConsentHash))}, 1 AS [found] FROM {Q(TableName(ConsentInfo.TYPEINFO))}"
            + $" WHERE {Q(ConsentInfo.TYPEINFO.IDColumn)} = @id";
        using var data = ConnectionHelper.ExecuteQuery(sql, new QueryDataParameters { { "@id", consentId } }, QueryTypeEnum.SQLQuery);
        if (data.Tables.Count == 0 || data.Tables[0].Rows.Count == 0)
        {
            hash = null;

            return false;
        }

        object value = data.Tables[0].Rows[0][0];
        hash = value is DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        return true;
    }

    private static object? ReadSingleValue(string sql, int id)
    {
        using var data = ConnectionHelper.ExecuteQuery(sql, new QueryDataParameters { { "@id", id } }, QueryTypeEnum.SQLQuery);

        return data.Tables.Count == 0 || data.Tables[0].Rows.Count == 0 ? null : data.Tables[0].Rows[0][0];
    }

    private static string Q(string identifier) => ContactSqlBuilder.QuoteIdentifier(identifier);

    private static string TableName(ObjectTypeInfo typeInfo) =>
        DataClassInfoProvider.GetDataClassInfo(typeInfo.ObjectClassName)?.ClassTableName
            ?? throw new InvalidOperationException($"The class {typeInfo.ObjectClassName} was not found.");
}
