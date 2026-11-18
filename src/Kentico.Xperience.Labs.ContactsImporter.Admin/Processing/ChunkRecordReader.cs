using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// One row of an uploaded chunk.
/// </summary>
/// <param name="Row">1-based data row number in the source file.</param>
/// <param name="Key">Normalized dedupe key.</param>
/// <param name="Values">Raw trimmed values by mapped field index; <c>null</c> = the row has no value for the field.</param>
/// <param name="Consent">Consent columns of the row (<c>c</c>); <c>null</c> when the job maps no consent columns.</param>
internal sealed record ImportRecord(long Row, string Key, string?[] Values, ImportRecordConsent? Consent = null);

/// <summary>
/// What the consent columns of a row say.
/// </summary>
/// <param name="Agreed">The "consent given" column has a value that means yes.</param>
/// <param name="Date">Consent date of the row (wall-clock time, no time zone); <c>null</c> = none.</param>
internal sealed record ImportRecordConsent(bool Agreed, DateTime? Date);

/// <summary>
/// The uploaded data of a job is missing or unreadable. The message is safe to store and show (no contact data).
/// </summary>
#pragma warning disable S3871 // Internal by design: thrown and caught inside the importer only, not part of the package API.
internal sealed class ImportDataException(ImportMessage userMessage, Exception? innerException = null) : Exception(userMessage.ToString(), innerException)
{
    /// <summary>What the user is told; the job stores it as the reason it failed.</summary>
    public ImportMessage UserMessage { get; } = userMessage;
}
#pragma warning restore S3871

/// <summary>
/// Reads the records of one stored chunk (gzipped NDJSON, validated at upload) in file order.
/// </summary>
/// <remarks>
/// Records are split on line feeds only, as <see cref="ChunkValidator"/> splits them: a carriage return (also one before
/// the line feed) is JSON whitespace inside the record, never a record break.
/// </remarks>
internal sealed class ChunkRecordReader : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly StreamReader reader;
    private readonly char[] buffer = new char[BufferSize];
    private readonly StringBuilder line = new();
    private int position;
    private int length;
    private readonly IReadOnlyDictionary<string, int> fieldIndexes;
    private readonly int fieldCount;
    private readonly int chunkIndex;

    /// <param name="gzippedChunk">Chunk file content; disposed with the reader.</param>
    /// <param name="context">Mapped fields of the job.</param>
    /// <param name="chunkIndex">Index of the chunk, for error messages.</param>
    public ChunkRecordReader(Stream gzippedChunk, ImportProcessingContext context, int chunkIndex)
    {
        reader = new StreamReader(new GZipStream(gzippedChunk, CompressionMode.Decompress), Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: BufferSize);
        fieldIndexes = context.FieldIndexes;
        fieldCount = context.Fields.Count;
        this.chunkIndex = chunkIndex;
    }

    /// <summary>
    /// Skips records that are already processed. Returns the number of records skipped.
    /// </summary>
    public long Skip(long count)
    {
        long skipped = 0;
        try
        {
            while (skipped < count && ReadRecordLine() is { } record)
            {
                if (record.Length > 0)
                {
                    skipped++;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw Unreadable(ex);
        }

        return skipped;
    }

    /// <summary>
    /// Reads up to <paramref name="maxCount"/> records; fewer (or none) at the end of the chunk.
    /// </summary>
    public List<ImportRecord> ReadBatch(int maxCount)
    {
        var records = new List<ImportRecord>(maxCount);
        try
        {
            while (records.Count < maxCount && ReadRecordLine() is { } record)
            {
                if (record.Length > 0)
                {
                    records.Add(Parse(record));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            throw Unreadable(ex);
        }

        return records;
    }

    public void Dispose() => reader.Dispose();

    /// <summary>
    /// Next record line without its line feed, or <c>null</c> at the end of the chunk. Unlike
    /// <see cref="TextReader.ReadLine"/>, a carriage return stays in the line.
    /// </summary>
    private string? ReadRecordLine()
    {
        line.Clear();
        bool hasData = false;
        while (true)
        {
            if (position == length)
            {
                length = reader.Read(buffer, 0, buffer.Length);
                position = 0;
                if (length == 0)
                {
                    return hasData ? line.ToString() : null;
                }
            }

            hasData = true;
            int newLine = Array.IndexOf(buffer, '\n', position, length - position);
            if (newLine < 0)
            {
                line.Append(buffer, position, length - position);
                position = length;
                continue;
            }

            line.Append(buffer, position, newLine - position);
            position = newLine + 1;

            return line.ToString();
        }
    }

    private ImportDataException Unreadable(Exception ex) =>
        new(ImportMessages.ChunkUnreadable(chunkIndex), ex);

    private ImportRecord Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        long row = root.GetProperty("r"u8).GetInt64();
        string key = root.GetProperty("k"u8).GetString() ?? throw new FormatException("The record has no key.");

        string?[] values = new string?[fieldCount];
        foreach (var property in root.GetProperty("v"u8).EnumerateObject())
        {
            // Values of fields that are not mapped cannot be stored (upload validation); ignore them if they are there anyway.
            if (fieldIndexes.TryGetValue(property.Name, out int index))
            {
                values[index] = property.Value.GetString();
            }
        }

        ImportRecordConsent? consent = null;
        if (root.TryGetProperty("c"u8, out var c))
        {
            DateTime? date = null;
            if (c.TryGetProperty("d"u8, out var d) && d.ValueKind != JsonValueKind.Null)
            {
                date = FieldValueConverter.TryParseDateTime(d.GetString()!, out var parsed) ? parsed : throw new FormatException("The consent date is not valid.");
            }

            consent = new ImportRecordConsent(c.TryGetProperty("a"u8, out var agreed) && agreed.ValueKind == JsonValueKind.True, date);
        }

        return new ImportRecord(row, key, values, consent);
    }
}
