namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Page command names of the importer pages. The React client uses the same names (<c>Client/src/shared/commands.ts</c>).
/// </summary>
internal static class ImportCommands
{
    public const string LIST_IMPORTS = "LIST_IMPORTS";
    public const string LOAD_CONFIG = "LOAD_CONFIG";
    public const string GET_JOB = "GET_JOB";
    public const string SAVE_CONFIG = "SAVE_CONFIG";
    public const string UPLOAD_CHUNK = "UPLOAD_CHUNK";
    public const string TRUNCATE_CHUNKS = "TRUNCATE_CHUNKS";
    public const string COMPLETE_UPLOAD = "COMPLETE_UPLOAD";
    public const string CANCEL_IMPORT = "CANCEL_IMPORT";
    public const string DELETE_FILES = "DELETE_FILES";
    public const string SAVE_OPTIONS = "SAVE_OPTIONS";
    public const string PROCESS_NEXT = "PROCESS_NEXT";
    public const string CONFIRM_IMPORT = "CONFIRM_IMPORT";
    public const string GET_REPORT = "GET_REPORT";
}

/// <summary>
/// Machine-readable results of the importer page commands (serialized as is; the client switches on them).
/// </summary>
internal static class ImportCommandCodes
{
    public const string OK = "ok";
    public const string INVALID_REQUEST = "invalidRequest";
    public const string JOB_NOT_FOUND = "jobNotFound";
    public const string INVALID_STATE = "invalidState";
    public const string INVALID_CONFIG = "invalidConfig";
    public const string INCONSISTENT = "inconsistent";
    public const string STORED = "stored";
    public const string ALREADY_STORED = "alreadyStored";
    public const string REJECTED = "rejected";
    public const string ANALYSIS_EXPIRED = "analysisExpired";

    /// <summary>
    /// <c>CONFIRM_IMPORT</c>: the import changes recipient list subscriptions or records consent, and the request did
    /// not carry the version of the consent statement that is current (the options or the statement changed).
    /// </summary>
    public const string ATTESTATION_REQUIRED = "attestationRequired";

    /// <summary>
    /// <c>CONFIRM_IMPORT</c>: the import needs the consent confirmation and the user does not have
    /// <see cref="ContactsImporterPermissions.CONFIRM_CONSENT"/>.
    /// </summary>
    public const string CONSENT_PERMISSION_REQUIRED = "consentPermissionRequired";

    /// <summary><c>PROCESS_NEXT</c> outcome: another request of this application instance is still processing the job.</summary>
    public const string BUSY = "busy";
}

/// <summary><c>LOAD_CONFIG</c> result: what the browser needs to parse, map and chunk a file.</summary>
internal sealed record ImportConfigResponse
{
    public required IReadOnlyList<ImportFieldDto> Fields { get; init; }

    public required ImportLimitsDto Limits { get; init; }

    /// <summary>Existing recipient lists, ordered by name, for the Options step.</summary>
    public IReadOnlyList<ImportChoiceDto> RecipientLists { get; init; } = [];

    /// <summary>Existing consents, ordered by name, for the Options step.</summary>
    public IReadOnlyList<ImportChoiceDto> Consents { get; init; } = [];
}

/// <summary>A recipient list or a consent the Options step offers.</summary>
/// <param name="Id">Contact group ID of the recipient list, or consent ID.</param>
/// <param name="Name">Display name.</param>
internal sealed record ImportChoiceDto(int Id, string Name);

/// <summary>Source columns with the consent of each row (<see cref="ImportConsentMapping"/>). Not contact fields.</summary>
internal sealed record ImportConsentMappingDto
{
    /// <summary>Column that says whether consent was given.</summary>
    public string? Agreed { get; init; }

    /// <summary>Column with the date consent was given.</summary>
    public string? Date { get; init; }
}

/// <summary>The stored confirmation of an import that affects consent (<see cref="ImportAttestation"/>).</summary>
/// <param name="UserName">User who confirmed; empty when the user no longer exists.</param>
/// <param name="Utc">When the import was confirmed.</param>
/// <param name="StatementVersion">Version of the consent statement the user confirmed.</param>
internal sealed record ImportAttestationDto(string UserName, DateTime Utc, string StatementVersion);

/// <summary><c>CONFIRM_IMPORT</c> data.</summary>
internal sealed record ConfirmImportRequest
{
    /// <summary>
    /// Version of the consent statement the user saw in the confirmation dialog. Required, and must be the current
    /// version, when the import needs the consent confirmation; ignored otherwise.
    /// </summary>
    public string? StatementVersion { get; init; }
}

/// <summary>Mappable contact field (<see cref="ContactFieldDefinition"/>).</summary>
/// <param name="Name">Field (column) name.</param>
/// <param name="Caption">Display name.</param>
/// <param name="DataType">Xperience field data type.</param>
/// <param name="MaxLength">Size of sized text fields.</param>
/// <param name="IsSupported">The import can write the field; other fields are not offered as mapping targets.</param>
/// <param name="CanBeKey">The field can be the match (dedupe key) field.</param>
/// <param name="IsReference">The field holds the ID of another object (country, contact status, user, ...); rows whose value does not exist there are skipped.</param>
/// <param name="ReferencedObjectType">Xperience object type the field references, for example <c>cms.country</c>; <c>null</c> when not known or not a reference.</param>
internal sealed record ImportFieldDto(string Name, string Caption, string DataType, int? MaxLength, bool IsSupported = true, bool CanBeKey = true, bool IsReference = false, string? ReferencedObjectType = null);

internal sealed record ImportLimitsDto
{
    /// <summary>Largest source file, in bytes.</summary>
    public required long MaxFileSizeBytes { get; init; }

    /// <summary>Target size of one gzipped chunk, in bytes.</summary>
    public required int ChunkSizeBytes { get; init; }

    /// <summary>Largest gzipped chunk the server accepts, in bytes.</summary>
    public required long MaxChunkCompressedBytes { get; init; }

    /// <summary>Largest chunk after decompression, in bytes.</summary>
    public required long MaxChunkUncompressedBytes { get; init; }

    /// <summary>Largest value of a field that is not text (numbers, dates), in characters.</summary>
    public required int MaxNonTextValueLength { get; init; }

    /// <summary>Longest dedupe key, in characters.</summary>
    public required int MaxKeyLength { get; init; }

    /// <summary>Chunk uploads the browser runs at the same time.</summary>
    public required int ParallelUploads { get; init; }
}

/// <summary>
/// Result of <c>GET_JOB</c>, <c>SAVE_OPTIONS</c> and <c>CONFIRM_IMPORT</c>: the job as the client shows it.
/// <see cref="Job"/> and <see cref="Status"/> are <c>null</c> when the job does not exist yet.
/// </summary>
internal sealed record ImportJobResponse
{
    public required string Code { get; init; }

    public string? Message { get; init; }

    /// <summary>Validation problems when <see cref="Code"/> is <c>invalidConfig</c>.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public ImportJobDto? Job { get; init; }

    public ImportStatusDto? Status { get; init; }

    /// <summary>Progress and counts of the analysis and the import (<see cref="IImportProcessor.GetProgress"/>).</summary>
    public ImportProgressDto? Progress { get; init; }

    /// <summary>Which row reports can be downloaded.</summary>
    public ImportReportsDto? Reports { get; init; }
}

/// <param name="Analyze">The dry-run report of the current analysis exists.</param>
/// <param name="Execute">The report of the import exists.</param>
/// <param name="IncludesKey">The reports hold the match value of every row (personal data).</param>
internal sealed record ImportReportsDto(bool Analyze, bool Execute, bool IncludesKey);

/// <summary>
/// Contact options (<see cref="ImportOptions"/>, PLAN §8) as camelCase option names. Data of <c>SAVE_OPTIONS</c>;
/// every value is validated.
/// </summary>
internal sealed record ImportOptionsDto
{
    /// <summary><c>create</c> or <c>skip</c>.</summary>
    public string? NewContacts { get; init; }

    /// <summary><c>update</c> or <c>skip</c>.</summary>
    public string? ExistingContacts { get; init; }

    /// <summary><c>ignore</c> or <c>clear</c>.</summary>
    public string? EmptyValues { get; init; }

    /// <summary><c>updateNewest</c>, <c>updateAll</c> or <c>skip</c>.</summary>
    public string? MultipleMatches { get; init; }

    /// <summary><c>skipRow</c> or <c>importWithoutEmail</c>. Ignored unless the email field is mapped and is not the match field.</summary>
    public string? InvalidEmail { get; init; }

    /// <summary>Contact field stamped with <see cref="SourceValue"/>; empty = no stamp.</summary>
    public string? SourceField { get; init; }

    public string? SourceValue { get; init; }

    /// <summary><c>createdOnly</c> or <c>createdAndUpdated</c>.</summary>
    public string? SourceStamp { get; init; }

    public bool ReportIncludesKey { get; init; }

    /// <summary>Recipient list (contact group ID) contacts are added to as subscribed; <c>null</c> = recipient lists are not changed.</summary>
    public int? RecipientListId { get; init; }

    /// <summary><c>keep</c> or <c>resubscribe</c>; missing = <c>keep</c>. Stored as <c>keep</c> without a recipient list.</summary>
    public string? Unsubscribed { get; init; }

    /// <summary><c>addAndReport</c> or <c>skipAndReport</c>; missing = <c>addAndReport</c>. Stored as the default without a recipient list.</summary>
    public string? BouncedAddress { get; init; }

    /// <summary><c>none</c>, <c>blanket</c> or <c>blanketWithColumnOverride</c>; missing = <c>none</c>.</summary>
    public string? Consent { get; init; }

    /// <summary>Consent agreements are recorded for. Required unless <see cref="Consent"/> is <c>none</c>; stored as <c>null</c> then.</summary>
    public int? ConsentId { get; init; }

    /// <summary><c>keep</c> or <c>reAgree</c>; missing = <c>keep</c>. Stored as <c>keep</c> when no consent is recorded.</summary>
    public string? RevokedConsent { get; init; }
}

/// <summary><c>PROCESS_NEXT</c> data.</summary>
internal sealed record ProcessNextRequest
{
    /// <summary><c>analyze</c> or <c>execute</c>.</summary>
    public string? Phase { get; init; }

    /// <summary>Token from the previous response; empty on the first call of a browser tab.</summary>
    public string? DriverToken { get; init; }

    /// <summary>The user chose to continue an import another tab or user left.</summary>
    public bool TakeOver { get; init; }
}

/// <summary>
/// <c>PROCESS_NEXT</c> result and the progress part of <c>GET_JOB</c> (<see cref="ImportProgress"/>). Counts only.
/// </summary>
internal sealed record ImportProgressDto
{
    /// <summary>
    /// camelCase <see cref="ImportProcessOutcome"/>, or <c>invalidRequest</c>, or <c>busy</c> when another request of
    /// this application instance is still processing the job (call again after a pause).
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary><c>analyze</c> or <c>execute</c>: the phase the counts belong to.</summary>
    public required string Phase { get; init; }

    /// <summary>Effective state name after the call.</summary>
    public required string State { get; init; }

    /// <summary>Token for the next call; <c>null</c> when the caller does not drive the job.</summary>
    public string? DriverToken { get; init; }

    public long RowsProcessed { get; init; }

    public long RowsTotal { get; init; }

    /// <summary>Running counts of <see cref="Phase"/>.</summary>
    public required ImportAggregatesDto Aggregates { get; init; }

    /// <summary>Counts of the finished analysis (expected), when there is one.</summary>
    public ImportAggregatesDto? Analysis { get; init; }

    public DateTime? AnalyzedUtc { get; init; }

    public string? Error { get; init; }
}

/// <summary><see cref="ImportAggregates"/> for the client.</summary>
internal sealed record ImportAggregatesDto
{
    public long Rows { get; init; }

    public long Created { get; init; }

    public long Updated { get; init; }

    public long Unchanged { get; init; }

    public long ContactsUpdated { get; init; }

    public long SkippedNew { get; init; }

    public long SkippedExisting { get; init; }

    public long SkippedMultipleMatches { get; init; }

    public long SkippedInvalidEmail { get; init; }

    public long SkippedInvalidValue { get; init; }

    public long Skipped { get; init; }

    public long DuplicateRows { get; init; }

    public long MultipleMatches { get; init; }

    public long ImportedWithoutEmail { get; init; }

    /// <summary>Recipient list: contacts added as subscribed.</summary>
    public long ListAdded { get; init; }

    public long ListAlreadySubscribed { get; init; }

    public long ListKeptUnsubscribed { get; init; }

    public long ListResubscribed { get; init; }

    public long ListSkippedBounced { get; init; }

    public long ListSkippedNoEmail { get; init; }

    public long BouncedHard { get; init; }

    public long BouncedSoftOverLimit { get; init; }

    public long BouncedSoftUnderLimit { get; init; }

    public long ConsentRecorded { get; init; }

    public long ConsentAlreadyAgreed { get; init; }

    public long ConsentKeptRevoked { get; init; }

    public long ConsentReAgreed { get; init; }

    public long ConsentNotGiven { get; init; }

    public long ConsentSkippedFutureDate { get; init; }

    public IReadOnlyList<ImportFieldChangeDto> Fields { get; init; } = [];
}

internal sealed record ImportFieldChangeDto(string Field, long Changed, long Cleared);

/// <summary><c>GET_REPORT</c> data: which report and where the previous page ended.</summary>
internal sealed record GetReportRequest
{
    /// <summary><c>analyze</c> or <c>execute</c>.</summary>
    public string? Phase { get; init; }

    /// <summary><see cref="GetReportResponse.NextPart"/> of the previous page; 0 for the first page.</summary>
    public long Part { get; init; }

    /// <summary><see cref="GetReportResponse.NextLine"/> of the previous page; 0 for the first page.</summary>
    public int Line { get; init; }
}

/// <summary>One page of a row report. The client joins the pages into one <c>.csv.gz</c> file.</summary>
internal sealed record GetReportResponse
{
    public required string Code { get; init; }

    public string? Message { get; init; }

    /// <summary>File name to offer for the download.</summary>
    public string? FileName { get; init; }

    /// <summary>CSV header line (without line break); the same on every page.</summary>
    public string? Header { get; init; }

    /// <summary>Base64 of the gzipped CSV lines of this page (UTF-8, LF line ends, no header).</summary>
    public string? Data { get; init; }

    /// <summary>Report rows in this page.</summary>
    public int Rows { get; init; }

    public long NextPart { get; init; }

    public int NextLine { get; init; }

    /// <summary>This was the last page.</summary>
    public bool Done { get; init; }
}

/// <summary>The part of <c>job.json</c> the File, Map and Upload steps use.</summary>
internal sealed record ImportJobDto
{
    public required string JobId { get; init; }

    public required ImportFileDto File { get; init; }

    public required string KeyField { get; init; }

    public required IReadOnlyList<ImportMappingDto> Mapping { get; init; }

    public required string DateFormat { get; init; }

    /// <summary>Source columns with the consent of each row; <c>null</c> when none are chosen.</summary>
    public ImportConsentMappingDto? ConsentMapping { get; init; }

    public required DateTime CreatedUtc { get; init; }

    /// <summary>Contact options of the Options step (PLAN §8).</summary>
    public required ImportOptionsDto Options { get; init; }

    /// <summary>The email field is mapped and is not the match field, so the "invalid email" option applies.</summary>
    public required bool InvalidEmailApplies { get; init; }

    /// <summary>Text contact fields that can hold the import source stamp (not mapped by this import).</summary>
    public required IReadOnlyList<ImportFieldDto> SourceFields { get; init; }

    /// <summary>Suggested source stamp value: <c>import-{date}-{file name}</c>.</summary>
    public required string DefaultSourceValue { get; init; }

    /// <summary>Display name of the recipient list of the options; <c>null</c> when none is chosen or it no longer exists.</summary>
    public string? RecipientListName { get; init; }

    /// <summary>Display name of the consent of the options; <c>null</c> when none is chosen or it no longer exists.</summary>
    public string? ConsentName { get; init; }

    /// <summary>
    /// The options change recipient list subscriptions or record consent, so confirming the import is also the
    /// confirmation of <see cref="AttestationStatement"/> and needs <see cref="ContactsImporterPermissions.CONFIRM_CONSENT"/>.
    /// </summary>
    public bool AttestationRequired { get; init; }

    /// <summary>Consent statement the user confirms. The client shows this text and never its own.</summary>
    public string AttestationStatement { get; init; } = "";

    /// <summary>Version of <see cref="AttestationStatement"/>; sent back with <c>CONFIRM_IMPORT</c>.</summary>
    public string AttestationVersion { get; init; } = "";

    /// <summary>Who confirmed the statement and when; <c>null</c> until then and after the options changed.</summary>
    public ImportAttestationDto? Attestation { get; init; }
}

internal sealed record ImportFileDto
{
    public string? Name { get; init; }

    public long Size { get; init; }

    public string? Fingerprint { get; init; }
}

internal sealed record ImportMappingDto
{
    public string? Source { get; init; }

    public string? Target { get; init; }
}

/// <summary>The part of <c>status.json</c> the client shows. No driver token, no instance name.</summary>
internal sealed record ImportStatusDto
{
    /// <summary>Effective state name (<see cref="ImportJobState"/>), <c>Paused</c> included.</summary>
    public required string State { get; init; }

    /// <summary>
    /// Stored chunks in index order: from the chunk files while the job is Uploading (gaps possible),
    /// from <c>status.json</c> afterwards.
    /// </summary>
    public required IReadOnlyList<ImportChunkDto> Chunks { get; init; }

    public required ImportProfileDto Profile { get; init; }

    public required DateTime UpdatedUtc { get; init; }

    public string? Error { get; init; }
}

internal sealed record ImportChunkDto(int Index, string Sha256, int Rows);

internal sealed record ImportProfileDto
{
    public long RowsRead { get; init; }

    public long RowsValid { get; init; }

    public long RowsRejected { get; init; }

    public long LikelyDuplicates { get; init; }

    public int ChunkCount { get; init; }
}

/// <summary><c>SAVE_CONFIG</c> data: file identity and mapping chosen in the File and Map steps.</summary>
internal sealed record SaveConfigRequest
{
    public ImportFileDto? File { get; init; }

    public string? KeyField { get; init; }

    public IReadOnlyList<ImportMappingDto>? Mapping { get; init; }

    public string? DateFormat { get; init; }

    /// <summary>Source columns with the consent of each row; <c>null</c> (or no column in it) = none.</summary>
    public ImportConsentMappingDto? ConsentMapping { get; init; }
}

/// <summary><c>SAVE_CONFIG</c> result. On <c>ok</c> the job accepts chunks (or is already past the upload).</summary>
internal sealed record SaveConfigResponse
{
    public required string Code { get; init; }

    public string? Message { get; init; }

    /// <summary>Validation problems when <see cref="Code"/> is <c>invalidConfig</c>.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public ImportJobDto? Job { get; init; }

    public ImportStatusDto? Status { get; init; }
}

/// <summary><c>UPLOAD_CHUNK</c> result (<see cref="ChunkIngestResult"/>).</summary>
internal sealed record UploadChunkResponse
{
    /// <summary><c>stored</c>, <c>alreadyStored</c> or <c>rejected</c>.</summary>
    public required string Code { get; init; }

    /// <summary>camelCase <see cref="ChunkRejectReason"/> when rejected.</summary>
    public string? Reason { get; init; }

    /// <summary>Description of the rejection; never contains contact data.</summary>
    public string? Message { get; init; }

    /// <summary>Source row number of the offending record, when known.</summary>
    public long? RowNumber { get; init; }
}

internal sealed record TruncateChunksRequest
{
    /// <summary>Chunks with this index and higher are deleted.</summary>
    public int FromIndex { get; init; } = -1;
}

internal sealed record TruncateChunksResponse
{
    public required string Code { get; init; }

    public string? Message { get; init; }

    /// <summary>Number of chunks deleted.</summary>
    public int Deleted { get; init; }

    /// <summary>Chunks still stored, in index order.</summary>
    public IReadOnlyList<ImportChunkDto> Chunks { get; init; } = [];
}

internal sealed record CompleteUploadRequest
{
    public ImportProfileDto? Profile { get; init; }
}

internal sealed record CompleteUploadResponse
{
    /// <summary><c>ok</c>, <c>jobNotFound</c>, <c>invalidState</c>, <c>inconsistent</c> or <c>invalidRequest</c>.</summary>
    public required string Code { get; init; }

    public string? Message { get; init; }

    /// <summary>Chunks the browser still has to upload (first 100) when <c>inconsistent</c>.</summary>
    public IReadOnlyList<int> MissingChunks { get; init; } = [];

    public ImportStatusDto? Status { get; init; }
}

/// <summary>Result of <c>CANCEL_IMPORT</c> and <c>DELETE_FILES</c>.</summary>
internal sealed record ImportActionResponse
{
    public required string Code { get; init; }

    public string? Message { get; init; }

    public ImportStatusDto? Status { get; init; }
}

/// <summary><c>DELETE_FILES</c> data on the imports list (the import page takes the job from its URL).</summary>
internal sealed record ImportJobRequest
{
    public string? JobId { get; init; }
}

internal sealed record ImportListResponse
{
    public required IReadOnlyList<ImportListItemDto> Imports { get; init; }

    /// <summary>True when more imports exist than are listed.</summary>
    public bool Truncated { get; init; }
}

/// <summary>One row of the recent imports list. Counts and file identity only, no contact data.</summary>
internal sealed record ImportListItemDto
{
    public required string JobId { get; init; }

    public required string FileName { get; init; }

    public required long FileSize { get; init; }

    public required DateTime CreatedUtc { get; init; }

    /// <summary>User name of the creator; empty when the user no longer exists.</summary>
    public required string CreatedBy { get; init; }

    public required string State { get; init; }

    public required DateTime UpdatedUtc { get; init; }

    public required long RowsValid { get; init; }

    /// <summary>The import (not the analysis) has started, so <see cref="Created"/> and <see cref="Updated"/> are its counts.</summary>
    public bool ImportStarted { get; init; }

    /// <summary>Contacts created by the import.</summary>
    public long Created { get; init; }

    /// <summary>Contacts updated by the import.</summary>
    public long Updated { get; init; }

    /// <summary>The job is not finished and can be opened to continue.</summary>
    public required bool CanContinue { get; init; }

    /// <summary>The job still has uploaded chunks or reports on the server.</summary>
    public required bool HasFiles { get; init; }

    /// <summary>Display name of the recipient list the import adds contacts to; <c>null</c> when none or it no longer exists.</summary>
    public string? RecipientListName { get; init; }

    /// <summary>Display name of the consent the import records agreements for; <c>null</c> when none or it no longer exists.</summary>
    public string? ConsentName { get; init; }
}
