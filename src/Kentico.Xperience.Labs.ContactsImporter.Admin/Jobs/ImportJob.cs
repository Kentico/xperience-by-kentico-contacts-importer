namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Import job configuration, stored as <c>job.json</c>.
/// </summary>
internal sealed record ImportJob
{
    public required Guid JobId { get; init; }

    public required int CreatedByUserId { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required ImportFileDescriptor File { get; init; }

    /// <summary>
    /// Target contact field used as the dedupe key. Must be one of the <see cref="Mapping"/> targets.
    /// </summary>
    public required string KeyField { get; init; }

    public required IReadOnlyList<ImportFieldMapping> Mapping { get; init; }

    /// <summary>
    /// Date format the browser used to parse date columns (<c>iso</c> or a chosen format). Dates arrive as ISO strings.
    /// </summary>
    public string DateFormat { get; init; } = "iso";

    /// <summary>
    /// Source columns that say whether and when a row's contact gave consent. Applied in the browser, like the field
    /// mapping, so a change needs a new upload. <c>null</c> = no consent columns; uploaded records then have no
    /// <c>c</c> property, otherwise every record has one. Used only with <see cref="ConsentMode.BlanketWithColumnOverride"/>.
    /// </summary>
    public ImportConsentMapping? ConsentMapping { get; init; }

    public ImportOptions Options { get; init; } = new();

    public ImportAttestation? Attestation { get; init; }
}

/// <summary>
/// Source file identity. The file itself never reaches the server.
/// </summary>
internal sealed record ImportFileDescriptor
{
    public required string Name { get; init; }

    public required long Size { get; init; }

    /// <summary>
    /// Hex hash of the file size and its first and last megabyte; lets the browser resume an upload.
    /// </summary>
    public required string Fingerprint { get; init; }
}

/// <summary>
/// Maps a source file column to a target contact field.
/// </summary>
internal sealed record ImportFieldMapping
{
    public required string Source { get; init; }

    public required string Target { get; init; }
}

/// <summary>
/// Source file columns with the consent of each row (wire: <c>"consentMapping": {"agreed": "...", "date": "..."}</c>).
/// At least one of the columns is set.
/// </summary>
internal sealed record ImportConsentMapping
{
    /// <summary>Column saying whether consent was given. <c>null</c> = not mapped: every row counts as consent given.</summary>
    public string? Agreed { get; init; }

    /// <summary>Column with the date (or date and time) the consent was given. <c>null</c> = not mapped: the import time is used.</summary>
    public string? Date { get; init; }
}

/// <summary>
/// Who confirmed the import and which consent statement they saw (PLAN D26).
/// </summary>
internal sealed record ImportAttestation
{
    public required int UserId { get; init; }

    public required DateTime Utc { get; init; }

    public required string StatementVersion { get; init; }
}
