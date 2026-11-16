using System.IO.Compression;
using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

internal class ChunkIngestServiceTests
{
    private TempStore temp = null!;
    private MutableTimeProvider time = null!;
    private FakeContactFieldSchemaProvider schema = null!;
    private ChunkIngestService service = null!;
    private Guid jobId;

    [SetUp]
    public void SetUp()
    {
        temp = new TempStore(o =>
        {
            o.ChunkSizeBytes = 2048;
            o.MaxChunkUncompressedBytes = 64 * 1024;
        });
        time = new MutableTimeProvider(TestData.Now);
        schema = new FakeContactFieldSchemaProvider(TestData.Fields);
        service = new ChunkIngestService(temp.Store, schema, temp.Options, time);

        jobId = Guid.NewGuid();
        temp.Store.CreateJob(TestData.Job(jobId));
        SetState(ImportJobState.Uploading);
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    private void SetState(ImportJobState state) => temp.Store.UpdateStatus(jobId, s => s with { State = state });

    private static string[] Records(int firstRow, int count) =>
        [.. Enumerable.Range(firstRow, count).Select(r => TestData.Record(r, $"user{r}@example.com"))];

    [Test]
    public void Valid_Chunk_Is_Stored_With_Metadata()
    {
        var payload = TestData.Payload(jobId, 0, Records(1, 3));

        var result = service.Ingest(payload);

        var info = temp.Store.ReadChunkInfo(jobId, 0);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), result.Message);
            Assert.That(info, Is.EqualTo(new ImportChunkInfo { Index = 0, Sha256 = payload.Sha256!, Rows = 3, FirstRow = 1, LastRow = 3 }));
            Assert.That(File.ReadAllBytes(temp.JobPath(jobId, "chunks", "0.ndjson.gz")), Is.EqualTo(Convert.FromBase64String(payload.Data!)));
        });
    }

    [Test]
    public void Stored_Chunk_Round_Trips_Through_The_Store()
    {
        string[] records = Records(1, 3);
        service.Ingest(TestData.Payload(jobId, 0, records));

        using var chunk = temp.Store.OpenChunk(jobId, 0);
        using var gzip = new GZipStream(chunk!, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        Assert.That(reader.ReadToEnd(), Is.EqualTo(string.Join('\n', records)));
    }

    [Test]
    public void Uppercase_Hash_Is_Accepted_And_Stored_Lowercase()
    {
        var payload = TestData.Payload(jobId, 0, Records(1, 1));

        var result = service.Ingest(payload with { Sha256 = payload.Sha256!.ToUpperInvariant() });

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), result.Message);
            Assert.That(temp.Store.ReadChunkInfo(jobId, 0)!.Sha256, Is.EqualTo(payload.Sha256));
        });
    }

    [Test]
    public void Same_Chunk_Again_Is_Idempotent_In_Any_State()
    {
        var payload = TestData.Payload(jobId, 0, Records(1, 2));
        service.Ingest(payload);

        var whileUploading = service.Ingest(payload);
        SetState(ImportJobState.Uploaded);
        var afterUpload = service.Ingest(payload);

        Assert.Multiple(() =>
        {
            Assert.That(whileUploading.Outcome, Is.EqualTo(ChunkIngestOutcome.AlreadyStored));
            Assert.That(afterUpload.Outcome, Is.EqualTo(ChunkIngestOutcome.AlreadyStored));
            Assert.That(afterUpload.IsAccepted, Is.True);
        });
    }

    [Test]
    public void Different_Content_Replaces_Chunk_Only_While_Uploading()
    {
        service.Ingest(TestData.Payload(jobId, 0, Records(1, 2)));
        var replacement = TestData.Payload(jobId, 0, Records(1, 3));

        var replaced = service.Ingest(replacement);
        SetState(ImportJobState.Uploaded);
        var refused = service.Ingest(TestData.Payload(jobId, 0, Records(1, 4)));

        Assert.Multiple(() =>
        {
            Assert.That(replaced.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), replaced.Message);
            Assert.That(refused.Reason, Is.EqualTo(ChunkRejectReason.InvalidState));
            Assert.That(temp.Store.ReadChunkInfo(jobId, 0)!.Sha256, Is.EqualTo(replacement.Sha256));
            Assert.That(temp.Store.ReadChunkInfo(jobId, 0)!.Rows, Is.EqualTo(3));
        });
    }

    [TestCase(ImportJobState.Configuring)]
    [TestCase(ImportJobState.Uploaded)]
    [TestCase(ImportJobState.Analyzed)]
    [TestCase(ImportJobState.Running)]
    [TestCase(ImportJobState.Completed)]
    public void New_Chunk_Is_Rejected_Unless_Uploading(ImportJobState state)
    {
        SetState(state);

        var result = service.Ingest(TestData.Payload(jobId, 0, Records(1, 1)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidState));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-guid")]
    [TestCase("..\\..\\other")]
    [TestCase("../6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [TestCase("6f9619ff8b86d011b42d00c04fc964ff")]
    [TestCase("{6f9619ff-8b86-d011-b42d-00c04fc964ff}")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void Job_Id_Must_Be_A_Canonical_Guid(string? id)
    {
        var result = service.Ingest(TestData.Payload(jobId, 0, Records(1, 1)) with { JobId = id });

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidPayload));
    }

    [Test]
    public void Unknown_Job_Is_Rejected()
    {
        var result = service.Ingest(TestData.Payload(Guid.NewGuid(), 0, Records(1, 1)));

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.JobNotFound));
    }

    [Test]
    public void Malformed_Payload_Values_Are_Rejected()
    {
        var payload = TestData.Payload(jobId, 0, Records(1, 1));

        ChunkIngestResult[] results =
        [
            service.Ingest(payload with { Index = -1 }),
            service.Ingest(payload with { Index = ChunkIngestService.MaxChunkCount }),
            service.Ingest(payload with { Rows = 0 }),
            service.Ingest(payload with { Sha256 = null }),
            service.Ingest(payload with { Sha256 = "abc" }),
            service.Ingest(payload with { Sha256 = new string('z', 64) }),
            service.Ingest(payload with { Data = null }),
            service.Ingest(payload with { Data = "" }),
            service.Ingest(payload with { Data = "***not base64***" }),
        ];

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Reason), Is.All.EqualTo(ChunkRejectReason.InvalidPayload));
            Assert.That(results.Select(r => r.Message), Is.All.Not.Empty);
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [Test]
    public void Hash_Mismatch_Is_Rejected()
    {
        var payload = TestData.Payload(jobId, 0, Records(1, 1));
        var other = TestData.Payload(jobId, 0, Records(1, 2));

        var result = service.Ingest(payload with { Sha256 = other.Sha256 });

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.HashMismatch));
    }

    [Test]
    public void Compressed_Chunk_Larger_Than_Limit_Is_Rejected_Before_Decoding()
    {
        byte[] random = new byte[(int)temp.Options.Value.MaxChunkCompressedBytes + 1];
        new Random(1).NextBytes(random);

        var justOver = service.Ingest(new UploadChunkPayload { JobId = jobId.ToString(), Index = 0, Rows = 1, Sha256 = TestData.Sha256Hex(random), Data = Convert.ToBase64String(random) });
        var farOver = service.Ingest(new UploadChunkPayload { JobId = jobId.ToString(), Index = 0, Rows = 1, Sha256 = TestData.Sha256Hex(random), Data = new string('!', 100_000) });

        Assert.Multiple(() =>
        {
            Assert.That(justOver.Reason, Is.EqualTo(ChunkRejectReason.TooLarge));
            Assert.That(farOver.Reason, Is.EqualTo(ChunkRejectReason.TooLarge), "size is checked before base64 decoding");
        });
    }

    [Test]
    public void Data_That_Is_Not_Gzip_Is_Rejected()
    {
        byte[] notGzip = Encoding.UTF8.GetBytes(TestData.Record(1, "a@b.c"));

        var result = service.Ingest(new UploadChunkPayload { JobId = jobId.ToString(), Index = 0, Rows = 1, Sha256 = TestData.Sha256Hex(notGzip), Data = Convert.ToBase64String(notGzip) });

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidGzip));
    }

    [Test]
    public void Decompression_Bomb_Is_Rejected()
    {
        // 1 MB of spaces compresses to about 1 KB: within the compressed limit, far over the decompressed limit.
        byte[] bomb = TestData.Gzip(new byte[1024 * 1024].Select(_ => (byte)' ').ToArray());
        Assert.That(bomb, Has.Length.LessThan(temp.Options.Value.MaxChunkCompressedBytes));

        var result = service.Ingest(new UploadChunkPayload { JobId = jobId.ToString(), Index = 0, Rows = 1, Sha256 = TestData.Sha256Hex(bomb), Data = Convert.ToBase64String(bomb) });

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.DecompressedTooLarge));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [Test]
    public void Invalid_Record_Rejects_Whole_Chunk_With_Row_Number()
    {
        string[] records = [TestData.Record(10, "a@b.c"), TestData.Record(11, "b@b.c", new string('x', 101)), TestData.Record(12, "c@b.c")];

        var result = service.Ingest(TestData.Payload(jobId, 0, records));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ChunkIngestOutcome.Rejected));
            Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
            Assert.That(result.RowNumber, Is.EqualTo(11));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [Test]
    public void Declared_Row_Count_Must_Match()
    {
        var payload = TestData.Payload(jobId, 0, Records(1, 3));

        var result = service.Ingest(payload with { Rows = 2 });

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.RowCountMismatch));
    }

    [Test]
    public void Value_Length_Comes_From_The_Current_Class_Schema()
    {
        schema.Fields = [.. TestData.Fields.Select(f => f.Name == "ContactFirstName" ? f with { MaxLength = 3 } : f)];

        var result = service.Ingest(TestData.Payload(jobId, 0, TestData.Record(1, "a@b.c", "Jane")));

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
    }

    [Test]
    public void Mapped_Field_Missing_From_Schema_Rejects_Chunk()
    {
        schema.Fields = [.. TestData.Fields.Where(f => f.Name != "ContactFirstName")];

        var result = service.Ingest(TestData.Payload(jobId, 0, Records(1, 1)));

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidConfiguration));
    }

    [Test]
    public void Row_Numbers_Must_Follow_Neighbor_Chunks_Uploaded_In_Any_Order()
    {
        var third = service.Ingest(TestData.Payload(jobId, 2, Records(21, 5)));
        var first = service.Ingest(TestData.Payload(jobId, 0, Records(1, 5)));
        var overlapsFirst = service.Ingest(TestData.Payload(jobId, 1, Records(5, 5)));
        var overlapsThird = service.Ingest(TestData.Payload(jobId, 1, Records(17, 5)));
        var second = service.Ingest(TestData.Payload(jobId, 1, Records(6, 15)));

        Assert.Multiple(() =>
        {
            Assert.That(third.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), third.Message);
            Assert.That(first.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), first.Message);
            Assert.That(overlapsFirst.Reason, Is.EqualTo(ChunkRejectReason.RowOrder));
            Assert.That(overlapsThird.Reason, Is.EqualTo(ChunkRejectReason.RowOrder));
            Assert.That(second.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), second.Message);
            Assert.That(temp.Store.ListChunks(jobId).Select(c => c.Index), Is.EqualTo(new[] { 0, 1, 2 }));
        });
    }

    [Test]
    public void CompleteUpload_Stores_Chunk_List_And_Profile_And_Moves_To_Uploaded()
    {
        service.Ingest(TestData.Payload(jobId, 1, Records(4, 2)));
        service.Ingest(TestData.Payload(jobId, 0, Records(1, 3)));
        var profile = new ImportProfile { RowsRead = 7, RowsValid = 5, RowsRejected = 2, LikelyDuplicates = 1, ChunkCount = 2 };
        time.Advance(TimeSpan.FromMinutes(1));

        var result = service.CompleteUpload(jobId, profile);

        var stored = temp.Store.ReadStatus(jobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(UploadCompletionOutcome.Completed), result.Message);
            Assert.That(stored.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(stored.Profile, Is.EqualTo(profile));
            Assert.That(stored.Chunks.Select(c => (c.Index, c.Rows)), Is.EqualTo(new[] { (0, 3), (1, 2) }));
            Assert.That(stored.UpdatedUtc, Is.EqualTo(time.UtcNow));
        });
    }

    [Test]
    public void CompleteUpload_Reports_Missing_Chunks()
    {
        service.Ingest(TestData.Payload(jobId, 1, Records(4, 2)));

        var result = service.CompleteUpload(jobId, new ImportProfile { RowsRead = 10, RowsValid = 10, ChunkCount = 4 });

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(UploadCompletionOutcome.Inconsistent));
            Assert.That(result.MissingChunks, Is.EqualTo(new[] { 0, 2, 3 }));
            Assert.That(temp.Store.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Uploading));
        });
    }

    [Test]
    public void CompleteUpload_Rejects_Totals_That_Do_Not_Match()
    {
        service.Ingest(TestData.Payload(jobId, 0, Records(1, 3)));
        service.Ingest(TestData.Payload(jobId, 1, Records(4, 2)));

        var wrongRows = service.CompleteUpload(jobId, new ImportProfile { RowsRead = 6, RowsValid = 6, ChunkCount = 2 });
        var extraChunk = service.CompleteUpload(jobId, new ImportProfile { RowsRead = 5, RowsValid = 3, ChunkCount = 1 });
        var noChunks = service.CompleteUpload(jobId, new ImportProfile { RowsRead = 5, RowsValid = 0, ChunkCount = 0 });
        var negative = service.CompleteUpload(jobId, new ImportProfile { RowsRead = 5, RowsValid = 5, RowsRejected = -1, ChunkCount = 2 });

        Assert.Multiple(() =>
        {
            Assert.That(wrongRows.Outcome, Is.EqualTo(UploadCompletionOutcome.Inconsistent));
            Assert.That(extraChunk.Outcome, Is.EqualTo(UploadCompletionOutcome.Inconsistent));
            Assert.That(noChunks.Outcome, Is.EqualTo(UploadCompletionOutcome.Inconsistent));
            Assert.That(negative.Outcome, Is.EqualTo(UploadCompletionOutcome.Inconsistent));
        });
    }

    [Test]
    public void CompleteUpload_Requires_Uploading_State_And_Existing_Job()
    {
        service.Ingest(TestData.Payload(jobId, 0, Records(1, 3)));
        SetState(ImportJobState.Configuring);

        var wrongState = service.CompleteUpload(jobId, new ImportProfile { RowsRead = 3, RowsValid = 3, ChunkCount = 1 });
        var unknown = service.CompleteUpload(Guid.NewGuid(), new ImportProfile { RowsRead = 3, RowsValid = 3, ChunkCount = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(wrongState.Outcome, Is.EqualTo(UploadCompletionOutcome.InvalidState));
            Assert.That(unknown.Outcome, Is.EqualTo(UploadCompletionOutcome.JobNotFound));
        });
    }

    private static UploadChunkPayload RandomChunk(Guid jobId, int index, int size, int seed)
    {
        // Not gzip: the size limits are checked before the content is looked at. Random bytes do not compress,
        // so a payload of an exact stored size is easy to make.
        byte[] content = new byte[size];
        new Random(seed).NextBytes(content);

        return new UploadChunkPayload { JobId = jobId.ToString(), Index = index, Rows = 1, Sha256 = TestData.Sha256Hex(content), Data = Convert.ToBase64String(content) };
    }

    private void StoreChunk(int index, int size) =>
        temp.Store.WriteChunk(jobId, new ImportChunkInfo { Index = index, Sha256 = new string('b', 64), Rows = 1, FirstRow = (index * 10) + 1, LastRow = (index * 10) + 1 }, new byte[size]);

    [Test]
    public void Chunk_That_Takes_The_Upload_Over_The_File_Size_Limit_Is_Rejected()
    {
        using var small = new TempStore(o => o.MaxFileSizeBytes = 1000);
        var limited = new ChunkIngestService(small.Store, schema, small.Options, time);
        small.Store.CreateJob(TestData.Job(jobId));
        small.Store.UpdateStatus(jobId, s => s with { State = ImportJobState.Uploading });
        small.Store.WriteChunk(jobId, new ImportChunkInfo { Index = 0, Sha256 = new string('b', 64), Rows = 1, FirstRow = 1, LastRow = 1 }, new byte[1500]);

        var overLimit = limited.Ingest(RandomChunk(jobId, 1, 501, seed: 1));
        var atLimit = limited.Ingest(RandomChunk(jobId, 1, 500, seed: 2));

        Assert.Multiple(() =>
        {
            Assert.That(small.Options.Value.MaxUploadBytes, Is.EqualTo(2000));
            Assert.That(overLimit.Reason, Is.EqualTo(ChunkRejectReason.UploadTooLarge));
            Assert.That(overLimit.Message, Does.Contain("2000 bytes"));
            Assert.That(atLimit.Reason, Is.EqualTo(ChunkRejectReason.InvalidGzip), "A chunk that still fits passes the size check and goes on to validation.");
            Assert.That(small.Store.ReadChunkInfo(jobId, 1), Is.Null);
        });
    }

    [Test]
    public void Replaced_Chunk_Does_Not_Count_Twice_Toward_The_Upload_Limit()
    {
        using var small = new TempStore(o => o.MaxFileSizeBytes = 1000);
        var limited = new ChunkIngestService(small.Store, schema, small.Options, time);
        small.Store.CreateJob(TestData.Job(jobId));
        small.Store.UpdateStatus(jobId, s => s with { State = ImportJobState.Uploading });
        small.Store.WriteChunk(jobId, new ImportChunkInfo { Index = 0, Sha256 = new string('b', 64), Rows = 1, FirstRow = 1, LastRow = 1 }, new byte[1500]);

        var replacement = limited.Ingest(RandomChunk(jobId, 0, 1600, seed: 3));
        var tooLargeReplacement = limited.Ingest(RandomChunk(jobId, 0, 2001, seed: 4));

        Assert.Multiple(() =>
        {
            Assert.That(replacement.Reason, Is.EqualTo(ChunkRejectReason.InvalidGzip), "1600 bytes replace the stored 1500 bytes: within the limit.");
            Assert.That(tooLargeReplacement.Reason, Is.EqualTo(ChunkRejectReason.UploadTooLarge));
        });
    }

    [Test]
    public void Valid_Chunks_Are_Stored_Until_The_Upload_Limit_Is_Reached()
    {
        byte[] chunk = Convert.FromBase64String(TestData.Payload(jobId, 0, Records(1, 3)).Data!);
        using var small = new TempStore(o => o.MaxFileSizeBytes = chunk.Length + 20);
        var limited = new ChunkIngestService(small.Store, schema, small.Options, time);
        small.Store.CreateJob(TestData.Job(jobId));
        small.Store.UpdateStatus(jobId, s => s with { State = ImportJobState.Uploading });

        var first = limited.Ingest(TestData.Payload(jobId, 0, Records(1, 3)));
        var second = limited.Ingest(TestData.Payload(jobId, 1, Records(4, 3)));
        var third = limited.Ingest(TestData.Payload(jobId, 2, Records(7, 3)));
        var firstAgain = limited.Ingest(TestData.Payload(jobId, 0, Records(1, 3)));

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), first.Message);
            Assert.That(second.Outcome, Is.EqualTo(ChunkIngestOutcome.Stored), second.Message);
            Assert.That(third.Reason, Is.EqualTo(ChunkRejectReason.UploadTooLarge));
            Assert.That(firstAgain.Outcome, Is.EqualTo(ChunkIngestOutcome.AlreadyStored), "Repeating a stored chunk is still accepted.");
            Assert.That(small.Store.ListChunks(jobId).Select(c => c.Index), Is.EqualTo(new[] { 0, 1 }));
        });
    }

    [Test]
    public void Upload_Limit_Does_Not_Overflow_For_A_Huge_File_Size_Limit()
    {
        var huge = new ContactsImporterOptions { MaxFileSizeBytes = long.MaxValue };
        StoreChunk(0, 100);

        Assert.Multiple(() =>
        {
            Assert.That(huge.MaxUploadBytes, Is.EqualTo(long.MaxValue));
            Assert.That(new ContactsImporterOptions().MaxUploadBytes, Is.EqualTo(10L * 1024 * 1024 * 1024));
            Assert.That(service.Ingest(TestData.Payload(jobId, 1, Records(11, 3))).Outcome, Is.EqualTo(ChunkIngestOutcome.Stored));
        });
    }
}
