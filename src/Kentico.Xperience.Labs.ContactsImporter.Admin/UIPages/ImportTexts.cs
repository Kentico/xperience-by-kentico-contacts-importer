using System.Globalization;
using System.Resources;

using CMS.Core;
using CMS.Helpers;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Localized server-side strings of the importer (<c>Resources/ContactsImporterResources.resx</c>).
/// </summary>
internal interface IImportTexts
{
    /// <summary>Returns the string for <paramref name="key"/>, a key without placeholders.</summary>
    public string Get(string key);

    /// <summary>
    /// Returns the text of <paramref name="message"/>. A message stored by an earlier version as English text (no
    /// importer key) is returned as it is.
    /// </summary>
    public string Get(ImportMessage message);
}

/// <summary>
/// Resource keys of the server strings. Every key of <c>ContactsImporterResources.resx</c> is a constant here; texts
/// with placeholders are created through <see cref="ImportMessages"/>, so the arguments always fit
/// (<c>ImportResourcesTests</c> checks both).
/// </summary>
internal static class ImportTextKeys
{
    private const string PREFIX = ContactsImporterResources.KEY_PREFIX + "error.";
    private const string CHUNK_REJECTED = PREFIX + "chunkrejected.";

    public const string INVALID_JOB_ID = PREFIX + "invalidjobid";
    public const string JOB_NOT_FOUND = PREFIX + "jobnotfound";
    public const string JOB_UNREADABLE = PREFIX + "jobunreadable";
    public const string INVALID_REQUEST = PREFIX + "invalidrequest";
    public const string INVALID_STATE = PREFIX + "invalidstate";
    public const string INVALID_CONFIG = PREFIX + "invalidconfig";
    public const string JOB_ID_MISMATCH = PREFIX + "jobidmismatch";
    public const string TRUNCATE_INDEX = PREFIX + "truncateindex";
    public const string BUSY = PREFIX + "busy";
    public const string IMPORT_ACTIVE = PREFIX + "importactive";
    public const string DELETE_FAILED = PREFIX + "deletefailed";
    public const string SOURCE_FIELD_MAPPED = PREFIX + "sourcefieldmapped";
    public const string ANALYSIS_EXPIRED = PREFIX + "analysisexpired";
    public const string NO_REPORT = PREFIX + "noreport";
    public const string CONSENT_COLUMN_REQUIRED = PREFIX + "consentcolumnrequired";
    public const string CONSENT_PERMISSION_REQUIRED = PREFIX + "consentpermissionrequired";
    public const string ATTESTATION_REQUIRED = PREFIX + "attestationrequired";
    public const string OTHER_IMPORT_RUNNING = PREFIX + "otherimportrunning";

    // Processing (stored in the job status, shown on the Options and Results steps).
    public const string FAILED = PREFIX + "failed";
    public const string UNEXPECTED = PREFIX + "unexpected";
    public const string UNREADABLE_JOB = PREFIX + "unreadablejob";
    public const string TRANSIENT = PREFIX + "transient";
    public const string PROCESSING_BUSY = PREFIX + "processingbusy";
    public const string CACHE_FAILED = PREFIX + "cachefailed";
    public const string CHUNK_MISSING = PREFIX + "chunkmissing";
    public const string CHUNK_INCOMPLETE = PREFIX + "chunkincomplete";
    public const string CHUNK_UNREADABLE = PREFIX + "chunkunreadable";
    public const string ABANDONED = PREFIX + "abandoned";
    public const string ATTESTATION_MISSING = PREFIX + "attestationmissing";
    public const string FIELD_MISSING = PREFIX + "fieldmissing";
    public const string AUDIENCE_UNCHECKED = PREFIX + "audienceunchecked";

    // Recipient list and consent of the options.
    public const string RECIPIENT_LIST_MISSING = PREFIX + "recipientlistmissing";
    public const string CONSENT_MISSING = PREFIX + "consentmissing";
    public const string CONSENT_NOT_SELECTED = PREFIX + "consentnotselected";
    public const string CONSENT_COLUMNS_MISSING = PREFIX + "consentcolumnsmissing";

    // Configuration checks (SAVE_CONFIG, SAVE_OPTIONS, start of a phase).
    public const string JOB_ID_EMPTY = PREFIX + "jobidempty";
    public const string DATE_FORMAT_INVALID = PREFIX + "dateformatinvalid";
    public const string FILE_NAME_INVALID = PREFIX + "filenameinvalid";
    public const string FILE_EMPTY = PREFIX + "fileempty";
    public const string FILE_TOO_LARGE = PREFIX + "filetoolarge";
    public const string FINGERPRINT_INVALID = PREFIX + "fingerprintinvalid";
    public const string NO_MAPPING = PREFIX + "nomapping";
    public const string SOURCE_COLUMN_INVALID = PREFIX + "sourcecolumninvalid";
    public const string FIELD_NOT_IMPORTABLE = PREFIX + "fieldnotimportable";
    public const string FIELD_MAPPED_TWICE = PREFIX + "fieldmappedtwice";
    public const string FIELD_TYPE_NOT_SUPPORTED = PREFIX + "fieldtypenotsupported";
    public const string KEY_FIELD_NOT_MAPPED = PREFIX + "keyfieldnotmapped";
    public const string KEY_FIELD_NOT_ALLOWED = PREFIX + "keyfieldnotallowed";
    public const string CONSENT_COLUMNS_EMPTY = PREFIX + "consentcolumnsempty";
    public const string CONSENT_COLUMN_INVALID = PREFIX + "consentcolumninvalid";
    public const string CONSENT_COLUMNS_SAME = PREFIX + "consentcolumnssame";
    public const string CONSENT_COLUMN_MAPPED = PREFIX + "consentcolumnmapped";
    public const string OPTION_UNKNOWN = PREFIX + "optionunknown";
    public const string RECIPIENT_LIST_INVALID = PREFIX + "recipientlistinvalid";
    public const string SOURCE_FIELD_NOT_TEXT = PREFIX + "sourcefieldnottext";
    public const string SOURCE_VALUE_INVALID = PREFIX + "sourcevalueinvalid";

    // Upload: what the UI says for a refused chunk or upload. The detailed reason is logged (English).
    public const string CHUNK_REJECTED_INVALID_PAYLOAD = CHUNK_REJECTED + "invalidpayload";
    public const string CHUNK_REJECTED_JOB_NOT_FOUND = CHUNK_REJECTED + "jobnotfound";
    public const string CHUNK_REJECTED_INVALID_STATE = CHUNK_REJECTED + "invalidstate";
    public const string CHUNK_REJECTED_INVALID_CONFIGURATION = CHUNK_REJECTED + "invalidconfiguration";
    public const string CHUNK_REJECTED_TOO_LARGE = CHUNK_REJECTED + "toolarge";
    public const string CHUNK_REJECTED_UPLOAD_TOO_LARGE = CHUNK_REJECTED + "uploadtoolarge";
    public const string CHUNK_REJECTED_HASH_MISMATCH = CHUNK_REJECTED + "hashmismatch";
    public const string CHUNK_REJECTED_INVALID_GZIP = CHUNK_REJECTED + "invalidgzip";
    public const string CHUNK_REJECTED_DECOMPRESSED_TOO_LARGE = CHUNK_REJECTED + "decompressedtoolarge";
    public const string CHUNK_REJECTED_INVALID_RECORD = CHUNK_REJECTED + "invalidrecord";
    public const string CHUNK_REJECTED_ROW_COUNT_MISMATCH = CHUNK_REJECTED + "rowcountmismatch";
    public const string CHUNK_REJECTED_ROW_ORDER = CHUNK_REJECTED + "roworder";
    public const string UPLOAD_NOT_COMPLETABLE = PREFIX + "uploadnotcompletable";
    public const string UPLOAD_INCONSISTENT = PREFIX + "uploadinconsistent";

    /// <summary>
    /// Consent statement of the Run confirmation dialog (PLAN D26). When the text changes, the statement version
    /// (<see cref="ImportAttestationPolicy.StatementVersion"/>) must be bumped with it.
    /// </summary>
    public const string ATTESTATION_STATEMENT = ContactsImporterResources.KEY_PREFIX + "attestation.statement";

    // Names the admin shows for the application, its pages and permissions; used as localization expressions.
    public const string APPLICATION_NAME = ContactsImporterResources.KEY_PREFIX + "application.name";
    public const string PAGE_IMPORTS = ContactsImporterResources.KEY_PREFIX + "page.imports";
    public const string PAGE_IMPORT = ContactsImporterResources.KEY_PREFIX + "page.import";
    public const string PERMISSION_IMPORT = ContactsImporterResources.KEY_PREFIX + "permission.import";
    public const string PERMISSION_CONFIRM_CONSENT = ContactsImporterResources.KEY_PREFIX + "permission.confirmconsent";

    /// <summary>What the UI says when the server refuses a chunk for <paramref name="reason"/>.</summary>
    public static string ChunkRejected(ChunkRejectReason reason) => reason switch
    {
        ChunkRejectReason.JobNotFound => CHUNK_REJECTED_JOB_NOT_FOUND,
        ChunkRejectReason.InvalidState => CHUNK_REJECTED_INVALID_STATE,
        ChunkRejectReason.InvalidConfiguration => CHUNK_REJECTED_INVALID_CONFIGURATION,
        ChunkRejectReason.TooLarge => CHUNK_REJECTED_TOO_LARGE,
        ChunkRejectReason.UploadTooLarge => CHUNK_REJECTED_UPLOAD_TOO_LARGE,
        ChunkRejectReason.HashMismatch => CHUNK_REJECTED_HASH_MISMATCH,
        ChunkRejectReason.InvalidGzip => CHUNK_REJECTED_INVALID_GZIP,
        ChunkRejectReason.DecompressedTooLarge => CHUNK_REJECTED_DECOMPRESSED_TOO_LARGE,
        ChunkRejectReason.InvalidRecord => CHUNK_REJECTED_INVALID_RECORD,
        ChunkRejectReason.RowCountMismatch => CHUNK_REJECTED_ROW_COUNT_MISMATCH,
        ChunkRejectReason.RowOrder => CHUNK_REJECTED_ROW_ORDER,
        ChunkRejectReason.InvalidPayload or _ => CHUNK_REJECTED_INVALID_PAYLOAD,
    };
}

/// <summary>
/// The messages services return for the user. Messages with placeholders are methods, so a call always passes the
/// arguments its text needs; the others are properties.
/// </summary>
internal static class ImportMessages
{
    public static ImportMessage Failed { get; } = new(ImportTextKeys.FAILED);

    public static ImportMessage Unexpected { get; } = new(ImportTextKeys.UNEXPECTED);

    public static ImportMessage UnreadableJob { get; } = new(ImportTextKeys.UNREADABLE_JOB);

    public static ImportMessage Transient { get; } = new(ImportTextKeys.TRANSIENT);

    public static ImportMessage ProcessingBusy { get; } = new(ImportTextKeys.PROCESSING_BUSY);

    public static ImportMessage CacheFailed { get; } = new(ImportTextKeys.CACHE_FAILED);

    public static ImportMessage Abandoned { get; } = new(ImportTextKeys.ABANDONED);

    public static ImportMessage AttestationMissing { get; } = new(ImportTextKeys.ATTESTATION_MISSING);

    public static ImportMessage FieldMissing { get; } = new(ImportTextKeys.FIELD_MISSING);

    public static ImportMessage AudienceUnchecked { get; } = new(ImportTextKeys.AUDIENCE_UNCHECKED);

    public static ImportMessage RecipientListMissing { get; } = new(ImportTextKeys.RECIPIENT_LIST_MISSING);

    public static ImportMessage ConsentMissing { get; } = new(ImportTextKeys.CONSENT_MISSING);

    public static ImportMessage ConsentNotSelected { get; } = new(ImportTextKeys.CONSENT_NOT_SELECTED);

    public static ImportMessage ConsentColumnsMissing { get; } = new(ImportTextKeys.CONSENT_COLUMNS_MISSING);

    public static ImportMessage JobIdEmpty { get; } = new(ImportTextKeys.JOB_ID_EMPTY);

    public static ImportMessage DateFormatInvalid { get; } = new(ImportTextKeys.DATE_FORMAT_INVALID);

    public static ImportMessage FileNameInvalid { get; } = new(ImportTextKeys.FILE_NAME_INVALID);

    public static ImportMessage FileEmpty { get; } = new(ImportTextKeys.FILE_EMPTY);

    public static ImportMessage FingerprintInvalid { get; } = new(ImportTextKeys.FINGERPRINT_INVALID);

    public static ImportMessage NoMapping { get; } = new(ImportTextKeys.NO_MAPPING);

    public static ImportMessage SourceColumnInvalid { get; } = new(ImportTextKeys.SOURCE_COLUMN_INVALID);

    public static ImportMessage KeyFieldNotMapped { get; } = new(ImportTextKeys.KEY_FIELD_NOT_MAPPED);

    public static ImportMessage ConsentColumnsEmpty { get; } = new(ImportTextKeys.CONSENT_COLUMNS_EMPTY);

    public static ImportMessage ConsentColumnInvalid { get; } = new(ImportTextKeys.CONSENT_COLUMN_INVALID);

    public static ImportMessage ConsentColumnsSame { get; } = new(ImportTextKeys.CONSENT_COLUMNS_SAME);

    public static ImportMessage ConsentColumnMapped { get; } = new(ImportTextKeys.CONSENT_COLUMN_MAPPED);

    public static ImportMessage OptionUnknown { get; } = new(ImportTextKeys.OPTION_UNKNOWN);

    public static ImportMessage RecipientListInvalid { get; } = new(ImportTextKeys.RECIPIENT_LIST_INVALID);

    public static ImportMessage SourceFieldNotText { get; } = new(ImportTextKeys.SOURCE_FIELD_NOT_TEXT);

    public static ImportMessage SourceValueInvalid { get; } = new(ImportTextKeys.SOURCE_VALUE_INVALID);

    /// <param name="state">The job state, by its name.</param>
    public static ImportMessage InvalidState(ImportJobState state) => new(ImportTextKeys.INVALID_STATE, state.ToString());

    /// <param name="fileName">File name of the import whose Run is unfinished.</param>
    public static ImportMessage OtherImportRunning(string fileName) => new(ImportTextKeys.OTHER_IMPORT_RUNNING, fileName);

    public static ImportMessage ChunkMissing(int chunkIndex) => new(ImportTextKeys.CHUNK_MISSING, chunkIndex);

    public static ImportMessage ChunkIncomplete(int chunkIndex) => new(ImportTextKeys.CHUNK_INCOMPLETE, chunkIndex);

    public static ImportMessage ChunkUnreadable(int chunkIndex) => new(ImportTextKeys.CHUNK_UNREADABLE, chunkIndex);

    public static ImportMessage FileTooLarge(long maxBytes) => new(ImportTextKeys.FILE_TOO_LARGE, maxBytes);

    public static ImportMessage FieldNotImportable(string field) => new(ImportTextKeys.FIELD_NOT_IMPORTABLE, field);

    public static ImportMessage FieldMappedTwice(string field) => new(ImportTextKeys.FIELD_MAPPED_TWICE, field);

    public static ImportMessage FieldTypeNotSupported(string field) => new(ImportTextKeys.FIELD_TYPE_NOT_SUPPORTED, field);

    public static ImportMessage KeyFieldNotAllowed(string field) => new(ImportTextKeys.KEY_FIELD_NOT_ALLOWED, field);
}

/// <summary>
/// Resolves strings through Xperience localization, in the UI culture of the current user.
/// </summary>
internal sealed class LocalizedImportTexts(ILocalizationService localizationService) : IImportTexts
{
    public string Get(string key) => localizationService.GetString(key);

    public string Get(ImportMessage message)
    {
        if (!ImportTextFormatter.IsImporterKey(message.Key))
        {
            return message.Key;
        }

        return message.Arguments.Count == 0
            ? localizationService.GetString(message.Key)
            : localizationService.GetStringFormat(message.Key, [.. message.Arguments]);
    }
}

/// <summary>
/// Reads the en-US strings straight from the embedded resource: for code that runs without an initialized Xperience
/// application, for logs (<see cref="ImportMessage.ToString"/>) and tests.
/// </summary>
internal sealed class ResourceImportTexts : IImportTexts
{
    private static readonly ResourceManager resources = new(typeof(ContactsImporterResources));

    public string Get(string key) => resources.GetString(key, CultureInfo.InvariantCulture) ?? key;

    public string Get(ImportMessage message)
    {
        if (!ImportTextFormatter.IsImporterKey(message.Key))
        {
            return message.Key;
        }

        return message.Arguments.Count == 0
            ? Get(message.Key)
            : string.Format(CultureInfo.InvariantCulture, Get(message.Key), [.. message.Arguments]);
    }
}

internal static class ImportTextFormatter
{
    /// <summary>The key is one of the importer's resource keys (not English text stored by an earlier version).</summary>
    public static bool IsImporterKey(string key) => key.StartsWith(ContactsImporterResources.KEY_PREFIX, StringComparison.Ordinal);
}
