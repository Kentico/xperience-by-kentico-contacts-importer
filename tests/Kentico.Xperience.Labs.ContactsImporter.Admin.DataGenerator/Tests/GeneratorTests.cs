using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator.Tests;

public partial class GeneratorTests
{
    private string folder = "";

    [SetUp]
    public void SetUp()
    {
        folder = Path.Combine(Path.GetTempPath(), "contacts-datagen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(folder, recursive: true);

    private GenerationResult Generate(GeneratorOptions options, string name) =>
        Generator.Run(options with { Out = Path.Combine(folder, name) });

    private static GeneratorOptions Full(OutputFormat format) => new()
    {
        Rows = 2_000,
        Format = format,
        Seed = 42,
        DuplicatesPercent = 10,
        InvalidEmailsPercent = 5,
        EmptyValuesPercent = 10,
        CustomColumns = 2,
        ConsentColumns = true,
    };

    [GeneratedRegex(@"^[a-z]+\.[a-z]+\.\d+@[a-z.]+$")]
    private static partial Regex ValidEmail();

    [TestCase(OutputFormat.Csv)]
    [TestCase(OutputFormat.Tsv)]
    [TestCase(OutputFormat.Ndjson)]
    [TestCase(OutputFormat.Json)]
    [TestCase(OutputFormat.Xlsx)]
    public void SameSeed_SameBytes_OtherSeed_OtherBytes(OutputFormat format)
    {
        var options = Full(format);
        var a = Generate(options, "a");
        var b = Generate(options, "b");
        var c = Generate(options with { Seed = 7 }, "c");

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(b.Path), Is.EqualTo(File.ReadAllBytes(a.Path)));
            Assert.That(File.ReadAllBytes(c.Path), Is.Not.EqualTo(File.ReadAllBytes(a.Path)));
        });
    }

    [TestCase(OutputFormat.Csv, Compression.None)]
    [TestCase(OutputFormat.Tsv, Compression.Gz)]
    [TestCase(OutputFormat.Ndjson, Compression.Zip)]
    [TestCase(OutputFormat.Json, Compression.Gz)]
    [TestCase(OutputFormat.Json, Compression.None)]
    [TestCase(OutputFormat.Xlsx, Compression.None)]
    [TestCase(OutputFormat.Xlsx, Compression.Zip)]
    public void EveryFormat_HoldsTheSameRowsAsCsv(OutputFormat format, Compression compress)
    {
        var csvOptions = Full(OutputFormat.Csv);
        var csv = TestFiles.ReadRows(Generate(csvOptions, "expected.csv").Path, csvOptions);
        var options = Full(format) with { Compress = compress };
        var result = Generate(options, "actual");

        var rows = TestFiles.ReadRows(result.Path, options);

        Assert.Multiple(() =>
        {
            Assert.That(csv, Has.Count.EqualTo(2_001));
            Assert.That(rows, Has.Count.EqualTo(csv.Count));
            Assert.That(rows, Is.EqualTo(csv));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Xlsx_IsAValidPackage_WithSharedOrInlineStrings(bool shared)
    {
        var options = Full(OutputFormat.Xlsx) with { XlsxSharedStrings = shared };
        var result = Generate(options, "contacts.xlsx");

        using var file = File.OpenRead(result.Path);
        var content = TestFiles.ReadXlsx(file);

        Assert.Multiple(() =>
        {
            Assert.That(content.Rows, Has.Count.EqualTo(2_001));
            Assert.That(content.Rows[0], Is.EqualTo(ContactRowSource.BuildColumns(options).ToArray()));
            Assert.That(content.Workbook.ToString(), Does.Contain("name=\"Contacts\""));
            Assert.That(content.ContentTypes.ToString().Contains("sharedStrings"), Is.EqualTo(shared));
            if (shared)
            {
                Assert.That(content.UniqueStrings, Is.GreaterThan(0).And.LessThan(content.StringRefs), "repeated strings deduplicated");
            }
        });
    }

    [Test]
    public void Xlsx_DatesAreSerialNumbersWithDateStyles()
    {
        var options = Full(OutputFormat.Xlsx) with { Rows = 50, EmptyValuesPercent = 0 };
        var result = Generate(options, "contacts.xlsx");

        using var zip = ZipFile.OpenRead(result.Path);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        string sheet = reader.ReadToEnd();

        Assert.Multiple(() =>
        {
            Assert.That(sheet, Does.Match("""<c r="H2" s="1"><v>\d+</v></c>"""));
            Assert.That(sheet, Does.Contain("t=\"b\""));
            Assert.That(sheet, Does.Match("""s="2"><v>\d+\.\d+</v>"""));
        });
    }

    [Test]
    public void Percentages_AreWithinTolerance()
    {
        var options = new GeneratorOptions { Rows = 20_000, Seed = 3, DuplicatesPercent = 10, InvalidEmailsPercent = 5, EmptyValuesPercent = 20 };
        var result = Generate(options, "contacts.csv");
        var rows = TestFiles.ReadRows(result.Path, options).Skip(1).ToList();

        var emails = rows.Select(r => r[ContactRowSource.EmailColumn]).ToList();
        int invalid = emails.Count(e => !ValidEmail().IsMatch(e));
        var valid = emails.Where(e => ValidEmail().IsMatch(e)).ToList();
        int duplicates = valid.Count - valid.Distinct(StringComparer.Ordinal).Count();
        int emptyCells = rows.Sum(r => r.Skip(1).Count(v => v.Length == 0));
        double emptyPercent = 100d * emptyCells / (rows.Count * (rows[0].Length - 1));

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(20_000));
            Assert.That(100d * invalid / rows.Count, Is.InRange(4, 6));
            Assert.That(100d * duplicates / rows.Count, Is.InRange(9, 11));
            Assert.That(emptyPercent, Is.InRange(19, 21));
            Assert.That(invalid, Is.EqualTo(result.Stats.InvalidEmails));
            Assert.That(duplicates, Is.EqualTo(result.Stats.Duplicates));
            Assert.That(emptyCells, Is.EqualTo(result.Stats.EmptyCells));
        });
    }

    [Test]
    public void KeyOffset_RunsOverlapByTheSharedRange()
    {
        var first = new GeneratorOptions { Rows = 1_000 };
        var second = first with { KeyOffset = 600, Seed = 2 };
        var a = TestFiles.ReadRows(Generate(first, "a.csv").Path, first).Skip(1).ToList();
        var b = TestFiles.ReadRows(Generate(second, "b.csv").Path, second).Skip(1).ToList();

        var shared = a.Select(r => r[0]).Intersect(b.Select(r => r[0])).ToList();
        var namesA = a.ToDictionary(r => r[0], r => (r[1], r[2]));

        Assert.Multiple(() =>
        {
            Assert.That(shared, Has.Count.EqualTo(400));
            Assert.That(b.Where(r => namesA.ContainsKey(r[0])).All(r => namesA[r[0]] == (r[1], r[2])), Is.True, "same names for the same email");
        });
    }

    [Test]
    public void Zip_HasOneEntryNamedLikeTheOutput()
    {
        var options = new GeneratorOptions { Rows = 10, Format = OutputFormat.Json, Compress = Compression.Zip };
        var result = Generate(options, "big.json.zip");

        using var zip = ZipFile.OpenRead(result.Path);

        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "big.json" }));
    }

    [Test]
    public void DateFormats_FormatTextDates()
    {
        var date = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);

        Assert.Multiple(() =>
        {
            Assert.That(TextFormatting.Date(date, DateFormat.Iso), Is.EqualTo("2001-02-03"));
            Assert.That(TextFormatting.Date(date, DateFormat.Dmy), Is.EqualTo("03.02.2001"));
            Assert.That(TextFormatting.Date(date, DateFormat.Mdy), Is.EqualTo("02/03/2001"));
            Assert.That(TextFormatting.DateTime(date, DateFormat.Iso), Is.EqualTo("2001-02-03T04:05:06"));
            Assert.That(TextFormatting.DateTime(date, DateFormat.Dmy), Is.EqualTo("03.02.2001 04:05:06"));
        });
    }

    [TestCase(0, "A")]
    [TestCase(25, "Z")]
    [TestCase(26, "AA")]
    [TestCase(701, "ZZ")]
    [TestCase(702, "AAA")]
    public void ColumnLetters(int index, string letters) =>
        Assert.That(XlsxWriter.ColumnLetters(index), Is.EqualTo(letters));

    [Test]
    public void Options_ParseAndValidate()
    {
        var options = GeneratorOptions.Parse(["--rows", "1_000_000", "--format", "XLSX", "--compress", "zip", "--seed", "5", "--duplicates", "2.5",
            "--consent-columns", "--custom-columns", "3", "--date-format", "dmy", "--key-offset", "10", "--xlsx-shared-strings", "false"]);

        Assert.Multiple(() =>
        {
            Assert.That(options.Rows, Is.EqualTo(1_000_000));
            Assert.That(options.Format, Is.EqualTo(OutputFormat.Xlsx));
            Assert.That(options.Compress, Is.EqualTo(Compression.Zip));
            Assert.That(options.DuplicatesPercent, Is.EqualTo(2.5));
            Assert.That(options.ConsentColumns, Is.True);
            Assert.That(options.CustomColumns, Is.EqualTo(3));
            Assert.That(options.DateFormat, Is.EqualTo(DateFormat.Dmy));
            Assert.That(options.KeyOffset, Is.EqualTo(10));
            Assert.That(options.XlsxSharedStrings, Is.False);
            Assert.That(GeneratorOptions.Parse(["--format", "xlsx", "--rows", "1048575"]).Rows, Is.EqualTo(1_048_575));
            Assert.That(() => GeneratorOptions.Parse(["--format", "xlsx", "--rows", "1048576"]), Throws.ArgumentException);
            Assert.That(() => GeneratorOptions.Parse(["--format", "xlsx", "--compress", "gz"]), Throws.ArgumentException);
            Assert.That(() => GeneratorOptions.Parse(["--format", "xml"]), Throws.ArgumentException);
            Assert.That(() => GeneratorOptions.Parse(["--duplicates", "101"]), Throws.ArgumentException);
            Assert.That(() => GeneratorOptions.Parse(["--rows"]), Throws.ArgumentException);
            Assert.That(() => GeneratorOptions.Parse(["--bogus"]), Throws.ArgumentException);
        });
    }

    [Test]
    public void Cli_ReturnsExitCodes()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string path = Path.Combine(folder, "cli.csv");

        Assert.Multiple(() =>
        {
            Assert.That(Program.Run(["--help"], output, error), Is.EqualTo(0));
            Assert.That(Program.Run(["--rows", "x"], output, error), Is.EqualTo(2));
            Assert.That(Program.Run(["--rows", "5", "--out", path, "--quiet"], output, error), Is.EqualTo(0));
            Assert.That(File.ReadAllLines(path), Has.Length.EqualTo(6));
        });
    }
}
