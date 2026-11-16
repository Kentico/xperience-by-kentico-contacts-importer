using System.IO.Compression;
using System.Text.Json;

using CMS.ContactManagement;
using CMS.DataEngine;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

using Microsoft.Extensions.Logging;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// A real import pipeline for one test: job files in a temporary folder, the real chunk validation, schema provider,
/// repository and cache invalidation against the test database.
/// </summary>
internal sealed class ImportHarness : IDisposable
{
    private readonly ChunkIngestService ingest;

    public ImportHarness(Action<ContactsImporterOptions>? configure = null, TimeProvider? timeProvider = null, IContactImportRepository? repository = null, IImportCacheInvalidator? cacheInvalidator = null)
    {
        Temp = new TempStore(configure);
        Time = timeProvider ?? TimeProvider.System;
        Schema = new ContactFieldSchemaProvider();
        Repository = repository ?? new ContactImportRepository(Provider<ContactInfo>.Instance);
        ingest = new ChunkIngestService(Temp.Store, Schema, Temp.Options, Time);
        var cleanup = new ImportJobCleanupService(Temp.Store, Temp.Options, Time, new ListLogger<ImportJobCleanupService>(Log));
        Processor = new ImportProcessor(Temp.Store, Schema, Repository, cleanup, cacheInvalidator ?? new ImportCacheInvalidator(), Temp.Options, Time, new ListLogger<ImportProcessor>(Log));
        Reports = new ImportReportReader(Temp.Store);
    }

    public TempStore Temp { get; }

    public ImportJobStore Store => Temp.Store;

    public TimeProvider Time { get; }

    public ContactFieldSchemaProvider Schema { get; }

    public IContactImportRepository Repository { get; }

    public ImportProcessor Processor { get; }

    public ImportReportReader Reports { get; }

    /// <summary>Everything the importer logged, formatted.</summary>
    public List<string> Log { get; } = [];

    /// <summary>
    /// Creates a job and uploads <paramref name="rows"/> through the real chunk validation. Each row has one value per
    /// target (<c>null</c> = the row has no value for the field). Returns the ID of the job, which is Uploaded.
    /// </summary>
    public Guid Upload(string keyField, string[] targets, IReadOnlyList<string?[]> rows, ImportOptions? options = null, int rowsPerChunk = int.MaxValue)
    {
        var job = TestData.Job(keyField: keyField, targets: targets) with { Options = options ?? new ImportOptions() };
        Store.CreateJob(job);
        Store.UpdateStatus(job.JobId, status => ImportJobStateMachine.Transition(status, ImportJobState.Uploading, UtcNow()));

        int keyIndex = Array.IndexOf(targets, keyField);
        var lines = new List<string>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var values = new Dictionary<string, string>();
            for (int t = 0; t < targets.Length; t++)
            {
                if (rows[i][t] is { } value)
                {
                    values[targets[t]] = value;
                }
            }

            string key = KeyNormalizer.NormalizeKey(keyField, rows[i][keyIndex]) ?? throw new InvalidOperationException($"Row {i + 1} has no valid key.");
            lines.Add(JsonSerializer.Serialize(new { r = i + 1, k = key, v = values }));
        }

        int chunkCount = 0;
        foreach (string[] chunk in lines.Chunk(Math.Max(1, Math.Min(rowsPerChunk, lines.Count))))
        {
            var result = ingest.Ingest(TestData.Payload(job.JobId, chunkCount++, chunk));
            Assert.That(result.IsAccepted, Is.True, result.Message);
        }

        var completed = ingest.CompleteUpload(job.JobId, new ImportProfile { RowsRead = rows.Count, RowsValid = rows.Count, ChunkCount = chunkCount });
        Assert.That(completed.Outcome, Is.EqualTo(UploadCompletionOutcome.Completed), completed.Message);

        return job.JobId;
    }

    /// <summary>
    /// Calls <see cref="ImportProcessor.ProcessNext"/> like the browser does until the phase stops.
    /// </summary>
    public ImportProgress Run(Guid jobId, ImportPhase phase)
    {
        string? token = null;
        ImportProgress progress;
        do
        {
            progress = Processor.ProcessNext(jobId, phase, token, takeOver: false);
            token = progress.DriverToken;
        }
        while (progress.Outcome == ImportProcessOutcome.Processed);

        return progress;
    }

    /// <summary>
    /// Analyze, confirm and execute; returns the progress of the import.
    /// </summary>
    public ImportProgress Import(Guid jobId)
    {
        var analysis = Run(jobId, ImportPhase.Analyze);
        Assert.That(analysis.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), analysis.Error);
        Assert.That(Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));
        var import = Run(jobId, ImportPhase.Execute);
        Assert.That(import.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), import.Error);

        return import;
    }

    /// <summary>
    /// The joined row report of a phase as CSV lines (header first); empty when there is no report.
    /// </summary>
    public string[] ReadReport(Guid jobId, ImportPhase phase)
    {
        using var buffer = new MemoryStream();
        if (Reports.CopyTo(jobId, phase, buffer) < 0)
        {
            return [];
        }

        buffer.Position = 0;
        using var reader = new StreamReader(new GZipStream(buffer, CompressionMode.Decompress));

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public void Dispose() => Temp.Dispose();

    private DateTime UtcNow() => Time.GetUtcNow().UtcDateTime;
}

internal sealed class ListLogger<T>(List<string> messages) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (messages)
        {
            messages.Add($"{logLevel} {eventId.Name}: {formatter(state, exception)} {exception}");
        }
    }
}
