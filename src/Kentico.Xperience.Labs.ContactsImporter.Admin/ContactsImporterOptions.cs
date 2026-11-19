namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Contacts Importer settings. Bound from the <c>ContactsImporter</c> configuration section and
/// optionally adjusted in code via <c>AddContactsImporter</c>.
/// </summary>
public class ContactsImporterOptions
{
    /// <summary>
    /// Name of the configuration section the options are bound from.
    /// </summary>
    public const string SectionName = "ContactsImporter";

    /// <summary>
    /// Root folder for import job files (job configuration, status, uploaded chunks, reports).
    /// Application-relative (<c>~/...</c>) or absolute. Accessed through CMS.IO, so the path can be mapped to
    /// shared storage. Must be shared by all instances in a web farm or SaaS environment.
    /// The NuGet package keeps files under the default path out of the build output and the published application
    /// of the project that installs it; for another path inside the project folder, exclude it in the project file.
    /// </summary>
    public string StoragePath { get; set; } = "~/App_Data/contacts-importer";

    /// <summary>
    /// Maximum size of the source file a marketer can import, in bytes. Default 5 GB.
    /// The server also limits the uploaded (compressed) data of one import to twice this size.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Target size of one uploaded chunk (gzipped, before base64 encoding), in bytes. Default 4 MB.
    /// The server accepts chunks up to twice this size. The base64 encoded chunk (about 4/3 of the size)
    /// must fit into the request body size limit of the application.
    /// </summary>
    public int ChunkSizeBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum size of one chunk after decompression, in bytes. Chunks that decompress to more are rejected.
    /// Default 128 MB.
    /// </summary>
    public long MaxChunkUncompressedBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// Number of rows processed in one database batch. Default 1000.
    /// With <see cref="AdaptiveBatchSize"/>, the largest batch the import (execute phase) uses.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Whether the import (execute phase) adapts its batch size to the database: batches slower than
    /// <see cref="TargetBatchSeconds"/> get smaller, clearly faster ones larger, between <see cref="MinBatchSize"/>
    /// and <see cref="BatchSize"/>. A database timeout or deadlock halves the batch at once. Protects the live site
    /// on small database plans. <c>false</c> = every batch has <see cref="BatchSize"/> rows. Default <c>true</c>.
    /// The analysis (dry run) only reads and always uses <see cref="BatchSize"/>.
    /// </summary>
    public bool AdaptiveBatchSize { get; set; } = true;

    /// <summary>
    /// Seconds of database work one import batch aims for when <see cref="AdaptiveBatchSize"/> is on. Default 2.
    /// </summary>
    public double TargetBatchSeconds { get; set; } = 2;

    /// <summary>
    /// Smallest batch the import uses when <see cref="AdaptiveBatchSize"/> is on. Default 50.
    /// A value above <see cref="BatchSize"/> counts as <see cref="BatchSize"/> (fixed batch size).
    /// </summary>
    public int MinBatchSize { get; set; } = 50;

    /// <summary>
    /// Seconds one processing request works on batches before it returns progress to the browser. Default 15.
    /// Keep well below the request timeouts of the hosting environment (CDN, reverse proxy).
    /// </summary>
    public int ProcessingTimeBudgetSeconds { get; set; } = 15;

    /// <summary>
    /// Hours a finished analysis (dry run) stays valid. An import confirmed or started later must be analyzed again. Default 24.
    /// </summary>
    public int AnalysisValidHours { get; set; } = 24;

    /// <summary>
    /// Seconds without a processing heartbeat after which a job being analyzed or run counts as paused
    /// and another browser tab or user may continue it. Default 60.
    /// </summary>
    public int HeartbeatStaleSeconds { get; set; } = 60;

    /// <summary>
    /// Days a row report without contact data is kept after an import finishes. Default 7.
    /// </summary>
    public int ReportRetentionDays { get; set; } = 7;

    /// <summary>
    /// Hours a row report that includes the key column (personal data) is kept after an import finishes. Default 24.
    /// </summary>
    public int ReportWithKeysRetentionHours { get; set; } = 24;

    /// <summary>
    /// Days without activity after which an unfinished import counts as abandoned; its uploaded data and
    /// reports are deleted. Default 7.
    /// </summary>
    public int AbandonedJobRetentionDays { get; set; } = 7;

    /// <summary>
    /// Days the status of a finished import (counts only, no contact data) is kept for the recent imports list. Default 90.
    /// </summary>
    public int StatusRetentionDays { get; set; } = 90;

    /// <summary>
    /// Largest accepted gzipped chunk in bytes.
    /// </summary>
    internal long MaxChunkCompressedBytes => 2L * ChunkSizeBytes;

    /// <summary>
    /// Most gzipped chunk bytes one job may store. The file itself never reaches the server, so the file size limit
    /// is enforced on what does: twice <see cref="MaxFileSizeBytes"/>, because the upload of a compressed source file
    /// (mapped columns as gzipped NDJSON, key repeated) can be larger than the file. Protects the storage from a
    /// client that ignores the file size limit; parallel uploads can exceed it by the chunks in flight.
    /// </summary>
    internal long MaxUploadBytes => MaxFileSizeBytes > long.MaxValue / 2 ? long.MaxValue : 2 * MaxFileSizeBytes;

    /// <summary>
    /// Smallest import batch: <see cref="MinBatchSize"/>, at most <see cref="BatchSize"/>.
    /// </summary>
    internal int EffectiveMinBatchSize => Math.Min(MinBatchSize, BatchSize);

    internal TimeSpan ProcessingTimeBudget => TimeSpan.FromSeconds(ProcessingTimeBudgetSeconds);

    /// <summary>
    /// Longest time an import call waits for the database lock of its job while another call holds it.
    /// </summary>
    internal TimeSpan JobLockTimeout => TimeSpan.FromSeconds(Math.Clamp(ProcessingTimeBudgetSeconds, 1, 10));

    internal TimeSpan AnalysisValid => TimeSpan.FromHours(AnalysisValidHours);

    internal TimeSpan HeartbeatStale => TimeSpan.FromSeconds(HeartbeatStaleSeconds);
}
