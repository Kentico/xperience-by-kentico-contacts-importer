namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// <c>UPLOAD_CHUNK</c> page command payload. Everything in it comes from the browser and is untrusted.
/// </summary>
internal sealed record UploadChunkPayload
{
    /// <summary>Job ID in canonical GUID form.</summary>
    public string? JobId { get; init; }

    /// <summary>0-based chunk index in file order.</summary>
    public int Index { get; init; }

    /// <summary>Hex SHA-256 of the gzipped bytes.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Number of records in the chunk.</summary>
    public int Rows { get; init; }

    /// <summary>Base64 of the gzipped NDJSON.</summary>
    public string? Data { get; init; }
}

internal enum ChunkIngestOutcome
{
    /// <summary>The chunk was validated and stored.</summary>
    Stored,

    /// <summary>A chunk with the same index and hash is already stored; nothing changed.</summary>
    AlreadyStored,

    /// <summary>The chunk was refused; nothing was stored.</summary>
    Rejected,
}

internal enum ChunkRejectReason
{
    /// <summary>A payload property is missing or malformed (job ID, index, hash, row count, base64).</summary>
    InvalidPayload,
    JobNotFound,

    /// <summary>The job does not accept chunks in its current state.</summary>
    InvalidState,

    /// <summary>The job configuration no longer matches the contact class (a mapped field is gone).</summary>
    InvalidConfiguration,

    /// <summary>The gzipped chunk is larger than allowed.</summary>
    TooLarge,

    /// <summary>With this chunk, the job would store more uploaded data than the file size limit allows.</summary>
    UploadTooLarge,
    HashMismatch,
    InvalidGzip,

    /// <summary>The chunk decompresses to more than allowed.</summary>
    DecompressedTooLarge,

    /// <summary>A record breaks the chunk contract; see the message and row number.</summary>
    InvalidRecord,

    /// <summary>The number of records differs from the declared row count.</summary>
    RowCountMismatch,

    /// <summary>Row numbers do not increase, within the chunk or against the neighboring chunks.</summary>
    RowOrder,
}

/// <param name="Outcome">What happened to the chunk.</param>
/// <param name="Reason">Why the chunk was rejected; <c>null</c> otherwise.</param>
/// <param name="Message">Description of the rejection without any contact data; <c>null</c> otherwise.</param>
/// <param name="RowNumber">Source file row number (<c>r</c>) of the offending record, when known.</param>
internal sealed record ChunkIngestResult(ChunkIngestOutcome Outcome, ChunkRejectReason? Reason = null, string? Message = null, long? RowNumber = null)
{
    public bool IsAccepted => Outcome != ChunkIngestOutcome.Rejected;

    public static ChunkIngestResult Rejected(ChunkRejectReason reason, string message, long? rowNumber = null) =>
        new(ChunkIngestOutcome.Rejected, reason, message, rowNumber);
}

internal enum UploadCompletionOutcome
{
    /// <summary>All chunks are consistent; the job is now Uploaded.</summary>
    Completed,
    JobNotFound,

    /// <summary>The job is not in the Uploading state.</summary>
    InvalidState,

    /// <summary>Chunks are missing, out of order, or do not match the profile.</summary>
    Inconsistent,
}

/// <param name="Outcome">Result of completing the upload.</param>
/// <param name="Status">Stored status when completed; <c>null</c> otherwise.</param>
/// <param name="Message">Description of the problem; <c>null</c> when completed.</param>
/// <param name="MissingChunks">Indexes of chunks the browser still has to upload (first 100).</param>
internal sealed record UploadCompletionResult(UploadCompletionOutcome Outcome, ImportJobStatus? Status = null, string? Message = null, IReadOnlyList<int>? MissingChunks = null);
