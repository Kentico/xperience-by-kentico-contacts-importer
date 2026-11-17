using System.IO.Compression;
using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

public class ImportReportTests
{
    private static string Value(string? value)
    {
        var builder = new StringBuilder();
        ImportReportFormat.AppendValue(builder, value);

        return builder.ToString();
    }

    private static ImportRowResult Row(long row, ImportRowOutcome outcome = ImportRowOutcome.Created, string? reason = null, string? field = null, string key = "key") =>
        new(row, outcome, reason, field, key);

    private static string[] Read(TempStore temp, Guid jobId, ImportPhase phase)
    {
        using var buffer = new MemoryStream();
        long rows = new ImportReportReader(temp.Store).CopyTo(jobId, phase, buffer);
        if (rows < 0)
        {
            return [];
        }

        buffer.Position = 0;
        using var reader = new StreamReader(new GZipStream(buffer, CompressionMode.Decompress));
        string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(rows + 1), "CopyTo returns the number of data rows.");

        return lines;
    }

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("plain", "plain")]
    [TestCase("jane@example.com", "jane@example.com")]
    [TestCase("a,b", "\"a,b\"")]
    [TestCase("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [TestCase("=1+1", "\"'=1+1\"")]
    [TestCase("+1", "\"'+1\"")]
    [TestCase("-1", "\"'-1\"")]
    [TestCase("@SUM(A1)", "\"'@SUM(A1)\"")]
    [TestCase("\tx", "\"'\tx\"")]
    [TestCase("=cmd|' /C calc'!A0,\"x\"", "\"'=cmd|' /C calc'!A0,\"\"x\"\"\"")]
    [TestCase("line\r\nbreak", "line  break")]
    [TestCase("\n=1", "\" =1\"")]
    [TestCase(" padded ", "\" padded \"")]
    public void Values_Are_Csv_Escaped_And_Cannot_Run_As_Formulas(string? value, string expected) => Assert.That(Value(value), Is.EqualTo(expected));

    [Test]
    public void Line_Has_The_Key_Only_When_Asked()
    {
        var withKey = new StringBuilder();
        var withoutKey = new StringBuilder();
        var row = Row(12, ImportRowOutcome.Skipped, ImportRowReasons.InvalidValue, "Age", "=jane@example.com");

        ImportReportFormat.AppendLine(withKey, row, includeKey: true);
        ImportReportFormat.AppendLine(withoutKey, row, includeKey: false);

        Assert.Multiple(() =>
        {
            Assert.That(withKey.ToString(), Is.EqualTo("12,skipped,invalidValue,Age,\"'=jane@example.com\"\n"));
            Assert.That(withoutKey.ToString(), Is.EqualTo("12,skipped,invalidValue,Age\n"));
            Assert.That(ImportReportFormat.Header(true), Is.EqualTo("row,outcome,reason,field,key"));
            Assert.That(ImportReportFormat.Header(false), Is.EqualTo("row,outcome,reason,field"));
        });
    }

    [Test]
    public void Line_Has_The_List_And_Consent_Columns_Only_For_Jobs_That_Use_Them()
    {
        var row = Row(12, ImportRowOutcome.Created, null, null, "jane@example.com") with { List = "added;bouncedHard", Consent = "recorded" };
        var list = new ImportOptions { RecipientListId = 4 };
        var consent = new ImportOptions { Consent = ConsentMode.Blanket, ConsentId = 2, ReportIncludesKey = true };
        var both = list with { Consent = ConsentMode.Blanket, ConsentId = 2 };

        static string Line(ImportRowResult row, ImportOptions options)
        {
            var builder = new StringBuilder();
            ImportReportFormat.AppendLine(builder, row, ImportReportColumns.From(options));

            return builder.ToString();
        }

        Assert.Multiple(() =>
        {
            Assert.That(ImportReportFormat.Header(new ImportOptions()), Is.EqualTo("row,outcome,reason,field"));
            Assert.That(ImportReportFormat.Header(list), Is.EqualTo("row,outcome,reason,field,list"));
            Assert.That(ImportReportFormat.Header(consent), Is.EqualTo("row,outcome,reason,field,consent,key"));
            Assert.That(ImportReportFormat.Header(both), Is.EqualTo("row,outcome,reason,field,list,consent"));
            Assert.That(Line(row, new ImportOptions()), Is.EqualTo("12,created,,\n"));
            Assert.That(Line(row, list), Is.EqualTo("12,created,,,added;bouncedHard\n"));
            Assert.That(Line(row, consent), Is.EqualTo("12,created,,,recorded,jane@example.com\n"));
            Assert.That(Line(row, both), Is.EqualTo("12,created,,,added;bouncedHard,recorded\n"));
            Assert.That(Line(row with { List = null, Consent = null }, both), Is.EqualTo("12,created,,,,\n"));
        });
    }

    [Test]
    public void Part_File_Names_Sort_By_First_Row_And_Parse_Back()
    {
        string name = ImportReportFormat.PartFileName(ImportPhase.Execute, 1234);

        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("report-execute-000000001234.csv.gz"));
            Assert.That(ImportReportFormat.TryParsePartFileName(name, ImportPhase.Execute, out long firstRow), Is.True);
            Assert.That(firstRow, Is.EqualTo(1234));
            Assert.That(ImportReportFormat.TryParsePartFileName(name, ImportPhase.Analyze, out _), Is.False);
            Assert.That(ImportReportFormat.TryParsePartFileName("report-execute-x.csv.gz", ImportPhase.Execute, out _), Is.False);
            Assert.That(ImportReportFormat.TryParsePartFileName("report-execute-12.csv", ImportPhase.Execute, out _), Is.False);
        });
    }

    [Test]
    public void Writer_Stores_Every_Batch_And_Reader_Joins_The_Parts_With_A_Header()
    {
        using var temp = new TempStore();
        var job = TestData.Job();
        temp.Store.CreateJob(job);

        var firstCall = new ImportReportPartWriter(temp.Store, job.JobId, ImportPhase.Analyze, includeKey: false);
        firstCall.Append([Row(1), Row(2, ImportRowOutcome.Updated)]);
        string[] afterFirstBatch = Read(temp, job.JobId, ImportPhase.Analyze);
        firstCall.Append([Row(5, ImportRowOutcome.Skipped, ImportRowReasons.MultipleMatches)]);
        firstCall.Append([]);
        var secondCall = new ImportReportPartWriter(temp.Store, job.JobId, ImportPhase.Analyze, includeKey: false);
        secondCall.Append([Row(9, ImportRowOutcome.Unchanged)]);

        Assert.Multiple(() =>
        {
            Assert.That(afterFirstBatch, Is.EqualTo(new[] { "row,outcome,reason,field", "1,created,,", "2,updated,," }));
            Assert.That(temp.Store.ListReports(job.JobId), Is.EqualTo(new[] { "report-analyze-000000000001.csv.gz", "report-analyze-000000000009.csv.gz" }));
            Assert.That(Read(temp, job.JobId, ImportPhase.Analyze), Is.EqualTo(new[]
            {
                "row,outcome,reason,field",
                "1,created,,",
                "2,updated,,",
                "5,skipped,multipleMatches,",
                "9,unchanged,,",
            }));
            Assert.That(Read(temp, job.JobId, ImportPhase.Execute), Is.Empty);
            Assert.That(new ImportReportReader(temp.Store).HasReport(job.JobId, ImportPhase.Analyze), Is.True);
            Assert.That(new ImportReportReader(temp.Store).HasReport(job.JobId, ImportPhase.Execute), Is.False);
            Assert.That(new ImportReportReader(temp.Store).GetDownloadFileName(ImportPhase.Execute), Is.EqualTo("contact-import-execute-report.csv.gz"));
        });
    }

    [Test]
    public void Reader_Drops_Rows_A_Later_Call_Processed_Again()
    {
        using var temp = new TempStore();
        var job = TestData.Job() with { Options = new ImportOptions { ReportIncludesKey = true } };
        temp.Store.CreateJob(job);

        // The first call stored rows 1-4 but stopped before the checkpoint of rows 3-4; the next call started at row 3.
        var stopped = new ImportReportPartWriter(temp.Store, job.JobId, ImportPhase.Execute, includeKey: true);
        stopped.Append([Row(1, key: "a"), Row(2, key: "b")]);
        stopped.Append([Row(3, key: "c"), Row(4, key: "d")]);
        var resumed = new ImportReportPartWriter(temp.Store, job.JobId, ImportPhase.Execute, includeKey: true);
        resumed.Append([Row(3, ImportRowOutcome.Unchanged, key: "c"), Row(4, ImportRowOutcome.Unchanged, key: "d")]);
        resumed.Append([Row(5, key: "e")]);

        Assert.That(Read(temp, job.JobId, ImportPhase.Execute), Is.EqualTo(new[]
        {
            "row,outcome,reason,field,key",
            "1,created,,,a",
            "2,created,,,b",
            "3,unchanged,,,c",
            "4,unchanged,,,d",
            "5,created,,,e",
        }));
    }

    [Test]
    public void Writer_Starts_A_New_Part_When_The_Current_One_Is_Large()
    {
        using var temp = new TempStore();
        var job = TestData.Job() with { Options = new ImportOptions { ReportIncludesKey = true } };
        temp.Store.CreateJob(job);
        var writer = new ImportReportPartWriter(temp.Store, job.JobId, ImportPhase.Analyze, includeKey: true);
        var random = new Random(1);

        long row = 0;
        for (int batch = 0; batch < 12; batch++)
        {
            // Random keys do not compress, so a few batches exceed the part size.
            writer.Append([.. Enumerable.Range(0, 1000).Select(_ => Row(++row, key: Convert.ToHexString(BitConverter.GetBytes(random.NextInt64())) + "@example.com"))]);
        }

        string[] lines = Read(temp, job.JobId, ImportPhase.Analyze);
        Assert.Multiple(() =>
        {
            Assert.That(temp.Store.ListReports(job.JobId), Has.Count.GreaterThan(1));
            Assert.That(lines, Has.Length.EqualTo(12001));
            Assert.That(lines.Skip(1).Select(l => long.Parse(l.AsSpan(0, l.IndexOf(',')), System.Globalization.CultureInfo.InvariantCulture)), Is.EqualTo(Enumerable.Range(1, 12000).Select(i => (long)i)));
        });
    }

    [Test]
    public void Store_Refuses_Report_Names_That_Could_Leave_The_Job_Folder()
    {
        using var temp = new TempStore();
        var job = TestData.Job();
        temp.Store.CreateJob(job);

        Assert.Multiple(() =>
        {
            Assert.That(() => temp.Store.WriteReport(job.JobId, "status.json", [1]), Throws.ArgumentException);
            Assert.That(() => temp.Store.WriteReport(job.JobId, "report-/../x.gz", [1]), Throws.ArgumentException);
            Assert.That(() => temp.Store.WriteReport(job.JobId, "report-..\\x.gz", [1]), Throws.ArgumentException);
            Assert.That(() => temp.Store.OpenReport(job.JobId, "report-a..b"), Throws.ArgumentException);
            Assert.That(() => temp.Store.DeleteReport(job.JobId, "../status.json"), Throws.ArgumentException);
            Assert.That(temp.Store.OpenReport(job.JobId, "report-missing.csv.gz"), Is.Null);
            Assert.That(() => temp.Store.DeleteReport(job.JobId, "report-missing.csv.gz"), Throws.Nothing);
        });

        temp.Store.WriteReport(job.JobId, "report-analyze-000000000001.csv.gz", [1, 2]);
        temp.Store.DeleteReport(job.JobId, "report-analyze-000000000001.csv.gz");
        Assert.That(temp.Store.ListReports(job.JobId), Is.Empty);
    }

    [Test]
    public void Aggregates_View_Reads_Counts_And_Field_Statistics()
    {
        var counts = new ImportCounts();
        counts.Add(ImportAggregateKeys.Rows, 10);
        counts.Add(ImportAggregateKeys.Created, 3);
        counts.Add(ImportAggregateKeys.Updated, 2);
        counts.Add(ImportAggregateKeys.SkippedNew);
        counts.Add(ImportAggregateKeys.SkippedInvalidEmail, 2);
        counts.Add(ImportAggregateKeys.FieldChangedPrefix + "B", 2);
        counts.Add(ImportAggregateKeys.FieldClearedPrefix + "B");
        counts.Add(ImportAggregateKeys.FieldChangedPrefix + "A");
        counts.Add(ImportAggregateKeys.Unchanged, 0);

        var stored = counts.AddTo(new Dictionary<string, long> { [ImportAggregateKeys.Rows] = 5, [ImportAggregateKeys.DuplicateRows] = 1 });
        var view = ImportAggregates.From(stored);

        Assert.Multiple(() =>
        {
            Assert.That(counts.Values.ContainsKey(ImportAggregateKeys.Unchanged), Is.False, "Zero counts are not stored.");
            Assert.That(view.Rows, Is.EqualTo(15));
            Assert.That(view.Created, Is.EqualTo(3));
            Assert.That(view.Updated, Is.EqualTo(2));
            Assert.That(view.DuplicateRows, Is.EqualTo(1));
            Assert.That(view.Skipped, Is.EqualTo(3));
            Assert.That(view.Fields, Is.EqualTo(new[] { new ImportFieldChangeStat("A", 1, 0), new ImportFieldChangeStat("B", 2, 1) }));
            Assert.That(ImportAggregates.From(new Dictionary<string, long>()), Is.EqualTo(new ImportAggregates()).Using<ImportAggregates>((x, y) => x.Rows == y.Rows && x.Fields.Count == y.Fields.Count));
        });
    }
}
