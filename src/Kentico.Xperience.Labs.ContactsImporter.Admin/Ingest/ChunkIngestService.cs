using System.IO.Compression;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Receives uploaded chunks: verifies, validates and stores them, and completes the upload.
/// </summary>
internal interface IChunkIngestService
{
    /// <summary>
    /// Verifies the hash, decompresses (size capped), validates every record against the job mapping and the
    /// contact class, and stores the chunk. Uploading the same index with the same hash again is accepted without
    /// changes; a different hash replaces the chunk only while the job is Uploading. A chunk that would take the stored
    /// data of the job over the limit derived from the maximum file size is refused. Never throws for bad input.
    /// </summary>
    public ChunkIngestResult Ingest(UploadChunkPayload payload);

    /// <summary>
    /// Finishes the upload: checks that chunks <c>0..n-1</c> are all stored, row numbers increase across chunks and
    /// the totals match <paramref name="profile"/>, then stores the chunk list and profile and moves the job to Uploaded.
    /// </summary>
    public UploadCompletionResult CompleteUpload(Guid jobId, ImportProfile profile);
}

internal sealed class ChunkIngestService(
    IImportJobStore store,
    IContactFieldSchemaProvider schemaProvider,
    IOptions<ContactsImporterOptions> options,
    TimeProvider timeProvider) : IChunkIngestService
{
    /// <summary>Highest accepted chunk index + 1.</summary>
    internal const int MaxChunkCount = 1_000_000;

    private const int Sha256HexLength = 64;
    private const int MaxReportedMissingChunks = 100;

    public ChunkIngestResult Ingest(UploadChunkPayload payload)
    {
        if (!ImportJobId.TryParse(payload.JobId, out var jobId))
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidPayload, "The job ID is not valid.");
        }

        if (payload.Index is < 0 or >= MaxChunkCount)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidPayload, "The chunk index is out of range.");
        }

        if (payload.Rows < 1)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidPayload, "The chunk must declare at least one row.");
        }

        if (payload.Sha256 is not { Length: Sha256HexLength } sha256 || !sha256.All(char.IsAsciiHexDigit))
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidPayload, "The chunk hash is not a SHA-256 hex string.");
        }

        sha256 = sha256.ToLowerInvariant();

        long maxCompressedBytes = options.Value.MaxChunkCompressedBytes;
        if (string.IsNullOrEmpty(payload.Data))
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidPayload, "The chunk has no data.");
        }

        if (payload.Data.Length > ((maxCompressedBytes + 2) / 3 * 4))
        {
            return TooLarge(maxCompressedBytes);
        }

        byte[] gzipped;
        try
        {
            gzipped = Convert.FromBase64String(payload.Data);
        }
        catch (FormatException)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidPayload, "The chunk data is not valid base64.");
        }

        if (gzipped.Length > maxCompressedBytes)
        {
            return TooLarge(maxCompressedBytes);
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(gzipped)), sha256, StringComparison.Ordinal))
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.HashMismatch, "The chunk data does not match its hash.");
        }

        return IngestVerified(jobId, payload.Index, payload.Rows, sha256, gzipped);
    }

    public UploadCompletionResult CompleteUpload(Guid jobId, ImportProfile profile)
    {
        var status = store.ReadStatus(jobId);
        if (status is null)
        {
            return new UploadCompletionResult(UploadCompletionOutcome.JobNotFound, Message: "The import job does not exist.");
        }

        if (status.State != ImportJobState.Uploading)
        {
            return new UploadCompletionResult(UploadCompletionOutcome.InvalidState, Message: $"The upload cannot be completed while the import is {status.State}.");
        }

        if (profile.RowsRead < 0 || profile.RowsValid < 0 || profile.RowsRejected < 0 || profile.LikelyDuplicates < 0 || profile.ChunkCount < 1)
        {
            return Inconsistent("The upload has no chunks or its statistics are not valid.");
        }

        var chunks = store.ListChunks(jobId);
        var stored = chunks.Select(c => c.Index).ToHashSet();
        int[] missing = [.. Enumerable.Range(0, Math.Min(profile.ChunkCount, MaxChunkCount)).Where(i => !stored.Contains(i)).Take(MaxReportedMissingChunks)];
        if (missing.Length > 0)
        {
            return Inconsistent("Some chunks are not uploaded yet.", missing);
        }

        if (chunks.Count != profile.ChunkCount)
        {
            return Inconsistent($"{chunks.Count} chunks are stored but the upload declares {profile.ChunkCount}.");
        }

        for (int i = 1; i < chunks.Count; i++)
        {
            if (chunks[i].FirstRow <= chunks[i - 1].LastRow)
            {
                return Inconsistent($"Row numbers of chunk {i} do not follow chunk {i - 1}.");
            }
        }

        long rows = chunks.Sum(c => (long)c.Rows);
        if (rows != profile.RowsValid || profile.RowsValid > profile.RowsRead)
        {
            return Inconsistent("The uploaded rows do not match the upload statistics.");
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var updated = store.UpdateStatus(jobId, current =>
            current.State == ImportJobState.Uploading
                ? ImportJobStateMachine.Transition(current, ImportJobState.Uploaded, utcNow) with { Chunks = chunks, Profile = profile }
                : current);

        return updated is { State: ImportJobState.Uploaded }
            ? new UploadCompletionResult(UploadCompletionOutcome.Completed, updated)
            : new UploadCompletionResult(UploadCompletionOutcome.InvalidState, Message: "The import changed while the upload was being completed.");
    }

    private static ChunkIngestResult TooLarge(long maxCompressedBytes) =>
        ChunkIngestResult.Rejected(ChunkRejectReason.TooLarge, $"The chunk is larger than {maxCompressedBytes} bytes.");

    private static UploadCompletionResult Inconsistent(string message, IReadOnlyList<int>? missingChunks = null) =>
        new(UploadCompletionOutcome.Inconsistent, Message: message, MissingChunks: missingChunks);

    private ChunkIngestResult IngestVerified(Guid jobId, int index, int rows, string sha256, byte[] gzipped)
    {
        var job = store.ReadJob(jobId);
        var status = store.ReadStatus(jobId);
        if (job is null || status is null)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.JobNotFound, "The import job does not exist.");
        }

        var existing = store.ReadChunkInfo(jobId, index);
        if (existing is not null && string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
        {
            return new ChunkIngestResult(ChunkIngestOutcome.AlreadyStored);
        }

        if (status.State != ImportJobState.Uploading)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidState, $"Chunks cannot be uploaded while the import is {status.State}.");
        }

        long maxUploadBytes = options.Value.MaxUploadBytes;
        if (gzipped.Length > maxUploadBytes - Math.Min(maxUploadBytes, store.GetChunksSize(jobId, index)))
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.UploadTooLarge, $"The uploaded data of the import would be larger than the allowed {maxUploadBytes} bytes.");
        }

        if (!ChunkValidationContext.TryCreate(job, schemaProvider.GetMappableFields(), out var context, out string? contextError))
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidConfiguration, contextError!);
        }

        ChunkValidationResult validation;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(gzipped, writable: false), CompressionMode.Decompress);
            validation = ChunkValidator.Validate(gzip, context!, options.Value.MaxChunkUncompressedBytes, rows);
        }
        catch (InvalidDataException)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.InvalidGzip, "The chunk data is not valid gzip.");
        }

        if (!validation.IsValid)
        {
            return ChunkIngestResult.Rejected(validation.Reason!.Value, validation.Message!, validation.RowNumber);
        }

        // Chunks upload in parallel, so only neighbors that are already stored can be checked here;
        // CompleteUpload checks the whole sequence.
        if (index > 0 && store.ReadChunkInfo(jobId, index - 1) is { } previous && validation.FirstRow <= previous.LastRow)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.RowOrder, $"Row numbers of chunk {index} do not follow chunk {index - 1}.", validation.FirstRow);
        }

        if (store.ReadChunkInfo(jobId, index + 1) is { } next && validation.LastRow >= next.FirstRow)
        {
            return ChunkIngestResult.Rejected(ChunkRejectReason.RowOrder, $"Row numbers of chunk {index} do not precede chunk {index + 1}.", validation.LastRow);
        }

        store.WriteChunk(
            jobId,
            new ImportChunkInfo { Index = index, Sha256 = sha256, Rows = validation.Rows, FirstRow = validation.FirstRow, LastRow = validation.LastRow },
            gzipped);

        return new ChunkIngestResult(ChunkIngestOutcome.Stored);
    }
}
