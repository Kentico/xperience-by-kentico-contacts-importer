using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Format of the row-level report: CSV with the columns <c>row,outcome,reason,field</c> and, only when the job asks
/// for it, <c>key</c>. Row number, outcome and reason codes are no personal data; the key is.
/// </summary>
/// <remarks>
/// While a phase runs, the report is stored as part files <c>report-{phase}-{first row}.csv.gz</c> (gzipped CSV lines
/// without a header), one or more per processing call; <see cref="IImportReportReader"/> joins them.
/// </remarks>
internal static class ImportReportFormat
{
    public const string PartExtension = ".csv.gz";

    private const int RowDigits = 12;

    public static string Header(bool includeKey) => includeKey ? "row,outcome,reason,field,key" : "row,outcome,reason,field";

    public static string PhaseName(ImportPhase phase) => phase == ImportPhase.Analyze ? "analyze" : "execute";

    public static string PartPrefix(ImportPhase phase) => $"{ImportJobStore.ReportFilePrefix}{PhaseName(phase)}-";

    /// <summary>Name of the part whose first row is <paramref name="firstRow"/>.</summary>
    public static string PartFileName(ImportPhase phase, long firstRow) =>
        PartPrefix(phase) + firstRow.ToString("D" + RowDigits, CultureInfo.InvariantCulture) + PartExtension;

    public static bool TryParsePartFileName(string fileName, ImportPhase phase, out long firstRow)
    {
        firstRow = 0;
        string prefix = PartPrefix(phase);

        return fileName.StartsWith(prefix, StringComparison.Ordinal)
            && fileName.EndsWith(PartExtension, StringComparison.Ordinal)
            && long.TryParse(fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - PartExtension.Length), NumberStyles.None, CultureInfo.InvariantCulture, out firstRow);
    }

    public static string OutcomeName(ImportRowOutcome outcome) => outcome switch
    {
        ImportRowOutcome.Created => "created",
        ImportRowOutcome.Updated => "updated",
        ImportRowOutcome.Unchanged => "unchanged",
        ImportRowOutcome.Skipped => "skipped",
        _ => throw new InvalidOperationException($"Unknown row outcome {outcome}."),
    };

    public static void AppendLine(StringBuilder builder, ImportRowResult row, bool includeKey)
    {
        builder.Append(row.Row.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(OutcomeName(row.Outcome)).Append(',');
        AppendValue(builder, row.Reason);
        builder.Append(',');
        AppendValue(builder, row.Field);
        if (includeKey)
        {
            builder.Append(',');
            AppendValue(builder, row.Key);
        }

        builder.Append('\n');
    }

    /// <summary>
    /// Appends a CSV value. A value a spreadsheet could run as a formula (starts with <c>=</c>, <c>+</c>, <c>-</c>,
    /// <c>@</c> or tab) gets a leading apostrophe; values with separators or quotes are quoted. Line breaks become
    /// spaces, so every report row is one line.
    /// </summary>
    public static void AppendValue(StringBuilder builder, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ');
        }

        bool formula = value[0] is '=' or '+' or '-' or '@' or '\t';
        bool quote = formula || value.AsSpan().IndexOfAny(',', '"') >= 0 || value[0] == ' ' || value[^1] == ' ';
        if (!quote)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        if (formula)
        {
            builder.Append('\'');
        }

        builder.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}

/// <summary>
/// Writes the report rows of one processing call. After every batch the current part file is stored again with
/// the new rows, so the stored report never lags behind the checkpoint by more than the batch being processed.
/// </summary>
internal sealed class ImportReportPartWriter(IImportJobStore store, Guid jobId, ImportPhase phase, bool includeKey)
{
    /// <summary>Compressed size after which the next batch starts a new part, which bounds the bytes rewritten per batch.</summary>
    internal const int MaxPartBytes = 64 * 1024;

    private readonly StringBuilder lines = new();
    private MemoryStream part = new();
    private string? partFileName;

    public void Append(IReadOnlyList<ImportRowResult> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        if (partFileName is null || part.Length >= MaxPartBytes)
        {
            part = new MemoryStream();
            partFileName = ImportReportFormat.PartFileName(phase, rows[0].Row);
        }

        lines.Clear();
        foreach (var row in rows)
        {
            ImportReportFormat.AppendLine(lines, row, includeKey);
        }

        // Each batch is its own gzip member; concatenated members are one valid gzip file.
        using (var gzip = new GZipStream(part, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(lines.ToString()));
        }

        store.WriteReport(jobId, partFileName, part.GetBuffer().AsSpan(0, (int)part.Length));
    }
}

/// <summary>
/// Read access to the row-level report of a job phase.
/// </summary>
internal interface IImportReportReader
{
    /// <summary>Whether the job has report rows for the phase.</summary>
    public bool HasReport(Guid jobId, ImportPhase phase);

    /// <summary>
    /// Writes the whole report of the phase to <paramref name="destination"/> as one gzipped CSV file with a header
    /// line. Returns the number of data rows, or -1 when the job has no report for the phase.
    /// </summary>
    public long CopyTo(Guid jobId, ImportPhase phase, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>File name to offer for the downloaded report.</summary>
    public string GetDownloadFileName(ImportPhase phase);
}

internal sealed class ImportReportReader(IImportJobStore store) : IImportReportReader
{
    public bool HasReport(Guid jobId, ImportPhase phase) => ListParts(store, jobId, phase).Count > 0;

    public string GetDownloadFileName(ImportPhase phase) => $"contact-import-{ImportReportFormat.PhaseName(phase)}-report.csv.gz";

    public long CopyTo(Guid jobId, ImportPhase phase, Stream destination, CancellationToken cancellationToken = default)
    {
        var parts = ListParts(store, jobId, phase);
        if (parts.Count == 0)
        {
            return -1;
        }

        bool includeKey = store.ReadJob(jobId)?.Options.ReportIncludesKey ?? false;
        long rows = 0;
        using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.NewLine = "\n";
        writer.WriteLine(ImportReportFormat.Header(includeKey));

        for (int i = 0; i < parts.Count; i++)
        {
            // A processing call that stopped between its database commit and its checkpoint leaves rows the next call
            // processes again; the part of that next call starts at those rows and replaces them.
            long endRow = i + 1 < parts.Count ? parts[i + 1].FirstRow : long.MaxValue;
            using var stream = store.OpenReport(jobId, parts[i].FileName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int comma = line.IndexOf(',', StringComparison.Ordinal);
                if (comma <= 0 || !long.TryParse(line.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out long row))
                {
                    continue;
                }

                if (row >= endRow)
                {
                    break;
                }

                writer.WriteLine(line);
                rows++;
            }
        }

        return rows;
    }

    /// <summary>Report parts of a phase ordered by first row.</summary>
    internal static List<(string FileName, long FirstRow)> ListParts(IImportJobStore store, Guid jobId, ImportPhase phase)
    {
        var parts = new List<(string FileName, long FirstRow)>();
        foreach (string fileName in store.ListReports(jobId))
        {
            if (ImportReportFormat.TryParsePartFileName(fileName, phase, out long firstRow))
            {
                parts.Add((fileName, firstRow));
            }
        }

        parts.Sort((x, y) => x.FirstRow.CompareTo(y.FirstRow));

        return parts;
    }
}
