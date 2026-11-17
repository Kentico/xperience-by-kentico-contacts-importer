using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Logic of the importer page commands: validates every input from the browser and calls the job services.
/// The pages add authentication, permissions and the current user. Never throws for bad input.
/// </summary>
internal interface IImportCommandService
{
    /// <summary>Mappable contact fields, the limits the browser must respect, and the recipient lists and consents to choose from.</summary>
    public ImportConfigResponse GetConfig();

    /// <summary>Recent imports, newest first. Runs the lazy cleanup sweep.</summary>
    public ImportListResponse ListImports();

    /// <summary>Job configuration and state, for resuming. <c>jobNotFound</c> for a job that was not created yet.</summary>
    public ImportJobResponse GetJob(string? jobId);

    /// <summary>
    /// Creates the job or changes its file and mapping, then opens it for chunks (Configuring → Uploading).
    /// A changed mapping of a job with uploaded data deletes that data.
    /// </summary>
    public SaveConfigResponse SaveConfig(string? jobId, SaveConfigRequest? request, int userId);

    /// <summary>Verifies, validates and stores one chunk.</summary>
    public UploadChunkResponse UploadChunk(string? jobId, UploadChunkPayload? payload);

    /// <summary>
    /// Resume: deletes the stored chunks with index <c>fromIndex</c> and higher, because the browser
    /// produced different chunks from there on (other browser, other gzip output).
    /// </summary>
    public TruncateChunksResponse TruncateChunks(string? jobId, TruncateChunksRequest? request);

    /// <summary>Checks the uploaded chunks against the profile and moves the job to Uploaded.</summary>
    public CompleteUploadResponse CompleteUpload(string? jobId, CompleteUploadRequest? request);

    /// <summary>
    /// Stores the contact, recipient list and consent options of the Options step. Allowed from Uploaded to Confirmed
    /// and not while a request is processing the job; a change after the analysis started returns the job to Uploaded
    /// (analyze again). A chosen recipient list and consent must exist; consent per row needs a mapped consent column.
    /// </summary>
    public ImportJobResponse SaveOptions(string? jobId, ImportOptionsDto? request);

    /// <summary>
    /// Does the next piece of the analysis or the import (<see cref="IImportProcessor.ProcessNext"/>) and returns the
    /// progress. One request per job at a time in this application instance.
    /// </summary>
    public ImportProgressDto ProcessNext(string? jobId, ProcessNextRequest? request, int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms an analyzed job for import (Analyzed → Confirmed). When the options change recipient list subscriptions
    /// or record consent (<see cref="ImportAttestationPolicy.IsRequired"/>), confirming is also the confirmation of the
    /// consent statement: the user needs <see cref="ContactsImporterPermissions.CONFIRM_CONSENT"/> and the request must
    /// carry the current statement version; user and time are stored with the job.
    /// </summary>
    /// <param name="jobId">Job to confirm.</param>
    /// <param name="request">Statement version the user saw.</param>
    /// <param name="userId">The confirming user.</param>
    /// <param name="canConfirmConsent">The user has <see cref="ContactsImporterPermissions.CONFIRM_CONSENT"/>.</param>
    public ImportJobResponse Confirm(string? jobId, ConfirmImportRequest? request, int userId, bool canConfirmConsent);

    /// <summary>One page of the row report of the analysis or the import.</summary>
    public GetReportResponse GetReport(string? jobId, GetReportRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Cancels an unfinished job and deletes its uploaded chunks. A processing request stops after its current batch.</summary>
    public ImportActionResponse Cancel(string? jobId, int userId);

    /// <summary>"Delete import files now": deletes chunks and reports; cancels the job when it is not finished.</summary>
    public ImportActionResponse DeleteFiles(string? jobId, int userId);
}

internal sealed class ImportCommandService(
    IImportJobStore store,
    IChunkIngestService ingest,
    IContactFieldSchemaProvider schemaProvider,
    IImportJobCleanupService cleanup,
    IImportUserNameProvider userNames,
    IImportProcessor processor,
    IImportReportPager reportPager,
    IImportReportReader reportReader,
    IImportProcessGate processGate,
    IImportTexts texts,
    IImportChoiceProvider choices,
    IImportAudienceResolver audience,
    IOptions<ContactsImporterOptions> options,
    TimeProvider timeProvider,
    ILogger<ImportCommandService>? logger = null) : IImportCommandService
{
    /// <summary>Chunk uploads the browser runs at the same time.</summary>
    internal const int ParallelUploads = 3;

    /// <summary>Most imports returned by <see cref="ListImports"/>.</summary>
    internal const int MaxListedImports = 200;

    /// <summary>Longest driver token accepted from the browser (tokens are 32 hex characters).</summary>
    internal const int MaxDriverTokenLength = 64;

    public ImportConfigResponse GetConfig()
    {
        cleanup.SweepIfDue();
        var settings = options.Value;

        return new ImportConfigResponse
        {
            Fields = [.. schemaProvider.GetMappableFields().Select(FieldDto)],
            Limits = new ImportLimitsDto
            {
                MaxFileSizeBytes = settings.MaxFileSizeBytes,
                ChunkSizeBytes = settings.ChunkSizeBytes,
                MaxChunkCompressedBytes = settings.MaxChunkCompressedBytes,
                MaxChunkUncompressedBytes = settings.MaxChunkUncompressedBytes,
                MaxNonTextValueLength = ContactFieldDefinition.MaxNonTextValueLength,
                MaxKeyLength = KeyNormalizer.MaxKeyLength,
                ParallelUploads = ParallelUploads,
            },
            RecipientLists = choices.GetRecipientLists(),
            Consents = choices.GetConsents(),
        };
    }

    public ImportListResponse ListImports()
    {
        cleanup.SweepIfDue();

        var jobs = new List<(ImportJob Job, ImportJobStatus Status)>();
        foreach (var jobId in store.ListJobs())
        {
            if (TryRead(jobId, out var job, out var status) && job is not null && status is not null)
            {
                jobs.Add((job, status));
            }
        }

        jobs.Sort((x, y) => y.Job.CreatedUtc.CompareTo(x.Job.CreatedUtc));
        bool truncated = jobs.Count > MaxListedImports;
        if (truncated)
        {
            jobs.RemoveRange(MaxListedImports, jobs.Count - MaxListedImports);
        }

        var names = userNames.GetUserNames([.. jobs.Select(j => j.Job.CreatedByUserId).Distinct()]);
        var utcNow = UtcNow();
        // Names of the recipient lists and consents the listed imports use; not queried when no import uses one.
        var listNames = jobs.Any(j => j.Job.Options.RecipientListId is not null) ? ChoiceNames(choices.GetRecipientLists()) : [];
        var consentNames = jobs.Any(j => j.Job.Options.ConsentId is not null) ? ChoiceNames(choices.GetConsents()) : [];

        return new ImportListResponse
        {
            Truncated = truncated,
            Imports = [.. jobs.Select(j => new ImportListItemDto
            {
                JobId = ImportJobId.Format(j.Job.JobId),
                FileName = j.Job.File.Name,
                FileSize = j.Job.File.Size,
                CreatedUtc = j.Job.CreatedUtc,
                CreatedBy = names.TryGetValue(j.Job.CreatedByUserId, out string? name) ? name : "",
                State = EffectiveState(j.Status, utcNow).ToString(),
                UpdatedUtc = j.Status.UpdatedUtc,
                RowsValid = j.Status.Profile.RowsValid,
                ImportStarted = ImportStarted(j.Status),
                Created = ImportStarted(j.Status) ? j.Status.Aggregates.GetValueOrDefault(ImportAggregateKeys.Created) : 0,
                Updated = ImportStarted(j.Status) ? j.Status.Aggregates.GetValueOrDefault(ImportAggregateKeys.ContactsUpdated) : 0,
                CanContinue = !ImportJobStateMachine.IsTerminal(j.Status.State),
                HasFiles = HasFiles(j.Job.JobId),
                RecipientListName = j.Job.Options.RecipientListId is { } listId ? listNames.GetValueOrDefault(listId) : null,
                ConsentName = j.Job.Options.Consent != ConsentMode.None && j.Job.Options.ConsentId is { } consentId ? consentNames.GetValueOrDefault(consentId) : null,
            })],
        };
    }

    public ImportJobResponse GetJob(string? jobId)
    {
        cleanup.SweepIfDue();

        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return new ImportJobResponse { Code = ImportCommandCodes.INVALID_REQUEST, Message = texts.Get(ImportTextKeys.INVALID_JOB_ID) };
        }

        if (!TryRead(id, out var job, out var status))
        {
            return new ImportJobResponse { Code = ImportCommandCodes.INVALID_STATE, Message = texts.Get(ImportTextKeys.JOB_UNREADABLE) };
        }

        if (job is null || status is null)
        {
            return new ImportJobResponse { Code = ImportCommandCodes.JOB_NOT_FOUND };
        }

        return JobResponse(ImportCommandCodes.OK, job, status);
    }

    public ImportJobResponse SaveOptions(string? jobId, ImportOptionsDto? request)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return JobFailed(ImportCommandCodes.INVALID_REQUEST, ImportTextKeys.INVALID_JOB_ID);
        }

        if (!TryRead(id, out var existing, out var status))
        {
            return JobFailed(ImportCommandCodes.INVALID_STATE, ImportTextKeys.JOB_UNREADABLE);
        }

        if (existing is null || status is null)
        {
            return JobFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND);
        }

        if (request is null || !TryBuildOptions(request, existing, out var newOptions))
        {
            return JobFailed(ImportCommandCodes.INVALID_REQUEST, ImportTextKeys.INVALID_REQUEST);
        }

        var utcNow = UtcNow();
        if (!AcceptsOptions(status.State))
        {
            return JobFailed(ImportCommandCodes.INVALID_STATE, ImportMessages.InvalidState(EffectiveState(status, utcNow)));
        }

        var job = existing with { Options = newOptions };
        // The resolver checks the chosen recipient list (exists, is a recipient list, not being deleted) and consent.
        var errors = ImportJobConfigValidator.Validate(job, schemaProvider.GetMappableFields(), options.Value, audience).Select(error => texts.Get(error)).ToList();
        errors.AddRange(ValidateAudience(job));
        if (newOptions.SourceField is { } sourceField && job.Mapping.Any(m => string.Equals(m.Target, sourceField, StringComparison.Ordinal)))
        {
            errors.Add(texts.Get(ImportTextKeys.SOURCE_FIELD_MAPPED));
        }

        if (errors.Count > 0)
        {
            return new ImportJobResponse { Code = ImportCommandCodes.INVALID_CONFIG, Message = texts.Get(ImportTextKeys.INVALID_CONFIG), Errors = errors };
        }

        if (ImportJobStateMachine.ClassifyConfigChange(existing, job) == ImportConfigChange.None)
        {
            return JobResponse(ImportCommandCodes.OK, existing, status);
        }

        // "Not while driven": a request that is processing the job right now would continue with the old options.
        if (!processGate.TryEnter(id))
        {
            return JobFailed(ImportCommandCodes.INVALID_STATE, ImportTextKeys.BUSY);
        }

        try
        {
            bool applied = false;
            bool analysisReset = false;
            var updated = store.UpdateStatus(id, current =>
            {
                applied = AcceptsOptions(current.State)
                    && ImportJobStateMachine.ApplyConfigChange(current, ImportConfigChange.Options, utcNow) is { Allowed: true };
                if (!applied)
                {
                    return current;
                }

                analysisReset = current.State != ImportJobState.Uploaded;

                return ImportJobStateMachine.ApplyConfigChange(current, ImportConfigChange.Options, utcNow).Status;
            });
            if (updated is null)
            {
                return JobFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND);
            }

            if (!applied)
            {
                return JobFailed(ImportCommandCodes.INVALID_STATE, ImportMessages.InvalidState(EffectiveState(updated, utcNow)));
            }

            // What was confirmed no longer matches what will run: the confirmation is asked again.
            job = analysisReset ? job with { Attestation = null } : job;
            store.WriteJob(job);

            return JobResponse(ImportCommandCodes.OK, job, updated);
        }
        finally
        {
            processGate.Exit(id);
        }
    }

    public ImportProgressDto ProcessNext(string? jobId, ProcessNextRequest? request, int userId, CancellationToken cancellationToken = default)
    {
        string? token = string.IsNullOrEmpty(request?.DriverToken) ? null : request.DriverToken;
        if (!ImportJobId.TryParse(jobId, out var id)
            || !TryParsePhase(request?.Phase, out var phase)
            || token is { Length: > MaxDriverTokenLength })
        {
            return new ImportProgressDto
            {
                Outcome = ImportCommandCodes.INVALID_REQUEST,
                Phase = ImportReportFormat.PhaseName(ImportPhase.Analyze),
                State = "",
                Aggregates = new ImportAggregatesDto(),
                Error = texts.Get(ImportTextKeys.INVALID_REQUEST),
            };
        }

        if (!processGate.TryEnter(id))
        {
            var current = processor.GetProgress(id);
            if (current is null)
            {
                return ProgressDto(new ImportProgress { Outcome = ImportProcessOutcome.JobNotFound, Phase = phase });
            }

            // The request in progress belongs to the caller (a retry sent while the previous request still finishes
            // its batch): come back later, nothing is processed twice. Otherwise another tab or user drives the job.
            bool ownRequest = token is not null && string.Equals(store.ReadStatus(id)?.DriverToken, token, StringComparison.Ordinal);

            return ownRequest
                ? ProgressDto(current) with { Outcome = ImportCommandCodes.BUSY, Error = texts.Get(ImportTextKeys.BUSY) }
                : ProgressDto(current) with { Outcome = JsonNamingPolicy.CamelCase.ConvertName(nameof(ImportProcessOutcome.ActiveElsewhere)) };
        }

        try
        {
            return ProgressDto(processor.ProcessNext(id, phase, token, request!.TakeOver, cancellationToken, UserOrNull(userId)));
        }
        finally
        {
            processGate.Exit(id);
        }
    }

    public ImportJobResponse Confirm(string? jobId, ConfirmImportRequest? request, int userId, bool canConfirmConsent)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return JobFailed(ImportCommandCodes.INVALID_REQUEST, ImportTextKeys.INVALID_JOB_ID);
        }

        if (!TryRead(id, out var current, out var currentStatus))
        {
            return JobFailed(ImportCommandCodes.INVALID_STATE, ImportTextKeys.JOB_UNREADABLE);
        }

        if (current is null || currentStatus is null)
        {
            return JobFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND);
        }

        // Confirming an import that changes subscriptions or records consent is the confirmation of the consent
        // statement: only for users with the permission, only for the statement version that is current, and stamped
        // with the user and time the server knows (never values from the browser).
        ImportAttestation? attestation = null;
        if (ImportAttestationPolicy.IsRequired(current.Options))
        {
            if (!canConfirmConsent || userId <= 0)
            {
                return JobResponse(ImportCommandCodes.CONSENT_PERMISSION_REQUIRED, current, currentStatus) with
                {
                    Message = texts.Get(ImportTextKeys.CONSENT_PERMISSION_REQUIRED),
                };
            }

            if (!string.Equals(request?.StatementVersion, ImportAttestationPolicy.StatementVersion, StringComparison.Ordinal))
            {
                return JobResponse(ImportCommandCodes.ATTESTATION_REQUIRED, current, currentStatus) with
                {
                    Message = texts.Get(ImportTextKeys.ATTESTATION_REQUIRED),
                };
            }

            attestation = new ImportAttestation { UserId = userId, Utc = UtcNow(), StatementVersion = ImportAttestationPolicy.StatementVersion };
        }

        ImportConfirmResult result;
        try
        {
            result = processor.Confirm(id, attestation);
        }
        catch (InvalidDataException)
        {
            return JobFailed(ImportCommandCodes.INVALID_STATE, ImportTextKeys.JOB_UNREADABLE);
        }

        if (result.Outcome == ImportConfirmOutcome.JobNotFound || !TryRead(id, out var job, out var status) || job is null || status is null)
        {
            return JobFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND);
        }

        return result.Outcome switch
        {
            ImportConfirmOutcome.Confirmed => JobResponse(ImportCommandCodes.OK, job, status),
            ImportConfirmOutcome.AnalysisExpired => JobResponse(ImportCommandCodes.ANALYSIS_EXPIRED, job, status) with { Message = texts.Get(ImportTextKeys.ANALYSIS_EXPIRED) },
            // The options changed between the check above and the processor (another tab): confirm again with them.
            ImportConfirmOutcome.AttestationRequired => JobResponse(ImportCommandCodes.ATTESTATION_REQUIRED, job, status) with { Message = texts.Get(ImportTextKeys.ATTESTATION_REQUIRED) },
            ImportConfirmOutcome.InvalidState or ImportConfirmOutcome.JobNotFound or _ => JobResponse(ImportCommandCodes.INVALID_STATE, job, status) with
            {
                Message = texts.Get(ImportMessages.InvalidState(EffectiveState(status, UtcNow()))),
            },
        };
    }

    public GetReportResponse GetReport(string? jobId, GetReportRequest? request, CancellationToken cancellationToken = default)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return new GetReportResponse { Code = ImportCommandCodes.INVALID_REQUEST, Message = texts.Get(ImportTextKeys.INVALID_JOB_ID) };
        }

        if (request is null || !TryParsePhase(request.Phase, out var phase) || request.Part < 0 || request.Line < 0)
        {
            return new GetReportResponse { Code = ImportCommandCodes.INVALID_REQUEST, Message = texts.Get(ImportTextKeys.INVALID_REQUEST) };
        }

        if (!TryRead(id, out var job, out var status) || job is null || status is null)
        {
            return new GetReportResponse { Code = ImportCommandCodes.JOB_NOT_FOUND, Message = texts.Get(ImportTextKeys.JOB_NOT_FOUND) };
        }

        var page = IsReportCurrent(status, phase) ? reportPager.ReadPage(id, phase, request.Part, request.Line, cancellationToken) : null;
        if (page is null)
        {
            return new GetReportResponse { Code = ImportCommandCodes.INVALID_STATE, Message = texts.Get(ImportTextKeys.NO_REPORT) };
        }

        return new GetReportResponse
        {
            Code = ImportCommandCodes.OK,
            FileName = reportReader.GetDownloadFileName(phase),
            Header = ImportReportFormat.Header(job.Options),
            Data = Convert.ToBase64String(page.GzippedLines),
            Rows = page.Rows,
            NextPart = page.NextPart,
            NextLine = page.NextLine,
            Done = page.Done,
        };
    }

    public SaveConfigResponse SaveConfig(string? jobId, SaveConfigRequest? request, int userId)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return SaveFailed(ImportCommandCodes.INVALID_REQUEST, ImportTextKeys.INVALID_JOB_ID);
        }

        if (!TryBuildConfig(request, out var file, out string keyField, out var mapping, out string dateFormat, out var consentMapping))
        {
            return SaveFailed(ImportCommandCodes.INVALID_REQUEST, ImportTextKeys.INVALID_REQUEST);
        }

        if (!TryRead(id, out var existing, out var status))
        {
            return SaveFailed(ImportCommandCodes.INVALID_STATE, ImportTextKeys.JOB_UNREADABLE);
        }

        var utcNow = UtcNow();
        var fields = schemaProvider.GetMappableFields();
        bool isNew = existing is null || status is null;
        if (isNew && store.JobExists(id))
        {
            // A folder without readable job files: an interrupted create. Start over.
            store.DeleteJob(id);
        }

        var job = isNew
            ? new ImportJob { JobId = id, CreatedByUserId = userId, CreatedUtc = utcNow, File = file, KeyField = keyField, Mapping = mapping, DateFormat = dateFormat, ConsentMapping = consentMapping }
            : existing! with { File = file, KeyField = keyField, Mapping = mapping, DateFormat = dateFormat, ConsentMapping = consentMapping };
        if (job.Options.SourceField is { } sourceField && mapping.Any(m => string.Equals(m.Target, sourceField, StringComparison.Ordinal)))
        {
            // The stamp field is now a mapped column: the file value wins, the stamp is chosen again in the Options step.
            job = job with { Options = job.Options with { SourceField = null, SourceValue = null, SourceStamp = SourceStampBehavior.CreatedOnly } };
        }

        if (job.Options.Consent == ConsentMode.BlanketWithColumnOverride && !HasConsentColumn(job))
        {
            // The column that said which rows agreed is gone. "Only those rows" never widens to every row: no consent
            // is recorded until it is chosen again in the Options step.
            job = job with { Options = job.Options with { Consent = ConsentMode.None, ConsentId = null, RevokedConsent = RevokedConsentBehavior.Keep } };
        }


        var errors = ImportJobConfigValidator.Validate(job, fields, options.Value);
        if (errors.Count > 0)
        {
            return new SaveConfigResponse { Code = ImportCommandCodes.INVALID_CONFIG, Message = texts.Get(ImportTextKeys.INVALID_CONFIG), Errors = [.. errors.Select(error => texts.Get(error))] };
        }

        if (isNew)
        {
            store.CreateJob(job);
        }
        else
        {
            var change = ImportJobStateMachine.ClassifyConfigChange(existing!, job);
            var applied = ImportJobStateMachine.ApplyConfigChange(status!, change, utcNow);
            if (ImportJobStateMachine.IsTerminal(status!.State) || !applied.Allowed)
            {
                return SaveFailed(ImportCommandCodes.INVALID_STATE, ImportMessages.InvalidState(EffectiveState(status, utcNow)));
            }

            if (change != ImportConfigChange.None)
            {
                // Status first: once the job is back in Configuring nothing reads the old chunks.
                store.UpdateStatus(id, current => ImportJobStateMachine.ApplyConfigChange(current, change, utcNow) is { Allowed: true } result ? result.Status : current);
                if (applied.DeleteChunks)
                {
                    store.DeleteChunks(id);
                }

                store.WriteJob(job);
            }
        }

        var updated = store.UpdateStatus(id, current =>
            current.State == ImportJobState.Configuring ? ImportJobStateMachine.Transition(current, ImportJobState.Uploading, utcNow) : current);
        if (updated is null)
        {
            return SaveFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND);
        }

        return new SaveConfigResponse { Code = ImportCommandCodes.OK, Job = JobDto(job), Status = ToDto(id, updated) };
    }

    public UploadChunkResponse UploadChunk(string? jobId, UploadChunkPayload? payload)
    {
        if (!ImportJobId.TryParse(jobId, out var id) || payload is null)
        {
            return ChunkRejected(ChunkRejectReason.InvalidPayload, texts.Get(ImportTextKeys.INVALID_REQUEST));
        }

        // The job comes from the page URL; a payload for another job is a client error, never a way to reach that job.
        if (!ImportJobId.TryParse(payload.JobId, out var payloadJobId) || payloadJobId != id)
        {
            return ChunkRejected(ChunkRejectReason.InvalidPayload, texts.Get(ImportTextKeys.JOB_ID_MISMATCH));
        }

        var result = ingest.Ingest(payload);

        return result.Outcome switch
        {
            ChunkIngestOutcome.Stored => new UploadChunkResponse { Code = ImportCommandCodes.STORED },
            ChunkIngestOutcome.AlreadyStored => new UploadChunkResponse { Code = ImportCommandCodes.ALREADY_STORED },
            ChunkIngestOutcome.Rejected => RejectedByIngest(id, payload.Index, result),
            _ => ChunkRejected(ChunkRejectReason.InvalidPayload, texts.Get(ImportTextKeys.INVALID_REQUEST)),
        };
    }

    public TruncateChunksResponse TruncateChunks(string? jobId, TruncateChunksRequest? request)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return new TruncateChunksResponse { Code = ImportCommandCodes.INVALID_REQUEST, Message = texts.Get(ImportTextKeys.INVALID_JOB_ID) };
        }

        if (request is null || request.FromIndex is < 0 or >= ChunkIngestService.MaxChunkCount)
        {
            return new TruncateChunksResponse { Code = ImportCommandCodes.INVALID_REQUEST, Message = texts.Get(ImportTextKeys.TRUNCATE_INDEX) };
        }

        if (!TryRead(id, out _, out var status) || status is null)
        {
            return new TruncateChunksResponse { Code = ImportCommandCodes.JOB_NOT_FOUND, Message = texts.Get(ImportTextKeys.JOB_NOT_FOUND) };
        }

        if (status.State != ImportJobState.Uploading)
        {
            return new TruncateChunksResponse
            {
                Code = ImportCommandCodes.INVALID_STATE,
                Message = texts.Get(ImportMessages.InvalidState(EffectiveState(status, UtcNow()))),
            };
        }

        int deleted = DeleteChunksFrom(id, request.FromIndex);

        return new TruncateChunksResponse { Code = ImportCommandCodes.OK, Deleted = deleted, Chunks = ToDto(store.ListChunks(id)) };
    }

    public CompleteUploadResponse CompleteUpload(string? jobId, CompleteUploadRequest? request)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return new CompleteUploadResponse { Code = ImportCommandCodes.INVALID_REQUEST, Message = texts.Get(ImportTextKeys.INVALID_JOB_ID) };
        }

        if (request?.Profile is not { } profileDto || profileDto.ChunkCount is < 1 or > ChunkIngestService.MaxChunkCount)
        {
            return new CompleteUploadResponse { Code = ImportCommandCodes.INVALID_REQUEST, Message = texts.Get(ImportTextKeys.INVALID_REQUEST) };
        }

        var profile = new ImportProfile
        {
            RowsRead = profileDto.RowsRead,
            RowsValid = profileDto.RowsValid,
            RowsRejected = profileDto.RowsRejected,
            LikelyDuplicates = profileDto.LikelyDuplicates,
            ChunkCount = profileDto.ChunkCount,
        };

        if (!TryRead(id, out _, out var status) || status is null)
        {
            return new CompleteUploadResponse { Code = ImportCommandCodes.JOB_NOT_FOUND, Message = texts.Get(ImportTextKeys.JOB_NOT_FOUND) };
        }

        // The browser repeats the call when the response got lost.
        if (status.State == ImportJobState.Uploaded && status.Profile == profile)
        {
            return new CompleteUploadResponse { Code = ImportCommandCodes.OK, Status = ToDto(id, status) };
        }

        if (status.State == ImportJobState.Uploading)
        {
            // Chunks past the end are left from an earlier upload attempt that produced more chunks.
            DeleteChunksFrom(id, profile.ChunkCount);
        }

        var result = ingest.CompleteUpload(id, profile);
        if (result.Outcome != UploadCompletionOutcome.Completed)
        {
            // The user gets a message per outcome; the detailed reason (English, no values of the file) goes to the log.
            logger?.LogWarning("Contact import {JobId}: the upload could not be completed ({Outcome}): {Reason}", id, result.Outcome, result.Message);
        }

        return result.Outcome switch
        {
            UploadCompletionOutcome.Completed => new CompleteUploadResponse { Code = ImportCommandCodes.OK, Status = ToDto(id, result.Status!) },
            UploadCompletionOutcome.JobNotFound => new CompleteUploadResponse { Code = ImportCommandCodes.JOB_NOT_FOUND, Message = texts.Get(ImportTextKeys.JOB_NOT_FOUND) },
            UploadCompletionOutcome.InvalidState => new CompleteUploadResponse { Code = ImportCommandCodes.INVALID_STATE, Message = texts.Get(ImportTextKeys.UPLOAD_NOT_COMPLETABLE) },
            UploadCompletionOutcome.Inconsistent => new CompleteUploadResponse
            {
                Code = ImportCommandCodes.INCONSISTENT,
                Message = texts.Get(ImportTextKeys.UPLOAD_INCONSISTENT),
                MissingChunks = result.MissingChunks ?? [],
            },
            _ => new CompleteUploadResponse { Code = ImportCommandCodes.INVALID_STATE, Message = texts.Get(ImportTextKeys.UPLOAD_NOT_COMPLETABLE) },
        };
    }

    public ImportActionResponse Cancel(string? jobId, int userId)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return ActionFailed(ImportCommandCodes.INVALID_REQUEST, ImportTextKeys.INVALID_JOB_ID);
        }

        if (!TryRead(id, out _, out var status) || status is null)
        {
            return ActionFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND);
        }

        var result = processor.Cancel(id, UserOrNull(userId));

        return result.Outcome switch
        {
            ImportCancelOutcome.Cancelled when result.Status is not null => new ImportActionResponse { Code = ImportCommandCodes.OK, Status = ToDto(id, result.Status) },
            ImportCancelOutcome.AlreadyFinished => ActionFailed(ImportCommandCodes.INVALID_STATE, ImportMessages.InvalidState(EffectiveState(result.Status ?? status, UtcNow()))),
            ImportCancelOutcome.JobNotFound or _ => ActionFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND),
        };
    }

    public ImportActionResponse DeleteFiles(string? jobId, int userId)
    {
        if (!ImportJobId.TryParse(jobId, out var id))
        {
            return ActionFailed(ImportCommandCodes.INVALID_REQUEST, ImportTextKeys.INVALID_JOB_ID);
        }

        if (!store.JobExists(id) || !cleanup.DeleteImportFiles(id, UserOrNull(userId)))
        {
            return ActionFailed(ImportCommandCodes.JOB_NOT_FOUND, ImportTextKeys.JOB_NOT_FOUND);
        }

        return TryRead(id, out _, out var status) && status is not null
            ? new ImportActionResponse { Code = ImportCommandCodes.OK, Status = ToDto(id, status) }
            : new ImportActionResponse { Code = ImportCommandCodes.OK };
    }

    private static bool TryBuildConfig(
        SaveConfigRequest? request,
        out ImportFileDescriptor file,
        out string keyField,
        out IReadOnlyList<ImportFieldMapping> mapping,
        out string dateFormat,
        out ImportConsentMapping? consentMapping)
    {
        file = new ImportFileDescriptor { Name = "", Size = 0, Fingerprint = "" };
        keyField = "";
        mapping = [];
        dateFormat = "";
        consentMapping = null;

        if (request?.File is not { Name: { } name, Fingerprint: { } fingerprint } requestFile
            || request.KeyField is null
            || request.Mapping is null
            || request.Mapping.Any(m => m?.Source is null || m.Target is null))
        {
            return false;
        }

        file = new ImportFileDescriptor { Name = name, Size = requestFile.Size, Fingerprint = fingerprint.ToLowerInvariant() };
        keyField = request.KeyField;
        mapping = [.. request.Mapping.Select(m => new ImportFieldMapping { Source = m.Source!, Target = m.Target! })];
        dateFormat = request.DateFormat ?? "iso";
        consentMapping = ConsentMapping(request.ConsentMapping);

        return IsKnownDateFormat(dateFormat);
    }

    /// <summary>Consent columns of a request; <c>null</c> when no column is named (empty names count as not named).</summary>
    private static ImportConsentMapping? ConsentMapping(ImportConsentMappingDto? request)
    {
        string? agreed = string.IsNullOrEmpty(request?.Agreed) ? null : request.Agreed;
        string? date = string.IsNullOrEmpty(request?.Date) ? null : request.Date;

        return agreed is null && date is null ? null : new ImportConsentMapping { Agreed = agreed, Date = date };
    }

    /// <summary>A "consent given" column is mapped: consent can be recorded for the rows that say yes.</summary>
    private static bool HasConsentColumn(ImportJob job) => job.ConsentMapping?.Agreed is not null;

    private static Dictionary<int, string> ChoiceNames(IReadOnlyList<ImportChoiceDto> items) =>
        items.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First().Name);

    /// <summary>
    /// Problem of the consent options that <see cref="ImportJobConfigValidator"/> does not report: consent per row needs
    /// the "consent given" column, not only a consent date column (the validator reports no consent columns at all,
    /// and the audience resolver a missing list or consent).
    /// </summary>
    private List<string> ValidateAudience(ImportJob job) =>
        job.Options.Consent == ConsentMode.BlanketWithColumnOverride && job.ConsentMapping is not null && !HasConsentColumn(job)
            ? [texts.Get(ImportTextKeys.CONSENT_COLUMN_REQUIRED)]
            : [];

    // Formats the browser can parse (Client/src/import-core/contract.ts DateFormat).
    private static bool IsKnownDateFormat(string dateFormat) => dateFormat is "iso" or "dmy" or "mdy";

    /// <summary>Stored states in which the Options step can change the options.</summary>
    private static bool AcceptsOptions(ImportJobState state) =>
        state is ImportJobState.Uploaded or ImportJobState.Analyzing or ImportJobState.Analyzed or ImportJobState.Confirmed;

    /// <summary>The import (not only the analysis) has started: the running counts are those of the import.</summary>
    private static bool ImportStarted(ImportJobStatus status) =>
        status.Checkpoint.Phase == ImportPhase.Execute && status.State is not (ImportJobState.Uploaded or ImportJobState.Configuring or ImportJobState.Uploading);

    /// <summary>
    /// Whether stored report parts of the phase belong to the current analysis or import. Parts of an analysis that
    /// was reset (options changed) stay on disk until the next analysis starts.
    /// </summary>
    private static bool IsReportCurrent(ImportJobStatus status, ImportPhase phase) =>
        phase == ImportPhase.Analyze ? status.Analysis is not null : ImportStarted(status);

    private static bool TryParsePhase(string? value, out ImportPhase phase)
    {
        phase = ImportPhase.Analyze;
        switch (value)
        {
            case "analyze":
                return true;
            case "execute":
                phase = ImportPhase.Execute;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The email field is mapped and is not the key: only then can an uploaded row have an invalid email.</summary>
    private static bool InvalidEmailApplies(ImportJob job) =>
        !KeyNormalizer.IsEmailField(job.KeyField) && job.Mapping.Any(m => KeyNormalizer.IsEmailField(m.Target));

    /// <summary>
    /// Builds the options from the request on top of the stored ones (options of later versions are kept).
    /// <c>false</c> when a value is not one of the known option names.
    /// </summary>
    private static bool TryBuildOptions(ImportOptionsDto request, ImportJob job, out ImportOptions options)
    {
        options = job.Options;
        if (!TryParseOption<NewContactsBehavior>(request.NewContacts, out var newContacts)
            || !TryParseOption<ExistingContactsBehavior>(request.ExistingContacts, out var existingContacts)
            || !TryParseOption<EmptyValuesBehavior>(request.EmptyValues, out var emptyValues)
            || !TryParseOption<MultipleMatchesBehavior>(request.MultipleMatches, out var multipleMatches)
            || !TryParseOption<InvalidEmailBehavior>(request.InvalidEmail, out var invalidEmail)
            || !TryParseOption<SourceStampBehavior>(request.SourceStamp, out var sourceStamp)
            || !TryParseOption<UnsubscribedBehavior>(request.Unsubscribed ?? OptionName(UnsubscribedBehavior.Keep), out var unsubscribed)
            || !TryParseOption<BouncedAddressBehavior>(request.BouncedAddress ?? OptionName(BouncedAddressBehavior.AddAndReport), out var bouncedAddress)
            || !TryParseOption<ConsentMode>(request.Consent ?? OptionName(ConsentMode.None), out var consent)
            || !TryParseOption<RevokedConsentBehavior>(request.RevokedConsent ?? OptionName(RevokedConsentBehavior.Keep), out var revokedConsent)
            || request.RecipientListId is <= 0
            || request.ConsentId is <= 0)
        {
            return false;
        }

        string? sourceField = string.IsNullOrWhiteSpace(request.SourceField) ? null : request.SourceField;
        options = job.Options with
        {
            NewContacts = newContacts,
            ExistingContacts = existingContacts,
            EmptyValues = emptyValues,
            MultipleMatches = multipleMatches,
            InvalidEmail = InvalidEmailApplies(job) ? invalidEmail : InvalidEmailBehavior.SkipRow,
            SourceField = sourceField,
            SourceValue = sourceField is null ? null : request.SourceValue?.Trim(),
            SourceStamp = sourceField is null ? SourceStampBehavior.CreatedOnly : sourceStamp,
            ReportIncludesKey = request.ReportIncludesKey,
            // Options that do not apply are stored as their defaults, which change nothing (keep unsubscribed, keep
            // revoked): a choice like "subscribe again" never stays behind for a list chosen later.
            RecipientListId = request.RecipientListId,
            Unsubscribed = request.RecipientListId is null ? UnsubscribedBehavior.Keep : unsubscribed,
            BouncedAddress = request.RecipientListId is null ? BouncedAddressBehavior.AddAndReport : bouncedAddress,
            Consent = consent,
            ConsentId = consent == ConsentMode.None ? null : request.ConsentId,
            RevokedConsent = consent == ConsentMode.None ? RevokedConsentBehavior.Keep : revokedConsent,
        };

        return true;
    }

    /// <summary>Parses the camelCase name of an enum value; numbers and other spellings are refused.</summary>
    private static bool TryParseOption<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        var matches = Enum.GetValues<TEnum>().Where(candidate => string.Equals(OptionName(candidate), value, StringComparison.Ordinal)).ToArray();
        result = matches.Length == 1 ? matches[0] : default;

        return matches.Length == 1;
    }

    private static string OptionName<TEnum>(TEnum value) where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static ImportOptionsDto OptionsDto(ImportOptions options) => new()
    {
        NewContacts = OptionName(options.NewContacts),
        ExistingContacts = OptionName(options.ExistingContacts),
        EmptyValues = OptionName(options.EmptyValues),
        MultipleMatches = OptionName(options.MultipleMatches),
        InvalidEmail = OptionName(options.InvalidEmail),
        SourceField = options.SourceField,
        SourceValue = options.SourceValue,
        SourceStamp = OptionName(options.SourceStamp),
        ReportIncludesKey = options.ReportIncludesKey,
        RecipientListId = options.RecipientListId,
        Unsubscribed = OptionName(options.Unsubscribed),
        BouncedAddress = OptionName(options.BouncedAddress),
        Consent = OptionName(options.Consent),
        ConsentId = options.ConsentId,
        RevokedConsent = OptionName(options.RevokedConsent),
    };

    private static ImportAggregatesDto AggregatesDto(ImportAggregates aggregates) => new()
    {
        Rows = aggregates.Rows,
        Created = aggregates.Created,
        Updated = aggregates.Updated,
        Unchanged = aggregates.Unchanged,
        ContactsUpdated = aggregates.ContactsUpdated,
        SkippedNew = aggregates.SkippedNew,
        SkippedExisting = aggregates.SkippedExisting,
        SkippedMultipleMatches = aggregates.SkippedMultipleMatches,
        SkippedInvalidEmail = aggregates.SkippedInvalidEmail,
        SkippedInvalidValue = aggregates.SkippedInvalidValue,
        Skipped = aggregates.Skipped,
        DuplicateRows = aggregates.DuplicateRows,
        MultipleMatches = aggregates.MultipleMatches,
        ImportedWithoutEmail = aggregates.ImportedWithoutEmail,
        ListAdded = aggregates.ListAdded,
        ListAlreadySubscribed = aggregates.ListAlreadySubscribed,
        ListKeptUnsubscribed = aggregates.ListKeptUnsubscribed,
        ListResubscribed = aggregates.ListResubscribed,
        ListSkippedBounced = aggregates.ListSkippedBounced,
        ListSkippedNoEmail = aggregates.ListSkippedNoEmail,
        BouncedHard = aggregates.BouncedHard,
        BouncedSoftOverLimit = aggregates.BouncedSoftOverLimit,
        BouncedSoftUnderLimit = aggregates.BouncedSoftUnderLimit,
        ConsentRecorded = aggregates.ConsentRecorded,
        ConsentAlreadyAgreed = aggregates.ConsentAlreadyAgreed,
        ConsentKeptRevoked = aggregates.ConsentKeptRevoked,
        ConsentReAgreed = aggregates.ConsentReAgreed,
        ConsentNotGiven = aggregates.ConsentNotGiven,
        ConsentSkippedFutureDate = aggregates.ConsentSkippedFutureDate,
        Fields = [.. aggregates.Fields.Select(f => new ImportFieldChangeDto(f.Field, f.Changed, f.Cleared))],
    };

    private ImportProgressDto ProgressDto(ImportProgress progress) => new()
    {
        Outcome = JsonNamingPolicy.CamelCase.ConvertName(progress.Outcome.ToString()),
        Phase = ImportReportFormat.PhaseName(progress.Phase),
        State = progress.State.ToString(),
        DriverToken = progress.DriverToken,
        RowsProcessed = progress.RowsProcessed,
        RowsTotal = progress.RowsTotal,
        Aggregates = AggregatesDto(progress.Aggregates),
        Analysis = progress.Analysis is null ? null : AggregatesDto(progress.Analysis),
        AnalyzedUtc = progress.AnalyzedUtc,
        Error = progress.Error is null ? null : texts.Get(progress.Error),
    };

    /// <summary>Suggested source stamp: <c>import-{date the import was created}-{file name}</c>.</summary>
    internal static string DefaultSourceValue(ImportJob job) =>
        string.Create(CultureInfo.InvariantCulture, $"import-{job.CreatedUtc:yyyy-MM-dd}-{job.File.Name}");

    private ImportJobDto JobDto(ImportJob job)
    {
        var mapped = job.Mapping.Select(m => m.Target).ToHashSet(StringComparer.Ordinal);
        string? recipientListName = job.Options.RecipientListId is { } listId
            ? choices.GetRecipientLists().FirstOrDefault(list => list.Id == listId)?.Name
            : null;
        string? consentName = job.Options.Consent != ConsentMode.None && job.Options.ConsentId is { } consentId
            ? choices.GetConsents().FirstOrDefault(consent => consent.Id == consentId)?.Name
            : null;

        return new ImportJobDto
        {
            JobId = ImportJobId.Format(job.JobId),
            File = new ImportFileDto { Name = job.File.Name, Size = job.File.Size, Fingerprint = job.File.Fingerprint },
            KeyField = job.KeyField,
            Mapping = [.. job.Mapping.Select(m => new ImportMappingDto { Source = m.Source, Target = m.Target })],
            DateFormat = job.DateFormat,
            ConsentMapping = job.ConsentMapping is null ? null : new ImportConsentMappingDto { Agreed = job.ConsentMapping.Agreed, Date = job.ConsentMapping.Date },
            CreatedUtc = job.CreatedUtc,
            Options = OptionsDto(job.Options),
            InvalidEmailApplies = InvalidEmailApplies(job),
            SourceFields = [.. schemaProvider.GetMappableFields().Where(f => f.IsText && !mapped.Contains(f.Name)).Select(FieldDto)],
            DefaultSourceValue = DefaultSourceValue(job),
            RecipientListName = recipientListName,
            ConsentName = consentName,
            AttestationRequired = ImportAttestationPolicy.IsRequired(job.Options),
            AttestationStatement = texts.Get(ImportTextKeys.ATTESTATION_STATEMENT),
            AttestationVersion = ImportAttestationPolicy.StatementVersion,
            Attestation = AttestationDto(job.Attestation),
        };
    }

    private ImportAttestationDto? AttestationDto(ImportAttestation? attestation)
    {
        if (attestation is null)
        {
            return null;
        }

        string userName = userNames.GetUserNames([attestation.UserId]).GetValueOrDefault(attestation.UserId, "");

        return new ImportAttestationDto(userName, attestation.Utc, attestation.StatementVersion);
    }

    private static ImportFieldDto FieldDto(ContactFieldDefinition field) =>
        new(field.Name, field.Caption, field.DataType, field.MaxLength, field.IsSupported, field.CanBeKey, field.IsReference, field.Reference?.ObjectType);

    /// <summary>The job as the client shows it: configuration, state, progress and counts, report availability.</summary>
    private ImportJobResponse JobResponse(string code, ImportJob job, ImportJobStatus status)
    {
        var jobId = job.JobId;
        var progress = new ImportProgress
        {
            Outcome = ImportProcessOutcome.Processed,
            Phase = status.Checkpoint.Phase,
            State = EffectiveState(status, UtcNow()),
            RowsProcessed = status.Aggregates.GetValueOrDefault(ImportAggregateKeys.Rows),
            RowsTotal = status.Profile.RowsValid,
            Aggregates = ImportAggregates.From(status.Aggregates),
            Analysis = status.Analysis is null ? null : ImportAggregates.From(status.Analysis.Aggregates),
            AnalyzedUtc = status.Analysis?.FinishedUtc,
            Error = status.ErrorMessage,
        };

        return new ImportJobResponse
        {
            Code = code,
            Job = JobDto(job),
            Status = ToDto(jobId, status),
            Progress = ProgressDto(progress),
            Reports = new ImportReportsDto(
                IsReportCurrent(status, ImportPhase.Analyze) && reportPager.HasReport(jobId, ImportPhase.Analyze),
                IsReportCurrent(status, ImportPhase.Execute) && reportPager.HasReport(jobId, ImportPhase.Execute),
                job.Options.ReportIncludesKey),
        };
    }

    private ImportJobResponse JobFailed(string code, string textKey) => JobFailed(code, new ImportMessage(textKey));

    private ImportJobResponse JobFailed(string code, ImportMessage message) =>
        new() { Code = code, Message = texts.Get(message) };

    private static IReadOnlyList<ImportChunkDto> ToDto(IReadOnlyList<ImportChunkInfo> chunks) =>
        [.. chunks.Select(c => new ImportChunkDto(c.Index, c.Sha256, c.Rows))];

    private static string ToCamelCase(ChunkRejectReason reason) => JsonNamingPolicy.CamelCase.ConvertName(reason.ToString());

    private static UploadChunkResponse ChunkRejected(ChunkRejectReason reason, string? message, long? rowNumber = null) =>
        new() { Code = ImportCommandCodes.REJECTED, Reason = ToCamelCase(reason), Message = message, RowNumber = rowNumber };

    /// <summary>
    /// A chunk the ingest refused: the user gets the message of the reason; the detailed reason (English, names fields
    /// and rows but no values) goes to the log.
    /// </summary>
    private UploadChunkResponse RejectedByIngest(Guid jobId, int chunkIndex, ChunkIngestResult result)
    {
        var reason = result.Reason ?? ChunkRejectReason.InvalidPayload;
        logger?.LogWarning("Contact import {JobId}: uploaded chunk {Chunk} was refused ({Reason}): {Detail}", jobId, chunkIndex, reason, result.Message);

        return ChunkRejected(reason, texts.Get(ImportTextKeys.ChunkRejected(reason)), result.RowNumber);
    }

    private ImportStatusDto ToDto(Guid jobId, ImportJobStatus status) => new()
    {
        State = EffectiveState(status, UtcNow()).ToString(),
        Chunks = ToDto(status.State == ImportJobState.Uploading ? store.ListChunks(jobId) : status.Chunks),
        Profile = new ImportProfileDto
        {
            RowsRead = status.Profile.RowsRead,
            RowsValid = status.Profile.RowsValid,
            RowsRejected = status.Profile.RowsRejected,
            LikelyDuplicates = status.Profile.LikelyDuplicates,
            ChunkCount = status.Profile.ChunkCount,
        },
        UpdatedUtc = status.UpdatedUtc,
        Error = status.ErrorMessage is { } error ? texts.Get(error) : null,
    };

    private SaveConfigResponse SaveFailed(string code, string textKey) => SaveFailed(code, new ImportMessage(textKey));

    private SaveConfigResponse SaveFailed(string code, ImportMessage message) =>
        new() { Code = code, Message = texts.Get(message) };

    private ImportActionResponse ActionFailed(string code, string textKey) => ActionFailed(code, new ImportMessage(textKey));

    private ImportActionResponse ActionFailed(string code, ImportMessage message) =>
        new() { Code = code, Message = texts.Get(message) };

    private ImportJobState EffectiveState(ImportJobStatus status, DateTime utcNow) =>
        ImportJobStateMachine.GetEffectiveState(status, utcNow, options.Value.HeartbeatStale);

    private bool HasFiles(Guid jobId) => store.HasChunks(jobId) || store.ListReports(jobId).Count > 0;

    /// <summary>Deletes stored chunks from the highest index down, so an interruption never leaves a gap below a kept chunk.</summary>
    private int DeleteChunksFrom(Guid jobId, int fromIndex)
    {
        int deleted = 0;
        foreach (var chunk in store.ListChunks(jobId).Where(c => c.Index >= fromIndex).OrderByDescending(c => c.Index))
        {
            store.DeleteChunk(jobId, chunk.Index);
            deleted++;
        }

        return deleted;
    }

    /// <summary>Reads job and status; <c>false</c> when a file exists but cannot be read.</summary>
    private bool TryRead(Guid jobId, out ImportJob? job, out ImportJobStatus? status)
    {
        job = null;
        status = null;
        try
        {
            job = store.ReadJob(jobId);
            status = store.ReadStatus(jobId);

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>User ID for the event log; <c>null</c> when no user is signed in.</summary>
    private static int? UserOrNull(int userId) => userId > 0 ? userId : null;
}
