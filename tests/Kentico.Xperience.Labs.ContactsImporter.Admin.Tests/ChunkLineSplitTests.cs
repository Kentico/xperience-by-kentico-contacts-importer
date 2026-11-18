using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// The upload check (<see cref="ChunkValidator"/>) and the processing (<see cref="ChunkRecordReader"/>) split a chunk
/// into the same records: on line feeds only, with a carriage return as JSON whitespace inside the record.
/// </summary>
public class ChunkLineSplitTests
{
    private const long MaxBytes = 10 * 1024 * 1024;

    private static readonly string first = TestData.Record(1, "jane@example.com");
    private static readonly string second = TestData.Record(2, "joe@example.com", "Joe");

    private static IEnumerable<TestCaseData> AcceptedChunks()
    {
        yield return new TestCaseData(first + "\n" + second + "\n").SetName("Line_Feeds");
        yield return new TestCaseData(first + "\r\n" + second + "\r\n").SetName("Carriage_Return_Line_Feeds");
        yield return new TestCaseData(first + "\r\n" + second).SetName("Carriage_Return_Line_Feed_Without_A_Last_One");
        yield return new TestCaseData(first.Replace(",\"k\":", ",\r\"k\":", StringComparison.Ordinal) + "\n" + second).SetName("Carriage_Return_Inside_A_Record");
        yield return new TestCaseData("\r" + first + "\r\r\n" + second.Replace(":{", ":\r{", StringComparison.Ordinal) + "\r").SetName("Carriage_Returns_Around_And_Inside_Records");
    }

    [TestCaseSource(nameof(AcceptedChunks))]
    public void Validator_And_Reader_Read_The_Same_Records(string content)
    {
        var validation = Validate(content, expectedRows: 2);
        var records = Read(content);
        var skipped = ReadAfterSkip(content, 1);

        Assert.Multiple(() =>
        {
            Assert.That(validation.IsValid, Is.True, validation.Message);
            Assert.That(validation.Rows, Is.EqualTo(2));
            Assert.That(records.Select(r => (r.Row, r.Key)), Is.EqualTo(new[] { (1L, "jane@example.com"), (2L, "joe@example.com") }));
            Assert.That(records[0].Values, Is.EqualTo(new[] { "jane@example.com", "Jane" }));
            Assert.That(records[1].Values, Is.EqualTo(new[] { "joe@example.com", "Joe" }));
            Assert.That(skipped.Select(r => r.Row), Is.EqualTo(new[] { 2L }));
        });
    }

    [Test]
    public void Carriage_Return_Is_Not_A_Record_Break()
    {
        var asOneLine = Validate(first + "\r" + second, expectedRows: 1);
        var asTwoLines = Validate(first + "\r" + second, expectedRows: 2);

        Assert.Multiple(() =>
        {
            Assert.That(asOneLine.IsValid, Is.False);
            Assert.That(asOneLine.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord), "two objects on one line");
            Assert.That(asTwoLines.IsValid, Is.False);
        });
    }

    private static ChunkValidationResult Validate(string content, int expectedRows)
    {
        Assert.That(ChunkValidationContext.TryCreate(TestData.Job(), TestData.Fields, out var context, out string? error), Is.True, error);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        return ChunkValidator.Validate(stream, context!, MaxBytes, expectedRows);
    }

    private static List<ImportRecord> Read(string content)
    {
        using var reader = CreateReader(content);
        var records = reader.ReadBatch(10);
        Assert.That(reader.ReadBatch(10), Is.Empty);

        return records;
    }

    private static List<ImportRecord> ReadAfterSkip(string content, long skip)
    {
        using var reader = CreateReader(content);
        Assert.That(reader.Skip(skip), Is.EqualTo(skip));

        return reader.ReadBatch(10);
    }

    private static ChunkRecordReader CreateReader(string content)
    {
        Assert.That(ImportProcessingContext.TryCreate(TestData.Job(), TestData.Fields, out var context, out var error), Is.True, error?.ToString());

        return new ChunkRecordReader(new MemoryStream(TestData.Gzip(content)), context!, 0);
    }
}
