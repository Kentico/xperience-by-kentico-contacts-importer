using System.Globalization;
using System.IO.Compression;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator;

/// <summary>
/// Minimal XLSX (one sheet "Contacts", header row, 1900 date system) written with <see cref="ZipArchive"/>
/// and hand-written SpreadsheetML.
/// <list type="bullet">
/// <item>Strings: shared strings table (<c>t="s"</c>, like Excel) or inline strings (<c>t="inlineStr"</c>).</item>
/// <item>Dates: serial numbers with style 1 (built-in <c>numFmtId</c> 14); date-times with style 2 (custom format 164 <c>yyyy-mm-dd hh:mm:ss</c>).</item>
/// <item>Booleans: <c>t="b"</c>. Empty cells are left out (sparse rows).</item>
/// </list>
/// Shared strings: the sheet is streamed first; strings go to a temp file and are copied into
/// <c>xl/sharedStrings.xml</c> at the end. Repeated strings are deduplicated while the lookup has
/// fewer than <see cref="MaxDedupeEntries"/> entries (keeps memory flat; later new strings get new indexes).
/// </summary>
public sealed class XlsxWriter : IRowWriter
{
    public const string SheetName = "Contacts";
    public const int MaxDedupeEntries = 200_000;

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly DateTime epoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly ZipArchive zip;
    private readonly bool sharedStrings;
    private readonly string sharedStringsTempPath;
    private StreamWriter? sheet;
    private StreamWriter? strings;
    private readonly Dictionary<string, int> stringIndex = new(StringComparer.Ordinal);
    private int uniqueStrings;
    private long stringRefs;
    private string[] columnLetters = [];
    private long rowNumber;

    public XlsxWriter(Stream output, bool sharedStrings, string tempFolder)
    {
        zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        this.sharedStrings = sharedStrings;
        sharedStringsTempPath = Path.Combine(tempFolder, $"sharedStrings-{Guid.NewGuid():N}.tmp");

        WritePart("[Content_Types].xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>{(sharedStrings ? """<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>""" : "")}</Types>
            """);
        WritePart("_rels/.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="{PackageRelNs}"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        WritePart("xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="{MainNs}" xmlns:r="{RelNs}"><sheets><sheet name="{SheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        WritePart("xl/_rels/workbook.xml.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="{PackageRelNs}"><Relationship Id="rId1" Type="{RelNs}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{RelNs}/styles" Target="styles.xml"/>{(sharedStrings ? $"""<Relationship Id="rId3" Type="{RelNs}/sharedStrings" Target="sharedStrings.xml"/>""" : "")}</Relationships>
            """);
        WritePart("xl/styles.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="{MainNs}"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm:ss"/></numFmts><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
            """);
    }

    public void WriteHeader(IReadOnlyList<string> columns)
    {
        columnLetters = Enumerable.Range(0, columns.Count).Select(ColumnLetters).ToArray();
        if (sharedStrings)
        {
            strings = new StreamWriter(sharedStringsTempPath, append: false, RowWriters.Utf8NoBom, RowWriters.BufferSize);
        }

        var entry = RowWriters.CreateEntry(zip, "xl/worksheets/sheet1.xml");
        sheet = new StreamWriter(entry.Open(), RowWriters.Utf8NoBom, RowWriters.BufferSize);
        sheet.Write($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>{"\n"}<worksheet xmlns="{MainNs}"><sheetData>""");

        rowNumber = 1;
        sheet.Write("<row r=\"1\">");
        for (int i = 0; i < columns.Count; i++)
        {
            WriteString(i, columns[i]);
        }
        sheet.Write("</row>");
    }

    public void WriteRow(Cell[] row)
    {
        var writer = sheet ?? throw new InvalidOperationException("WriteHeader first.");
        rowNumber++;
        writer.Write("<row r=\"");
        writer.Write(rowNumber);
        writer.Write("\">");
        for (int i = 0; i < row.Length; i++)
        {
            var cell = row[i];
            switch (cell.Kind)
            {
                case CellKind.Text:
                    WriteString(i, cell.Text ?? "");
                    break;
                case CellKind.Date:
                    WriteNumber(i, (cell.Value.Date - epoch).TotalDays, style: 1);
                    break;
                case CellKind.DateTime:
                    WriteNumber(i, (cell.Value - epoch).TotalDays, style: 2);
                    break;
                case CellKind.Bool:
                    StartCell(i);
                    writer.Write(cell.Flag ? "\" t=\"b\"><v>1</v></c>" : "\" t=\"b\"><v>0</v></c>");
                    break;
                case CellKind.Empty:
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }
        writer.Write("</row>");
    }

    private void StartCell(int column)
    {
        var writer = sheet!;
        writer.Write("<c r=\"");
        writer.Write(columnLetters[column]);
        writer.Write(rowNumber);
    }

    private void WriteNumber(int column, double value, int style)
    {
        StartCell(column);
        var writer = sheet!;
        writer.Write("\" s=\"");
        writer.Write(style);
        writer.Write("\"><v>");
        writer.Write(value.ToString(CultureInfo.InvariantCulture));
        writer.Write("</v></c>");
    }

    private void WriteString(int column, string value)
    {
        StartCell(column);
        var writer = sheet!;
        if (!sharedStrings)
        {
            writer.Write("\" t=\"inlineStr\"><is>");
            WriteT(writer, value);
            writer.Write("</is></c>");

            return;
        }

        stringRefs++;
        if (!stringIndex.TryGetValue(value, out int index))
        {
            index = uniqueStrings++;
            if (stringIndex.Count < MaxDedupeEntries)
            {
                stringIndex.Add(value, index);
            }
            strings!.Write("<si>");
            WriteT(strings, value);
            strings.Write("</si>");
        }

        writer.Write("\" t=\"s\"><v>");
        writer.Write(index);
        writer.Write("</v></c>");
    }

    private static void WriteT(StreamWriter writer, string value)
    {
        bool preserve = value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        writer.Write(preserve ? "<t xml:space=\"preserve\">" : "<t>");
        WriteEscaped(writer, value);
        writer.Write("</t>");
    }

    private static void WriteEscaped(StreamWriter writer, string value)
    {
        if (value.AsSpan().IndexOfAny('&', '<', '>') < 0)
        {
            writer.Write(value);

            return;
        }

        foreach (char ch in value)
        {
            switch (ch)
            {
                case '&':
                    writer.Write("&amp;");
                    break;
                case '<':
                    writer.Write("&lt;");
                    break;
                case '>':
                    writer.Write("&gt;");
                    break;
                default:
                    writer.Write(ch);
                    break;
            }
        }
    }

    public void Complete()
    {
        if (sheet is null)
        {
            throw new InvalidOperationException("WriteHeader first.");
        }

        sheet.Write("</sheetData></worksheet>");
        sheet.Dispose();
        sheet = null;

        if (strings is not null)
        {
            strings.Dispose();
            strings = null;
            var entry = RowWriters.CreateEntry(zip, "xl/sharedStrings.xml");
            using var target = entry.Open();
            using (var head = new StreamWriter(target, RowWriters.Utf8NoBom, 4096, leaveOpen: true))
            {
                head.Write($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>{"\n"}<sst xmlns="{MainNs}" count="{stringRefs}" uniqueCount="{uniqueStrings}">""");
            }
            using (var source = new FileStream(sharedStringsTempPath, FileMode.Open, FileAccess.Read, FileShare.None, RowWriters.BufferSize, FileOptions.DeleteOnClose))
            {
                source.CopyTo(target, RowWriters.BufferSize);
            }
            target.Write("</sst>"u8);
        }
    }

    public void Dispose()
    {
        sheet?.Dispose();
        strings?.Dispose();
        zip.Dispose();
        if (File.Exists(sharedStringsTempPath))
        {
            File.Delete(sharedStringsTempPath);
        }
    }

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    public static string ColumnLetters(int index)
    {
        string letters = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + ((n - 1) % 26)) + letters;
        }

        return letters;
    }

    private void WritePart(string name, string content)
    {
        var entry = RowWriters.CreateEntry(zip, name);
        using var stream = entry.Open();
        stream.Write(RowWriters.Utf8NoBom.GetBytes(content));
    }
}
