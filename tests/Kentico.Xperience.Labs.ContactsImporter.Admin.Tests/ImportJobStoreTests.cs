using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

public class ImportJobStoreTests
{
    private TempStore temp = null!;
    private ImportJobStore store = null!;

    [SetUp]
    public void SetUp()
    {
        temp = new TempStore();
        store = temp.Store;
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    private static ImportChunkInfo Chunk(int index, int rows = 2, long firstRow = 1) =>
        new() { Index = index, Sha256 = new string('a', 64), Rows = rows, FirstRow = firstRow, LastRow = firstRow + rows - 1 };

    [Test]
    public void CreateJob_Writes_Job_And_Initial_Status()
    {
        var job = TestData.Job();

        var status = store.CreateJob(job);

        var readJob = store.ReadJob(job.JobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(store.JobExists(job.JobId), Is.True);
            Assert.That(File.Exists(temp.JobPath(job.JobId, "job.json")), Is.True);
            Assert.That(File.Exists(temp.JobPath(job.JobId, "status.json")), Is.True);
            Assert.That(readJob with { Mapping = [] }, Is.EqualTo(job with { Mapping = [] }));
            Assert.That(readJob.Mapping, Is.EqualTo(job.Mapping));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Configuring));
            Assert.That(store.ReadStatus(job.JobId)!.State, Is.EqualTo(ImportJobState.Configuring));
            Assert.That(store.ReadStatus(job.JobId)!.UpdatedUtc, Is.EqualTo(job.CreatedUtc));
            Assert.That(Directory.GetFiles(temp.JobPath(job.JobId)), Has.Length.EqualTo(2), "no temporary files left");
        });
    }

    [Test]
    public void CreateJob_Twice_Throws()
    {
        var job = TestData.Job();
        store.CreateJob(job);

        Assert.That(() => store.CreateJob(job), Throws.InvalidOperationException);
    }

    [Test]
    public void Reads_Of_Unknown_Job_Return_Nothing()
    {
        var jobId = Guid.NewGuid();

        Assert.Multiple(() =>
        {
            Assert.That(store.JobExists(jobId), Is.False);
            Assert.That(store.ReadJob(jobId), Is.Null);
            Assert.That(store.ReadStatus(jobId), Is.Null);
            Assert.That(store.ReadChunkInfo(jobId, 0), Is.Null);
            Assert.That(store.OpenChunk(jobId, 0), Is.Null);
            Assert.That(store.ListChunks(jobId), Is.Empty);
            Assert.That(store.ListReports(jobId), Is.Empty);
            Assert.That(store.HasChunks(jobId), Is.False);
            Assert.That(store.UpdateStatus(jobId, s => s), Is.Null);
            Assert.That(store.ListJobs(), Is.Empty);
        });
    }

    [Test]
    public void Writes_To_Unknown_Job_Throw_And_Create_Nothing()
    {
        var job = TestData.Job();

        Assert.Multiple(() =>
        {
            Assert.That(() => store.WriteJob(job), Throws.InvalidOperationException);
            Assert.That(() => store.WriteStatus(job.JobId, new ImportJobStatus()), Throws.InvalidOperationException);
            Assert.That(() => store.WriteChunk(job.JobId, Chunk(0), [1, 2, 3]), Throws.InvalidOperationException);
            Assert.That(Directory.Exists(temp.JobPath(job.JobId)), Is.False);
        });
    }

    [Test]
    public void WriteJob_And_WriteStatus_Replace_Existing_Files()
    {
        var job = TestData.Job();
        store.CreateJob(job);
        var status = new ImportJobStatus
        {
            State = ImportJobState.Running,
            Checkpoint = new ImportCheckpoint { Phase = ImportPhase.Execute, ChunkIndex = 2, RowOffset = 500 },
            Aggregates = new Dictionary<string, long> { ["created"] = 12 },
            HeartbeatUtc = TestData.Now,
            DriverToken = "token",
            LastInstance = "instance",
            UpdatedUtc = TestData.Now,
        };

        store.WriteJob(job with { KeyField = "ContactFirstName" });
        store.WriteStatus(job.JobId, status);

        var readStatus = store.ReadStatus(job.JobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(store.ReadJob(job.JobId)!.KeyField, Is.EqualTo("ContactFirstName"));
            Assert.That(readStatus.State, Is.EqualTo(ImportJobState.Running));
            Assert.That(readStatus.Checkpoint, Is.EqualTo(status.Checkpoint));
            Assert.That(readStatus.Aggregates, Is.EqualTo(status.Aggregates));
            Assert.That(readStatus.HeartbeatUtc, Is.EqualTo(TestData.Now));
            Assert.That(readStatus.HeartbeatUtc!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(readStatus.DriverToken, Is.EqualTo("token"));
            Assert.That(Directory.GetFiles(temp.JobPath(job.JobId)), Has.Length.EqualTo(2), "no temporary files left");
        });
    }

    [Test]
    public void Paused_State_Cannot_Be_Stored()
    {
        var job = TestData.Job();
        store.CreateJob(job);

        Assert.That(() => store.WriteStatus(job.JobId, new ImportJobStatus { State = ImportJobState.Paused }), Throws.ArgumentException);
    }

    [Test]
    public void UpdateStatus_Applies_Concurrent_Updates_Without_Losing_Any()
    {
        var job = TestData.Job();
        store.CreateJob(job);

        Parallel.For(0, 40, _ => store.UpdateStatus(job.JobId, s => s with { Checkpoint = s.Checkpoint with { RowOffset = s.Checkpoint.RowOffset + 1 } }));

        Assert.That(store.ReadStatus(job.JobId)!.Checkpoint.RowOffset, Is.EqualTo(40));
    }

    [Test]
    public void Corrupt_Status_File_Throws_InvalidDataException()
    {
        var job = TestData.Job();
        store.CreateJob(job);
        File.WriteAllText(temp.JobPath(job.JobId, "status.json"), "{ not json");

        Assert.That(() => store.ReadStatus(job.JobId), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Chunks_Are_Written_Read_Listed_In_Order_And_Deleted()
    {
        var job = TestData.Job();
        store.CreateJob(job);
        byte[] content = Encoding.UTF8.GetBytes("chunk content");

        store.WriteChunk(job.JobId, Chunk(10, firstRow: 21), content);
        store.WriteChunk(job.JobId, Chunk(2, firstRow: 5), content);
        store.WriteChunk(job.JobId, Chunk(0), content);

        using (var stream = store.OpenChunk(job.JobId, 2)!)
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(content));
        }

        Assert.Multiple(() =>
        {
            Assert.That(store.ListChunks(job.JobId).Select(c => c.Index), Is.EqualTo(new[] { 0, 2, 10 }));
            Assert.That(store.ReadChunkInfo(job.JobId, 2), Is.EqualTo(Chunk(2, firstRow: 5)));
            Assert.That(store.ReadChunkInfo(job.JobId, 1), Is.Null);
            Assert.That(store.ReadChunkInfo(job.JobId, -1), Is.Null);
            Assert.That(store.OpenChunk(job.JobId, 1), Is.Null);
            Assert.That(store.HasChunks(job.JobId), Is.True);
            Assert.That(Directory.GetFiles(temp.JobPath(job.JobId, "chunks")).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "0.json", "0.ndjson.gz", "2.json", "2.ndjson.gz", "10.json", "10.ndjson.gz" }));
        });

        store.DeleteChunk(job.JobId, 2);
        Assert.That(store.ListChunks(job.JobId).Select(c => c.Index), Is.EqualTo(new[] { 0, 10 }));

        store.DeleteChunks(job.JobId);
        Assert.Multiple(() =>
        {
            Assert.That(store.ListChunks(job.JobId), Is.Empty);
            Assert.That(store.HasChunks(job.JobId), Is.False);
            Assert.That(Directory.Exists(temp.JobPath(job.JobId, "chunks")), Is.False);
            Assert.That(store.ReadJob(job.JobId), Is.Not.Null, "deleting chunks keeps the job");
        });
    }

    [Test]
    public void WriteChunk_Replaces_Chunk_With_Same_Index()
    {
        var job = TestData.Job();
        store.CreateJob(job);
        store.WriteChunk(job.JobId, Chunk(0, rows: 2), [1, 2]);

        store.WriteChunk(job.JobId, Chunk(0, rows: 5), [3, 4, 5]);

        Assert.Multiple(() =>
        {
            Assert.That(store.ReadChunkInfo(job.JobId, 0)!.Rows, Is.EqualTo(5));
            Assert.That(File.ReadAllBytes(temp.JobPath(job.JobId, "chunks", "0.ndjson.gz")), Is.EqualTo(new byte[] { 3, 4, 5 }));
            Assert.That(Directory.GetFiles(temp.JobPath(job.JobId, "chunks")), Has.Length.EqualTo(2));
        });
    }

    [Test]
    public void ListChunks_Ignores_Data_Without_Metadata_And_Foreign_Files()
    {
        var job = TestData.Job();
        store.CreateJob(job);
        store.WriteChunk(job.JobId, Chunk(0), [1]);
        string chunks = temp.JobPath(job.JobId, "chunks");
        File.WriteAllBytes(Path.Combine(chunks, "1.ndjson.gz"), [1]);
        File.WriteAllText(Path.Combine(chunks, "notes.json"), "{}");
        File.WriteAllText(Path.Combine(chunks, "5.json.abc.tmp"), "{}");
        File.WriteAllBytes(Path.Combine(chunks, "7.json"), ImportJson.Serialize(Chunk(8)));

        Assert.That(store.ListChunks(job.JobId).Select(c => c.Index), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public void Reports_Are_Listed_And_Deleted_Without_Touching_Other_Files()
    {
        var job = TestData.Job();
        store.CreateJob(job);
        File.WriteAllText(temp.JobPath(job.JobId, "report-analyze.csv.gz"), "a");
        File.WriteAllText(temp.JobPath(job.JobId, "report-execute.csv.gz"), "b");

        Assert.That(store.ListReports(job.JobId), Is.EqualTo(new[] { "report-analyze.csv.gz", "report-execute.csv.gz" }));

        store.DeleteReports(job.JobId);

        Assert.Multiple(() =>
        {
            Assert.That(store.ListReports(job.JobId), Is.Empty);
            Assert.That(store.ReadJob(job.JobId), Is.Not.Null);
            Assert.That(store.ReadStatus(job.JobId), Is.Not.Null);
        });
    }

    [Test]
    public void ListJobs_Returns_Only_Guid_Folders_And_DeleteJob_Removes_Everything()
    {
        var first = TestData.Job();
        var second = TestData.Job();
        store.CreateJob(first);
        store.CreateJob(second);
        store.WriteChunk(first.JobId, Chunk(0), [1]);
        Directory.CreateDirectory(Path.Combine(temp.Root, "not-a-job"));
        File.WriteAllText(Path.Combine(temp.Root, "readme.txt"), "x");

        Assert.That(store.ListJobs(), Is.EquivalentTo(new[] { first.JobId, second.JobId }));

        store.DeleteJob(first.JobId);

        Assert.Multiple(() =>
        {
            Assert.That(store.ListJobs(), Is.EqualTo(new[] { second.JobId }));
            Assert.That(Directory.Exists(temp.JobPath(first.JobId)), Is.False);
            Assert.That(Directory.Exists(Path.Combine(temp.Root, "not-a-job")), Is.True);
            Assert.That(() => store.DeleteJob(first.JobId), Throws.Nothing);
        });
    }

    [Test]
    public void Job_Folder_Is_Directly_Under_The_Root_With_The_Canonical_Id()
    {
        var job = TestData.Job(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"));

        store.CreateJob(job);

        Assert.That(Directory.GetDirectories(temp.Root).Select(Path.GetFileName), Is.EqualTo(new[] { "6f9619ff-8b86-d011-b42d-00c04fc964ff" }));
    }

    [TestCase("6f9619ff-8b86-d011-b42d-00c04fc964ff", true)]
    [TestCase("6F9619FF-8B86-D011-B42D-00C04FC964FF", true)]
    [TestCase("6f9619ff8b86d011b42d00c04fc964ff", false)]
    [TestCase("{6f9619ff-8b86-d011-b42d-00c04fc964ff}", false)]
    [TestCase(" 6f9619ff-8b86-d011-b42d-00c04fc964ff", false)]
    [TestCase("..", false)]
    [TestCase("../6f9619ff-8b86-d011-b42d-00c04fc964ff", false)]
    [TestCase("00000000-0000-0000-0000-000000000000", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void ImportJobId_Accepts_Only_Canonical_Guids(string? value, bool expected)
    {
        bool parsed = ImportJobId.TryParse(value, out var jobId);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.EqualTo(expected));
            Assert.That(jobId != Guid.Empty, Is.EqualTo(expected));
        });
    }
}
