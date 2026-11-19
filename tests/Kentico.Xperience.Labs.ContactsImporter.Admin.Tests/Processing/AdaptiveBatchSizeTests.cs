namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

public class AdaptiveBatchSizeTests
{
    [TestCase(1000, 1000, 2.0, 1000, TestName = "On target: kept")]
    [TestCase(1000, 1000, 2.4, 1000, TestName = "Slightly slow: kept")]
    [TestCase(1000, 1000, 1.7, 1000, TestName = "Slightly fast: kept")]
    [TestCase(800, 800, 4.0, 400, TestName = "Twice the target: halved")]
    [TestCase(800, 800, 3.0, 533, TestName = "Slow: scaled by target / actual")]
    [TestCase(800, 800, 60.0, 200, TestName = "Very slow: at most a quarter")]
    [TestCase(100, 100, 1.0, 150, TestName = "Fast: at most x1.5")]
    [TestCase(100, 100, 1.5, 133, TestName = "Fast: scaled by target / actual")]
    [TestCase(100, 40, 0.1, 100, TestName = "Fast partial batch at the end of a chunk: kept")]
    [TestCase(100, 40, 4.0, 50, TestName = "Slow partial batch: shrinks")]
    [TestCase(900, 900, 0.1, 1000, TestName = "Growth clamped to the maximum")]
    [TestCase(60, 60, 30.0, 50, TestName = "Shrink clamped to the minimum")]
    [TestCase(500, 500, 0.0, 500, TestName = "No measured time: kept")]
    [TestCase(500, 500, double.NaN, 500, TestName = "Invalid time: kept")]
    [TestCase(5000, 5000, 2.0, 1000, TestName = "Current above the maximum: clamped")]
    [TestCase(10, 10, 2.0, 50, TestName = "Current below the minimum: clamped")]
    public void Next_Size_Follows_The_Batch_Time_Toward_The_Target(int current, int rows, double seconds, int expected) =>
        Assert.That(AdaptiveBatchSize.Next(current, rows, seconds, 2.0, 50, 1000, loadError: false), Is.EqualTo(expected));

    [Test]
    public void Fast_Batch_Grows_By_At_Least_One_Row() =>
        Assert.That(AdaptiveBatchSize.Next(1, 1, 1.5, 2.0, 1, 1000, loadError: false), Is.EqualTo(2));

    [TestCase(1000, 500)]
    [TestCase(101, 50)]
    [TestCase(80, 50)]
    [TestCase(50, 50)]
    public void Load_Error_Halves_The_Size_Down_To_The_Minimum(int current, int expected) =>
        Assert.That(AdaptiveBatchSize.Next(current, current, 0.1, 2.0, 50, 1000, loadError: true), Is.EqualTo(expected));

    [Test]
    public void Minimum_Above_The_Maximum_Means_A_Fixed_Size() => Assert.Multiple(() =>
    {
        Assert.That(AdaptiveBatchSize.Next(20, 20, 30.0, 2.0, 50, 20, loadError: false), Is.EqualTo(20));
        Assert.That(AdaptiveBatchSize.Next(20, 20, 0.1, 2.0, 50, 20, loadError: true), Is.EqualTo(20));
        Assert.That(new ContactsImporterOptions { BatchSize = 20, MinBatchSize = 50 }.EffectiveMinBatchSize, Is.EqualTo(20));
    });

    [Test]
    public void Current_Size_Is_The_Stored_One_Only_For_An_Adaptive_Import()
    {
        var options = new ContactsImporterOptions { BatchSize = 1000, MinBatchSize = 50 };
        var stored = new ImportCheckpoint { Phase = ImportPhase.Execute, BatchSize = 300 };

        Assert.Multiple(() =>
        {
            Assert.That(AdaptiveBatchSize.Current(ImportPhase.Execute, stored, options), Is.EqualTo(300));
            Assert.That(AdaptiveBatchSize.Current(ImportPhase.Execute, new ImportCheckpoint { Phase = ImportPhase.Execute }, options), Is.EqualTo(1000), "Job of an earlier version.");
            Assert.That(AdaptiveBatchSize.Current(ImportPhase.Analyze, stored, options), Is.EqualTo(1000));
            Assert.That(AdaptiveBatchSize.Current(ImportPhase.Execute, stored, new ContactsImporterOptions { BatchSize = 1000, AdaptiveBatchSize = false }), Is.EqualTo(1000));
            Assert.That(AdaptiveBatchSize.Current(ImportPhase.Execute, stored, new ContactsImporterOptions { BatchSize = 200 }), Is.EqualTo(200), "Lowered maximum applies to a running import.");
            Assert.That(AdaptiveBatchSize.Current(ImportPhase.Execute, stored, new ContactsImporterOptions { BatchSize = 1000, MinBatchSize = 400 }), Is.EqualTo(400));
        });
    }

    [Test]
    public void Load_Errors_Are_Timeouts_Found_Through_Wrapping_Exceptions() => Assert.Multiple(() =>
    {
        Assert.That(AdaptiveBatchSize.IsLoadError(new TimeoutException()), Is.True);
        Assert.That(AdaptiveBatchSize.IsLoadError(new InvalidOperationException("wrapper", new TimeoutException())), Is.True);
        Assert.That(AdaptiveBatchSize.IsLoadError(new IOException()), Is.False, "Storage problem, not database load.");
        Assert.That(AdaptiveBatchSize.IsLoadError(new InvalidOperationException()), Is.False);
    });

    [Test]
    public void Stored_Size_Survives_Serialization_And_Old_Status_Has_None()
    {
        var checkpoint = new ImportCheckpoint { Phase = ImportPhase.Execute, ChunkIndex = 2, RowOffset = 300, LastRow = 2300, BatchSize = 37 };
        byte[] oldJson = """{"phase":"execute","chunkIndex":2,"rowOffset":300,"lastRow":2300}"""u8.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ImportJson.Deserialize<ImportCheckpoint>(ImportJson.Serialize(checkpoint)), Is.EqualTo(checkpoint));
            Assert.That(ImportJson.Deserialize<ImportCheckpoint>(oldJson).BatchSize, Is.Null);
        });
    }

    [Test]
    public void Options_Are_Validated()
    {
        var validator = new ContactsImporterOptionsValidator();
        var defaults = new ContactsImporterOptions();

        Assert.Multiple(() =>
        {
            Assert.That(defaults.AdaptiveBatchSize, Is.True);
            Assert.That(defaults.TargetBatchSeconds, Is.EqualTo(2));
            Assert.That(defaults.MinBatchSize, Is.EqualTo(50));
            Assert.That(validator.Validate(null, defaults).Succeeded, Is.True);
            Assert.That(validator.Validate(null, new ContactsImporterOptions { BatchSize = 20 }).Succeeded, Is.True, "Minimum above the batch size means a fixed size.");
            Assert.That(validator.Validate(null, new ContactsImporterOptions { MinBatchSize = 0 }).FailureMessage, Does.Contain(nameof(ContactsImporterOptions.MinBatchSize)));
            Assert.That(validator.Validate(null, new ContactsImporterOptions { TargetBatchSeconds = 0 }).FailureMessage, Does.Contain(nameof(ContactsImporterOptions.TargetBatchSeconds)));
            Assert.That(validator.Validate(null, new ContactsImporterOptions { TargetBatchSeconds = double.NaN }).FailureMessage, Does.Contain(nameof(ContactsImporterOptions.TargetBatchSeconds)));
            Assert.That(validator.Validate(null, new ContactsImporterOptions { TargetBatchSeconds = double.PositiveInfinity }).FailureMessage, Does.Contain(nameof(ContactsImporterOptions.TargetBatchSeconds)));
        });
    }
}
