using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator.Tests;

/// <summary>Readers for generated files (tests only): every format → rows of text as the CSV writer formats them.</summary>
internal static class TestFiles
{
    private static readonly XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly DateTime epoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>RFC 4180 parser (CRLF records; quoted fields). Includes the header row.</summary>
    public static List<string[]> ReadDelimited(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        char previous = '\0';
        foreach (char ch in text)
        {
            if (quoted)
            {
                if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                // "" inside a quoted field: the first quote closed it, this one reopens it
                if (previous == '"')
                {
                    field.Append('"');
                }
                quoted = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\n' && previous == '\r')
            {
                fields.Add(field.ToString());
                field.Clear();
                rows.Add([.. fields]);
                fields.Clear();
            }
            else if (ch != '\r')
            {
                field.Append(ch);
            }
            previous = ch;
        }

        Assert.That(fields, Is.Empty, "file must end with CRLF");

        return rows;
    }

    /// <summary>JSON array or NDJSON → header (first object's keys) + rows; null → "", booleans → yes/no.</summary>
    public static List<string[]> ReadJson(string text, bool array)
    {
        var objects = new List<JsonElement>();
        if (array)
        {
            using var document = JsonDocument.Parse(text);
            Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            objects.AddRange(document.RootElement.EnumerateArray().Select(e => e.Clone()));
        }
        else
        {
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                objects.Add(document.RootElement.Clone());
            }
        }

        var rows = new List<string[]> { objects[0].EnumerateObject().Select(p => p.Name).ToArray() };
        rows.AddRange(objects.Select(o => o.EnumerateObject().Select(p => JsonText(p.Value)).ToArray()));

        return rows;
    }

    private static string JsonText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return "";
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean() ? "yes" : "no";
        }

        return value.GetString()!;
    }

    /// <summary>Reads the XLSX package and its single sheet. Dates (styles 1 + 2) → ISO text, booleans → yes/no.</summary>
    public static XlsxContent ReadXlsx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        XDocument Part(string name)
        {
            var entry = zip.GetEntry(name);
            Assert.That(entry, Is.Not.Null, name);
            using var partStream = entry!.Open();

            return XDocument.Load(partStream);
        }

        var contentTypes = Part("[Content_Types].xml");
        var workbook = Part("xl/workbook.xml");
        var styles = Part("xl/styles.xml");
        var sheet = Part("xl/worksheets/sheet1.xml");
        var sst = zip.GetEntry("xl/sharedStrings.xml") is null ? null : Part("xl/sharedStrings.xml");
        Part("_rels/.rels");
        Part("xl/_rels/workbook.xml.rels");

        string[] strings = sst?.Root!.Elements(main + "si").Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value))).ToArray() ?? [];
        string[] numFmts = styles.Root!.Element(main + "cellXfs")!.Elements(main + "xf").Select(x => (string)x.Attribute("numFmtId")!).ToArray();

        var rows = new List<string[]>();
        int width = 0;
        int expectedRow = 1;
        foreach (var row in sheet.Root!.Element(main + "sheetData")!.Elements(main + "row"))
        {
            Assert.That((int)row.Attribute("r")!, Is.EqualTo(expectedRow++));
            var cells = row.Elements(main + "c").ToList();
            width = Math.Max(width, cells.Count);
            string[] values = new string[width];
            Array.Fill(values, "");
            foreach (var c in cells)
            {
                string reference = (string)c.Attribute("r")!;
                int column = ColumnIndex(reference);
                string? type = (string?)c.Attribute("t");
                string v = (string?)c.Element(main + "v") ?? "";
                values[column] = type switch
                {
                    "s" => strings[int.Parse(v, CultureInfo.InvariantCulture)],
                    "inlineStr" => string.Concat(c.Element(main + "is")!.Descendants(main + "t").Select(t => t.Value)),
                    "b" => v == "1" ? "yes" : "no",
                    _ => FormatNumber(v, numFmts[(int?)c.Attribute("s") ?? 0]),
                };
            }
            rows.Add(values);
        }

        return new XlsxContent(rows, strings.Length, sst is null ? 0 : (long)sst.Root!.Attribute("count")!, contentTypes, workbook);
    }

    private static string FormatNumber(string value, string numFmtId)
    {
        double number = double.Parse(value, CultureInfo.InvariantCulture);

        return numFmtId switch
        {
            "14" => epoch.AddDays(number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "164" => epoch.AddSeconds(Math.Round(number * 86_400)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    private static int ColumnIndex(string reference)
    {
        int index = 0;
        foreach (char ch in reference.TakeWhile(char.IsAsciiLetterUpper))
        {
            index = (index * 26) + ch - 'A' + 1;
        }

        return index - 1;
    }

    /// <summary>Rows of any generated file (decompresses gz / single-entry zip first).</summary>
    public static List<string[]> ReadRows(string path, GeneratorOptions options)
    {
        using var file = File.OpenRead(path);
        using var payload = Decompress(file, options.Compress);
        if (options.Format == OutputFormat.Xlsx)
        {
            var buffer = new MemoryStream();
            payload.CopyTo(buffer);
            buffer.Position = 0;

            return ReadXlsx(buffer).Rows;
        }

        string text = new StreamReader(payload, Encoding.UTF8).ReadToEnd();

        return options.Format switch
        {
            OutputFormat.Csv => ReadDelimited(text, ','),
            OutputFormat.Tsv => ReadDelimited(text, '\t'),
            OutputFormat.Ndjson => ReadJson(text, array: false),
            OutputFormat.Json => ReadJson(text, array: true),
            OutputFormat.Xlsx => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(),
        };
    }

    public static Stream Decompress(Stream file, Compression compression)
    {
        switch (compression)
        {
            case Compression.Gz:
                return new GZipStream(file, CompressionMode.Decompress);
            case Compression.Zip:
                var zip = new ZipArchive(file, ZipArchiveMode.Read);
                Assert.That(zip.Entries, Has.Count.EqualTo(1));
                var buffer = new MemoryStream();
                using (var entry = zip.Entries[0].Open())
                {
                    entry.CopyTo(buffer);
                }
                buffer.Position = 0;

                return buffer;
            case Compression.None:
                var copy = new MemoryStream();
                file.CopyTo(copy);
                copy.Position = 0;

                return copy;
            default:
                throw new InvalidOperationException();
        }
    }
}

internal sealed record XlsxContent(List<string[]> Rows, int UniqueStrings, long StringRefs, XDocument ContentTypes, XDocument Workbook);
