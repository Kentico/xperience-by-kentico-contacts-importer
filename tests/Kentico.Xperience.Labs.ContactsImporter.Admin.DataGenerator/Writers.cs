using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator;

/// <summary>Streaming writer of one output format. Memory does not grow with the row count.</summary>
public interface IRowWriter : IDisposable
{
    public void WriteHeader(IReadOnlyList<string> columns);

    public void WriteRow(Cell[] row);

    /// <summary>Writes trailing content (closing bracket, XLSX parts). Must be called before dispose.</summary>
    public void Complete();
}

public static class RowWriters
{
    internal static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal const int BufferSize = 1 << 20;

    /// <summary>Fixed entry time so the same options + seed give the same zip bytes.</summary>
    private static readonly DateTimeOffset entryTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Zip entry with deflate (optimal) and a fixed timestamp.</summary>
    public static ZipArchiveEntry CreateEntry(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = entryTime;

        return entry;
    }

    /// <summary>Writer for the format. <paramref name="tempFolder"/> holds XLSX temp files (shared strings).</summary>
    public static IRowWriter Create(GeneratorOptions options, Stream output, string tempFolder) => options.Format switch
    {
        OutputFormat.Csv => new DelimitedWriter(output, ',', options.DateFormat),
        OutputFormat.Tsv => new DelimitedWriter(output, '\t', options.DateFormat),
        OutputFormat.Ndjson => new JsonRowWriter(output, array: false, options.DateFormat),
        OutputFormat.Json => new JsonRowWriter(output, array: true, options.DateFormat),
        OutputFormat.Xlsx => new XlsxWriter(output, options.XlsxSharedStrings, tempFolder),
        _ => throw new InvalidOperationException(),
    };
}

/// <summary>CSV / TSV: UTF-8 without BOM, CRLF, RFC 4180 quoting (fields with delimiter, quote or line break).</summary>
public sealed class DelimitedWriter : IRowWriter
{
    private readonly StreamWriter writer;
    private readonly char delimiter;
    private readonly DateFormat dateFormat;

    public DelimitedWriter(Stream output, char delimiter, DateFormat dateFormat)
    {
        writer = new StreamWriter(output, RowWriters.Utf8NoBom, RowWriters.BufferSize, leaveOpen: true) { NewLine = "\r\n" };
        this.delimiter = delimiter;
        this.dateFormat = dateFormat;
    }

    public void WriteHeader(IReadOnlyList<string> columns)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            WriteField(i, columns[i]);
        }
        writer.WriteLine();
    }

    public void WriteRow(Cell[] row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            WriteField(i, TextFormatting.Format(row[i], dateFormat));
        }
        writer.WriteLine();
    }

    private void WriteField(int index, string value)
    {
        if (index > 0)
        {
            writer.Write(delimiter);
        }
        if (value.AsSpan().IndexOfAny(['"', delimiter, '\r', '\n']) < 0)
        {
            writer.Write(value);

            return;
        }

        writer.Write('"');
        writer.Write(value.Replace("\"", "\"\""));
        writer.Write('"');
    }

    public void Complete() => writer.Flush();

    public void Dispose() => writer.Dispose();
}

/// <summary>
/// NDJSON (one object per line, LF) or JSON array (top-level <c>[</c>, one object per line, <c>]</c>).
/// Empty cells are <c>null</c>, booleans are JSON booleans, dates are strings in the date format.
/// Non-ASCII text is written as UTF-8 (not <c>\u</c> escapes).
/// </summary>
public sealed class JsonRowWriter : IRowWriter
{
    private static readonly byte[] arrayStart = "[\n"u8.ToArray();
    private static readonly byte[] arraySeparator = ",\n"u8.ToArray();
    private static readonly byte[] arrayEnd = "\n]\n"u8.ToArray();
    private static readonly byte[] emptyArray = "[]\n"u8.ToArray();

    private readonly BufferedStream output;
    private readonly Utf8JsonWriter json;
    private readonly bool array;
    private readonly DateFormat dateFormat;
    private JsonEncodedText[] names = [];
    private long rows;

    public JsonRowWriter(Stream output, bool array, DateFormat dateFormat)
    {
        this.output = new BufferedStream(output, RowWriters.BufferSize);
        json = new Utf8JsonWriter(this.output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true });
        this.array = array;
        this.dateFormat = dateFormat;
    }

    public void WriteHeader(IReadOnlyList<string> columns) =>
        names = columns.Select(c => JsonEncodedText.Encode(c, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)).ToArray();

    public void WriteRow(Cell[] row)
    {
        if (array)
        {
            output.Write(rows == 0 ? arrayStart : arraySeparator);
        }

        json.WriteStartObject();
        for (int i = 0; i < row.Length; i++)
        {
            var cell = row[i];
            switch (cell.Kind)
            {
                case CellKind.Empty:
                    json.WriteNull(names[i]);
                    break;
                case CellKind.Bool:
                    json.WriteBoolean(names[i], cell.Flag);
                    break;
                case CellKind.Text:
                case CellKind.Date:
                case CellKind.DateTime:
                    json.WriteString(names[i], TextFormatting.Format(cell, dateFormat));
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }
        json.WriteEndObject();
        json.Flush();
        json.Reset();
        if (!array)
        {
            output.WriteByte((byte)'\n');
        }
        rows++;
    }

    public void Complete()
    {
        if (array)
        {
            output.Write(rows == 0 ? emptyArray : arrayEnd);
        }
        output.Flush();
    }

    public void Dispose()
    {
        json.Dispose();
        output.Flush();
    }
}
