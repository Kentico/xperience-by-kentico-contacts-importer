using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Reads the row report of a job phase in pages for the <c>GET_REPORT</c> page command, so a report of any size is
/// downloaded with bounded memory per request and bounded response size.
/// </summary>
/// <remarks>
/// The report is stored as part files (<see cref="ImportReportFormat"/>). A page starts at a part (its first row) and
/// a line in it, and holds lines until <see cref="ImportReportPager.MaxPageBytes"/> of CSV text is reached. Per call the server reads at
/// most the skipped lines of one part plus one page. Rows are joined the way <see cref="IImportReportReader.CopyTo"/>
/// joins them: a part ends where the next part starts (rows processed again after an interrupted call are replaced).
/// </remarks>
internal interface IImportReportPager
{
    /// <summary>Whether the job has report rows for the phase.</summary>
    public bool HasReport(Guid jobId, ImportPhase phase);

    /// <summary>
    /// Reads one page; <c>null</c> when the job has no report for the phase.
    /// </summary>
    /// <param name="jobId">Job.</param>
    /// <param name="phase">Report of the analysis or of the import.</param>
    /// <param name="part">First row of the part to continue in; 0 for the first page.</param>
    /// <param name="line">Lines of that part already returned.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    public ImportReportPage? ReadPage(Guid jobId, ImportPhase phase, long part, int line, CancellationToken cancellationToken = default);
}

/// <param name="GzippedLines">Gzipped CSV lines (UTF-8, LF line ends, no header).</param>
/// <param name="Rows">Report rows in the page.</param>
/// <param name="NextPart">Part the next page continues in.</param>
/// <param name="NextLine">Lines of <paramref name="NextPart"/> already returned.</param>
/// <param name="Done">No rows follow.</param>
internal sealed record ImportReportPage(byte[] GzippedLines, int Rows, long NextPart, int NextLine, bool Done);

internal sealed class ImportReportPager(IImportJobStore store) : IImportReportPager
{
    /// <summary>CSV text (characters) after which a page ends. One more line can follow.</summary>
    internal const int MaxPageBytes = 2 * 1024 * 1024;

    private readonly int maxPageBytes = MaxPageBytes;

    /// <summary>For tests: small pages.</summary>
    internal ImportReportPager(IImportJobStore store, int maxPageBytes) : this(store) => this.maxPageBytes = maxPageBytes;

    public bool HasReport(Guid jobId, ImportPhase phase) => ImportReportReader.ListParts(store, jobId, phase).Count > 0;

    public ImportReportPage? ReadPage(Guid jobId, ImportPhase phase, long part, int line, CancellationToken cancellationToken = default)
    {
        var parts = ImportReportReader.ListParts(store, jobId, phase);
        if (parts.Count == 0)
        {
            return null;
        }

        int index = parts.FindIndex(p => p.FirstRow >= part);
        if (index < 0)
        {
            return new ImportReportPage(Gzip(""), 0, part, line, true);
        }

        // The part of the cursor is gone (replaced by a later attempt): continue at the start of the next one.
        int skip = parts[index].FirstRow == part ? Math.Max(0, line) : 0;
        var text = new StringBuilder();
        int rows = 0;

        for (; index < parts.Count; index++, skip = 0)
        {
            long endRow = index + 1 < parts.Count ? parts[index + 1].FirstRow : long.MaxValue;
            using var stream = store.OpenReport(jobId, parts[index].FileName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
            int lineIndex = 0;
            while (reader.ReadLine() is { } current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lineIndex++ < skip)
                {
                    continue;
                }

                int comma = current.IndexOf(',', StringComparison.Ordinal);
                if (comma <= 0 || !long.TryParse(current.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out long row))
                {
                    continue;
                }

                if (row >= endRow)
                {
                    break;
                }

                text.Append(current).Append('\n');
                rows++;
                if (text.Length >= maxPageBytes)
                {
                    return new ImportReportPage(Gzip(text.ToString()), rows, parts[index].FirstRow, lineIndex, false);
                }
            }
        }

        return new ImportReportPage(Gzip(text.ToString()), rows, parts[^1].FirstRow, int.MaxValue, true);
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// Lets one <c>PROCESS_NEXT</c> request per job work at a time in this application instance. A second request for
/// the same job (a retry sent while the first one is still finishing its batch) is told to come back later instead
/// of processing the same rows twice. Other instances of a web farm are covered by the job status checks.
/// </summary>
internal interface IImportProcessGate
{
    /// <summary>Claims the job; <c>false</c> when a request is already processing it.</summary>
    public bool TryEnter(Guid jobId);

    public void Exit(Guid jobId);

    /// <summary>A request is processing the job right now.</summary>
    public bool IsBusy(Guid jobId);
}

internal sealed class ImportProcessGate : IImportProcessGate
{
    private readonly HashSet<Guid> busy = [];

    public bool TryEnter(Guid jobId)
    {
        lock (busy)
        {
            return busy.Add(jobId);
        }
    }

    public void Exit(Guid jobId)
    {
        lock (busy)
        {
            busy.Remove(jobId);
        }
    }

    public bool IsBusy(Guid jobId)
    {
        lock (busy)
        {
            return busy.Contains(jobId);
        }
    }
}
