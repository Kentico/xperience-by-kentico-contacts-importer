using Microsoft.Extensions.Logging.Abstractions;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.UIPages;

internal class ImportCommandServiceTests
{
    private const int UserId = 53;
    private const string Fingerprint = "0a1b2c3d";

    private TempStore temp = null!;
    private MutableTimeProvider time = null!;
    private FakeContactFieldSchemaProvider schema = null!;
    private FakeUserNameProvider users = null!;
    private ImportCommandService service = null!;
    private Guid jobId;
    private string id = null!;

    [SetUp]
    public void SetUp()
    {
        temp = new TempStore(o =>
        {
            o.ChunkSizeBytes = 2048;
            o.MaxChunkUncompressedBytes = 64 * 1024;
            o.MaxFileSizeBytes = 1_000_000;
        });
        time = new MutableTimeProvider(TestData.Now);
        schema = new FakeContactFieldSchemaProvider(TestData.Fields);
        users = new FakeUserNameProvider { Names = { [UserId] = "administrator" } };
        var ingest = new ChunkIngestService(temp.Store, schema, temp.Options, time);
        var cleanup = new ImportJobCleanupService(temp.Store, temp.Options, time, NullLogger<ImportJobCleanupService>.Instance);
        var processor = new ImportProcessor(
            temp.Store,
            schema,
            new Processing.FakeContactRepository(),
            cleanup,
            new Processing.FakeCacheInvalidator(),
            temp.Options,
            time,
            NullLogger<ImportProcessor>.Instance);
        service = new ImportCommandService(
            temp.Store,
            ingest,
            schema,
            cleanup,
            users,
            processor,
            new ImportReportPager(temp.Store),
            new ImportReportReader(temp.Store),
            new ImportProcessGate(),
            new ResourceImportTexts(),
            new FakeChoiceProvider(),
            new FakeChoiceProvider(),
            temp.Options,
            time);

        jobId = Guid.NewGuid();
        id = jobId.ToString("D");
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    private static SaveConfigRequest Config(
        string keyField = "ContactEmail",
        string fileName = "contacts.csv",
        long size = 1234,
        string fingerprint = Fingerprint,
        string? dateFormat = "iso",
        params (string Source, string Target)[] mapping)
    {
        if (mapping.Length == 0)
        {
            mapping = [("Email", "ContactEmail"), ("First Name", "ContactFirstName")];
        }

        return new SaveConfigRequest
        {
            File = new ImportFileDto { Name = fileName, Size = size, Fingerprint = fingerprint },
            KeyField = keyField,
            Mapping = [.. mapping.Select(m => new ImportMappingDto { Source = m.Source, Target = m.Target })],
            DateFormat = dateFormat,
        };
    }

    private static string[] Records(int firstRow, int count) =>
        [.. Enumerable.Range(firstRow, count).Select(r => TestData.Record(r, $"user{r}@example.com"))];

    private UploadChunkResponse Upload(int index, int firstRow, int count) =>
        service.UploadChunk(id, TestData.Payload(jobId, index, Records(firstRow, count)));

    private void StartUpload() =>
        Assert.That(service.SaveConfig(id, Config(), UserId).Code, Is.EqualTo(ImportCommandCodes.OK));

    private static CompleteUploadRequest Profile(int rows, int chunks, int rejected = 0) =>
        new() { Profile = new ImportProfileDto { RowsRead = rows + rejected, RowsValid = rows, RowsRejected = rejected, ChunkCount = chunks } };

    private ImportJobState State() => temp.Store.ReadStatus(jobId)!.State;

    private int[] StoredChunks() => [.. temp.Store.ListChunks(jobId).Select(c => c.Index)];

    private static readonly string?[] invalidJobIds =
    [
        null,
        "",
        "not-a-guid",
        "..",
        "../../etc",
        "00000000-0000-0000-0000-000000000000",
        "{6f9619ff-8b86-d011-b42d-00c04fc964ff}",
        "6f9619ff8b86d011b42d00c04fc964ff",
        " 6f9619ff-8b86-d011-b42d-00c04fc964ff",
        "6f9619ff-8b86-d011-b42d-00c04fc964ff/..",
    ];

    [Test]
    public void GetConfig_Marks_Fields_That_Reference_Another_Object()
    {
        schema.Fields =
        [
            .. TestData.Fields,
            new("ContactCountryID", "Country", "integer", null, null, new ContactFieldReference("cms.country", "CMS_Country", "CountryID")),
            new("CustomRegionID", "Region", "integer", null, null, new ContactFieldReference(null, "Custom_Region", "RegionID", "dbo")),
        ];

        var fields = service.GetConfig().Fields;

        Assert.Multiple(() =>
        {
            Assert.That(fields[0].IsReference, Is.False);
            Assert.That(fields[0].ReferencedObjectType, Is.Null);
            Assert.That(fields[^2], Is.EqualTo(new ImportFieldDto("ContactCountryID", "Country", "integer", null, true, true, true, "cms.country")));
            Assert.That(fields[^1], Is.EqualTo(new ImportFieldDto("CustomRegionID", "Region", "integer", null, true, true, true, null)));
        });
    }

    [Test]
    public void GetConfig_Returns_Mappable_Fields_And_Limits()
    {
        var config = service.GetConfig();

        Assert.Multiple(() =>
        {
            Assert.That(config.Fields.Select(f => f.Name), Is.EqualTo(TestData.Fields.Select(f => f.Name)));
            Assert.That(config.Fields[0], Is.EqualTo(new ImportFieldDto("ContactEmail", "Email", "text", 254)));
            Assert.That(config.Limits.MaxFileSizeBytes, Is.EqualTo(1_000_000));
            Assert.That(config.Limits.ChunkSizeBytes, Is.EqualTo(2048));
            Assert.That(config.Limits.MaxChunkCompressedBytes, Is.EqualTo(4096));
            Assert.That(config.Limits.MaxChunkUncompressedBytes, Is.EqualTo(64 * 1024));
            Assert.That(config.Limits.MaxNonTextValueLength, Is.EqualTo(ContactFieldDefinition.MaxNonTextValueLength));
            Assert.That(config.Limits.MaxKeyLength, Is.EqualTo(450));
            Assert.That(config.Limits.ParallelUploads, Is.InRange(2, 3));
        });
    }

    [TestCaseSource(nameof(invalidJobIds))]
    public void Every_Command_Refuses_An_Invalid_Job_Id(string? invalidId) => Assert.Multiple(() =>
                                                                                   {
                                                                                       Assert.That(service.GetJob(invalidId).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
                                                                                       Assert.That(service.SaveConfig(invalidId, Config(), UserId).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
                                                                                       Assert.That(service.UploadChunk(invalidId, TestData.Payload(jobId, 0, Records(1, 1))).Code, Is.EqualTo(ImportCommandCodes.REJECTED));
                                                                                       Assert.That(service.TruncateChunks(invalidId, new TruncateChunksRequest { FromIndex = 0 }).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
                                                                                       Assert.That(service.CompleteUpload(invalidId, Profile(1, 1)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
                                                                                       Assert.That(service.Cancel(invalidId, UserId).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
                                                                                       Assert.That(service.DeleteFiles(invalidId, UserId).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
                                                                                       Assert.That(Directory.Exists(temp.Root) ? Directory.GetFileSystemEntries(temp.Root) : [], Is.Empty);
                                                                                   });

    [Test]
    public void GetJob_Of_A_Job_That_Was_Not_Created_Is_Not_Found()
    {
        var response = service.GetJob(id);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
            Assert.That(response.Job, Is.Null);
            Assert.That(response.Status, Is.Null);
            Assert.That(temp.Store.JobExists(jobId), Is.False);
        });
    }

    [Test]
    public void GetJob_Returns_Config_State_And_Stored_Chunks_While_Uploading()
    {
        StartUpload();
        Upload(0, 1, 3);
        Upload(2, 7, 2);

        var response = service.GetJob(id);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Job!.JobId, Is.EqualTo(id));
            Assert.That(response.Job.File.Name, Is.EqualTo("contacts.csv"));
            Assert.That(response.Job.File.Fingerprint, Is.EqualTo(Fingerprint));
            Assert.That(response.Job.KeyField, Is.EqualTo("ContactEmail"));
            Assert.That(response.Job.Mapping.Select(m => (m.Source, m.Target)), Is.EqualTo(new[] { ("Email", "ContactEmail"), ("First Name", "ContactFirstName") }));
            Assert.That(response.Status!.State, Is.EqualTo("Uploading"));
            Assert.That(response.Status.Chunks.Select(c => (c.Index, c.Rows)), Is.EqualTo(new[] { (0, 3), (2, 2) }));
            Assert.That(response.Status.Chunks[0].Sha256, Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void GetJob_With_Unreadable_Files_Reports_Invalid_State()
    {
        StartUpload();
        File.WriteAllText(temp.JobPath(jobId, "job.json"), "{ not json");

        var response = service.GetJob(id);

        Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
    }

    [Test]
    public void SaveConfig_Creates_The_Job_For_The_Current_User_And_Opens_It_For_Chunks()
    {
        var response = service.SaveConfig(id, Config(fingerprint: "0A1B2C3D"), UserId);

        var job = temp.Store.ReadJob(jobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), response.Message);
            Assert.That(response.Status!.State, Is.EqualTo("Uploading"));
            Assert.That(response.Job!.JobId, Is.EqualTo(id));
            Assert.That(job.CreatedByUserId, Is.EqualTo(UserId));
            Assert.That(job.CreatedUtc, Is.EqualTo(TestData.Now));
            Assert.That(job.File, Is.EqualTo(new ImportFileDescriptor { Name = "contacts.csv", Size = 1234, Fingerprint = "0a1b2c3d" }));
            Assert.That(job.KeyField, Is.EqualTo("ContactEmail"));
            Assert.That(job.Options, Is.EqualTo(new ImportOptions()));
            Assert.That(State(), Is.EqualTo(ImportJobState.Uploading));
        });
    }

    [Test]
    public void SaveConfig_Refuses_Incomplete_Requests()
    {
        var requests = new SaveConfigRequest?[]
        {
            null,
            new(),
            Config() with { File = null },
            Config() with { File = new ImportFileDto { Name = null, Size = 1, Fingerprint = "aa" } },
            Config() with { File = new ImportFileDto { Name = "a.csv", Size = 1, Fingerprint = null } },
            Config() with { KeyField = null },
            Config() with { Mapping = null },
            Config() with { Mapping = [new ImportMappingDto { Source = "Email", Target = null }] },
            Config() with { Mapping = [null!] },
            Config(dateFormat: "yyyy"),
        };

        Assert.Multiple(() =>
        {
            foreach (var request in requests)
            {
                Assert.That(service.SaveConfig(id, request, UserId).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            }

            Assert.That(temp.Store.JobExists(jobId), Is.False);
        });
    }

    [Test]
    public void SaveConfig_Uses_Iso_When_The_Date_Format_Is_Missing()
    {
        var response = service.SaveConfig(id, Config(dateFormat: null), UserId);

        Assert.That(response.Job!.DateFormat, Is.EqualTo("iso"));
    }

    [Test]
    public void SaveConfig_Validates_The_Configuration_And_Creates_Nothing_When_Invalid()
    {
        var unknownTarget = service.SaveConfig(id, Config(mapping: ("Id", "ContactGUID")), UserId);
        var keyNotMapped = service.SaveConfig(id, Config(keyField: "CustomerID"), UserId);
        var tooLarge = service.SaveConfig(id, Config(size: 1_000_001), UserId);
        var badFingerprint = service.SaveConfig(id, Config(fingerprint: "../x"), UserId);

        Assert.Multiple(() =>
        {
            foreach (var response in new[] { unknownTarget, keyNotMapped, tooLarge, badFingerprint })
            {
                Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_CONFIG));
                Assert.That(response.Errors, Is.Not.Empty);
                Assert.That(response.Message, Is.Not.Empty);
            }

            Assert.That(temp.Store.JobExists(jobId), Is.False);
        });
    }

    [Test]
    public void SaveConfig_With_The_Same_Config_Keeps_Uploaded_Chunks_And_The_Creator()
    {
        StartUpload();
        Upload(0, 1, 3);

        var response = service.SaveConfig(id, Config(), userId: 99);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Uploading"));
            Assert.That(response.Status.Chunks.Select(c => c.Index), Is.EqualTo(new[] { 0 }));
            Assert.That(temp.Store.ReadJob(jobId)!.CreatedByUserId, Is.EqualTo(UserId));
        });
    }

    [Test]
    public void SaveConfig_With_A_Changed_Mapping_Deletes_Uploaded_Chunks()
    {
        StartUpload();
        Upload(0, 1, 3);

        var response = service.SaveConfig(id, Config(keyField: "ContactLastName", mapping: ("Surname", "ContactLastName")), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Uploading"));
            Assert.That(response.Status.Chunks, Is.Empty);
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
            Assert.That(temp.Store.ReadJob(jobId)!.Mapping.Single(), Is.EqualTo(new ImportFieldMapping { Source = "Surname", Target = "ContactLastName" }));
        });
    }

    [Test]
    public void SaveConfig_With_Another_File_Deletes_Uploaded_Chunks()
    {
        StartUpload();
        Upload(0, 1, 3);

        var response = service.SaveConfig(id, Config(fingerprint: "ffff", size: 99), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
            Assert.That(temp.Store.ReadJob(jobId)!.File.Fingerprint, Is.EqualTo("ffff"));
        });
    }

    [Test]
    public void SaveConfig_After_The_Upload_Keeps_It_Unless_The_Mapping_Changes()
    {
        StartUpload();
        Upload(0, 1, 3);
        Assert.That(service.CompleteUpload(id, Profile(3, 1)).Code, Is.EqualTo(ImportCommandCodes.OK));

        var same = service.SaveConfig(id, Config(), UserId);
        var stateAfterSame = State();
        var changed = service.SaveConfig(id, Config(dateFormat: "dmy"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(same.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(same.Status!.State, Is.EqualTo("Uploaded"));
            Assert.That(same.Status.Profile.RowsValid, Is.EqualTo(3));
            Assert.That(stateAfterSame, Is.EqualTo(ImportJobState.Uploaded));

            Assert.That(changed.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(changed.Status!.State, Is.EqualTo("Uploading"));
            Assert.That(changed.Status.Profile.RowsValid, Is.Zero);
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [TestCase(ImportJobState.Cancelled)]
    [TestCase(ImportJobState.Completed)]
    [TestCase(ImportJobState.Failed)]
    public void SaveConfig_Of_A_Finished_Job_Is_Refused(ImportJobState state)
    {
        StartUpload();
        temp.Store.UpdateStatus(jobId, s => s with { State = state });

        var same = service.SaveConfig(id, Config(), UserId);
        var changed = service.SaveConfig(id, Config(dateFormat: "dmy"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(same.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(changed.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(State(), Is.EqualTo(state));
            Assert.That(temp.Store.ReadJob(jobId)!.DateFormat, Is.EqualTo("iso"));
        });
    }

    [Test]
    public void SaveConfig_Cannot_Change_The_Mapping_Of_A_Running_Job()
    {
        StartUpload();
        temp.Store.UpdateStatus(jobId, s => s with { State = ImportJobState.Running });

        var response = service.SaveConfig(id, Config(dateFormat: "dmy"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(State(), Is.EqualTo(ImportJobState.Running));
        });
    }

    [Test]
    public void SaveConfig_Replaces_A_Job_Folder_Without_Readable_Files()
    {
        Directory.CreateDirectory(temp.JobPath(jobId));

        var response = service.SaveConfig(id, Config(), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(State(), Is.EqualTo(ImportJobState.Uploading));
        });
    }

    [Test]
    public void UploadChunk_Stores_A_Valid_Chunk_And_Accepts_It_Again()
    {
        StartUpload();

        var first = Upload(0, 1, 3);
        var again = Upload(0, 1, 3);

        Assert.Multiple(() =>
        {
            Assert.That(first.Code, Is.EqualTo(ImportCommandCodes.STORED), first.Message);
            Assert.That(again.Code, Is.EqualTo(ImportCommandCodes.ALREADY_STORED));
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void UploadChunk_For_Another_Job_Than_The_Page_Is_Rejected_And_Touches_Neither_Job()
    {
        StartUpload();
        var otherJobId = Guid.NewGuid();
        Assert.That(service.SaveConfig(otherJobId.ToString("D"), Config(), UserId).Code, Is.EqualTo(ImportCommandCodes.OK));

        var response = service.UploadChunk(id, TestData.Payload(otherJobId, 0, Records(1, 3)));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.REJECTED));
            Assert.That(response.Reason, Is.EqualTo("invalidPayload"));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
            Assert.That(temp.Store.HasChunks(otherJobId), Is.False);
        });
    }

    [Test]
    public void UploadChunk_Refuses_Missing_Payloads()
    {
        StartUpload();

        Assert.Multiple(() =>
        {
            Assert.That(service.UploadChunk(id, null).Code, Is.EqualTo(ImportCommandCodes.REJECTED));
            Assert.That(service.UploadChunk(id, new UploadChunkPayload()).Code, Is.EqualTo(ImportCommandCodes.REJECTED));
            Assert.That(service.UploadChunk(id, new UploadChunkPayload { JobId = id }).Reason, Is.EqualTo("invalidPayload"));
        });
    }

    [Test]
    public void UploadChunk_Reports_The_Reject_Reason_In_Camel_Case()
    {
        StartUpload();
        var tampered = TestData.Payload(jobId, 0, Records(1, 2)) with { Sha256 = new string('a', 64) };

        var response = service.UploadChunk(id, tampered);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.REJECTED));
            Assert.That(response.Reason, Is.EqualTo("hashMismatch"));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [Test]
    public void UploadChunk_Rejection_Names_The_Row_But_No_Contact_Data()
    {
        StartUpload();
        string secret = "secret.person@example.com";
        string invalid = "{\"r\":2,\"k\":\"" + secret + "\",\"v\":{\"ContactEmail\":\"" + secret + "\",\"ContactGUID\":\"x\"}}";

        var response = service.UploadChunk(id, TestData.Payload(jobId, 0, TestData.Record(1, "user1@example.com"), invalid));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.REJECTED));
            Assert.That(response.Reason, Is.EqualTo("invalidRecord"));
            Assert.That(response.RowNumber, Is.EqualTo(2));
            Assert.That(response.Message, Does.Not.Contain("secret"));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [Test]
    public void UploadChunk_Is_Refused_Before_SaveConfig_And_After_The_Upload_Completed()
    {
        var beforeCreate = Upload(0, 1, 3);
        StartUpload();
        Upload(0, 1, 3);
        service.CompleteUpload(id, Profile(3, 1));

        var afterComplete = Upload(1, 4, 3);

        Assert.Multiple(() =>
        {
            Assert.That(beforeCreate.Reason, Is.EqualTo("jobNotFound"));
            Assert.That(afterComplete.Reason, Is.EqualTo("invalidState"));
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void TruncateChunks_Deletes_Chunks_From_The_Index_Up()
    {
        StartUpload();
        Upload(0, 1, 2);
        Upload(1, 3, 2);
        Upload(2, 5, 2);
        Upload(4, 9, 2);

        var response = service.TruncateChunks(id, new TruncateChunksRequest { FromIndex = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Deleted, Is.EqualTo(3));
            Assert.That(response.Chunks.Select(c => c.Index), Is.EqualTo(new[] { 0 }));
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
            Assert.That(File.Exists(temp.JobPath(jobId, "chunks", "1.ndjson.gz")), Is.False);
            Assert.That(State(), Is.EqualTo(ImportJobState.Uploading));
        });
    }

    [Test]
    public void TruncateChunks_Past_The_Last_Chunk_Deletes_Nothing()
    {
        StartUpload();
        Upload(0, 1, 2);

        var response = service.TruncateChunks(id, new TruncateChunksRequest { FromIndex = 5 });

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Deleted, Is.Zero);
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void TruncateChunks_Refuses_Invalid_Requests()
    {
        StartUpload();
        Upload(0, 1, 2);

        Assert.Multiple(() =>
        {
            Assert.That(service.TruncateChunks(id, null).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.TruncateChunks(id, new TruncateChunksRequest()).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.TruncateChunks(id, new TruncateChunksRequest { FromIndex = -1 }).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.TruncateChunks(id, new TruncateChunksRequest { FromIndex = int.MaxValue }).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.TruncateChunks(Guid.NewGuid().ToString("D"), new TruncateChunksRequest { FromIndex = 0 }).Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void TruncateChunks_Is_Only_Possible_While_Uploading()
    {
        StartUpload();
        Upload(0, 1, 2);
        service.CompleteUpload(id, Profile(2, 1));

        var response = service.TruncateChunks(id, new TruncateChunksRequest { FromIndex = 0 });

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(response.Message, Does.Contain("Uploaded"));
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void Resume_With_Other_Chunk_Boundaries_Needs_And_Works_After_Truncating_At_The_First_Difference()
    {
        // First browser: rows 1-2, 3, 4-6. It stopped before completing.
        StartUpload();
        Upload(0, 1, 2);
        Upload(1, 3, 1);
        Upload(2, 4, 3);

        // Second browser: rows 1-2 (same chunk), 3-5, 6. Chunk 1 differs and overlaps the stored chunk 2.
        var same = Upload(0, 1, 2);
        var conflicting = Upload(1, 3, 3);
        var truncated = service.TruncateChunks(id, new TruncateChunksRequest { FromIndex = 1 });
        var second = Upload(1, 3, 3);
        var third = Upload(2, 6, 1);
        var completed = service.CompleteUpload(id, Profile(6, 3));

        Assert.Multiple(() =>
        {
            Assert.That(same.Code, Is.EqualTo(ImportCommandCodes.ALREADY_STORED));
            Assert.That(conflicting.Code, Is.EqualTo(ImportCommandCodes.REJECTED));
            Assert.That(conflicting.Reason, Is.EqualTo("rowOrder"));
            Assert.That(truncated.Deleted, Is.EqualTo(2));
            Assert.That(truncated.Chunks.Select(c => c.Index), Is.EqualTo(new[] { 0 }));
            Assert.That(second.Code, Is.EqualTo(ImportCommandCodes.STORED), second.Message);
            Assert.That(third.Code, Is.EqualTo(ImportCommandCodes.STORED), third.Message);
            Assert.That(completed.Code, Is.EqualTo(ImportCommandCodes.OK), completed.Message);
            Assert.That(completed.Status!.Chunks.Select(c => c.Rows), Is.EqualTo(new[] { 2, 3, 1 }));
        });
    }

    [Test]
    public void CompleteUpload_Stores_Profile_And_Chunks_And_Moves_To_Uploaded()
    {
        StartUpload();
        Upload(0, 1, 3);
        Upload(1, 4, 2);

        var response = service.CompleteUpload(id, new CompleteUploadRequest
        {
            Profile = new ImportProfileDto { RowsRead = 8, RowsValid = 5, RowsRejected = 3, LikelyDuplicates = 1, ChunkCount = 2 },
        });

        var status = temp.Store.ReadStatus(jobId)!;
        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), response.Message);
            Assert.That(response.Status!.State, Is.EqualTo("Uploaded"));
            Assert.That(response.Status.Profile, Is.EqualTo(new ImportProfileDto { RowsRead = 8, RowsValid = 5, RowsRejected = 3, LikelyDuplicates = 1, ChunkCount = 2 }));
            Assert.That(response.Status.Chunks.Select(c => (c.Index, c.Rows)), Is.EqualTo(new[] { (0, 3), (1, 2) }));
            Assert.That(status.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(status.Profile.LikelyDuplicates, Is.EqualTo(1));
        });
    }

    [Test]
    public void CompleteUpload_Reports_Missing_Chunks()
    {
        StartUpload();
        Upload(0, 1, 3);
        Upload(2, 7, 3);

        var response = service.CompleteUpload(id, Profile(9, 3));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.INCONSISTENT));
            Assert.That(response.MissingChunks, Is.EqualTo(new[] { 1 }));
            Assert.That(State(), Is.EqualTo(ImportJobState.Uploading));
        });
    }

    [Test]
    public void CompleteUpload_Deletes_Stale_Chunks_Past_The_End()
    {
        StartUpload();
        Upload(0, 1, 3);
        Upload(1, 4, 3);
        Upload(2, 7, 3);

        var response = service.CompleteUpload(id, Profile(6, 2));

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK), response.Message);
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(response.Status!.Chunks, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void CompleteUpload_Can_Be_Repeated_With_The_Same_Profile()
    {
        StartUpload();
        Upload(0, 1, 3);
        service.CompleteUpload(id, Profile(3, 1));

        var again = service.CompleteUpload(id, Profile(3, 1));
        var different = service.CompleteUpload(id, Profile(4, 1));

        Assert.Multiple(() =>
        {
            Assert.That(again.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(again.Status!.State, Is.EqualTo("Uploaded"));
            Assert.That(different.Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void CompleteUpload_Refuses_Invalid_Profiles_And_Wrong_Totals()
    {
        StartUpload();
        Upload(0, 1, 3);

        Assert.Multiple(() =>
        {
            Assert.That(service.CompleteUpload(id, null).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.CompleteUpload(id, new CompleteUploadRequest()).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.CompleteUpload(id, Profile(3, 0)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.CompleteUpload(id, Profile(3, int.MaxValue)).Code, Is.EqualTo(ImportCommandCodes.INVALID_REQUEST));
            Assert.That(service.CompleteUpload(id, Profile(99, 1)).Code, Is.EqualTo(ImportCommandCodes.INCONSISTENT));
            Assert.That(service.CompleteUpload(id, Profile(-3, 1)).Code, Is.EqualTo(ImportCommandCodes.INCONSISTENT));
            Assert.That(service.CompleteUpload(Guid.NewGuid().ToString("D"), Profile(3, 1)).Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
            Assert.That(State(), Is.EqualTo(ImportJobState.Uploading));
            Assert.That(StoredChunks(), Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void Cancel_Moves_The_Job_To_Cancelled_And_Deletes_Its_Chunks()
    {
        StartUpload();
        Upload(0, 1, 3);

        var response = service.Cancel(id, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Cancelled"));
            Assert.That(State(), Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
            Assert.That(temp.Store.ReadStatus(jobId)!.FinishedUtc, Is.EqualTo(TestData.Now));
        });
    }

    [Test]
    public void Cancel_Of_A_Finished_Or_Unknown_Job_Is_Refused()
    {
        StartUpload();
        service.Cancel(id, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(service.Cancel(id, UserId).Code, Is.EqualTo(ImportCommandCodes.INVALID_STATE));
            Assert.That(service.Cancel(Guid.NewGuid().ToString("D"), UserId).Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
        });
    }

    [Test]
    public void DeleteFiles_Deletes_Chunks_And_Reports_And_Cancels_An_Unfinished_Job()
    {
        StartUpload();
        Upload(0, 1, 3);
        File.WriteAllText(temp.JobPath(jobId, "report-analyze.csv.gz"), "x");

        var response = service.DeleteFiles(id, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Code, Is.EqualTo(ImportCommandCodes.OK));
            Assert.That(response.Status!.State, Is.EqualTo("Cancelled"));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
            Assert.That(temp.Store.ListReports(jobId), Is.Empty);
            Assert.That(temp.Store.JobExists(jobId), Is.True);
        });
    }

    [Test]
    public void DeleteFiles_Keeps_The_State_Of_A_Finished_Job_And_Refuses_Unknown_Jobs()
    {
        StartUpload();
        temp.Store.UpdateStatus(jobId, s => s with { State = ImportJobState.Completed });
        File.WriteAllText(temp.JobPath(jobId, "report-execute.csv.gz"), "x");

        var response = service.DeleteFiles(id, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(response.Status!.State, Is.EqualTo("Completed"));
            Assert.That(temp.Store.ListReports(jobId), Is.Empty);
            Assert.That(service.DeleteFiles(Guid.NewGuid().ToString("D"), UserId).Code, Is.EqualTo(ImportCommandCodes.JOB_NOT_FOUND));
        });
    }

    [Test]
    public void ListImports_Returns_Newest_First_With_User_Names_And_Flags()
    {
        StartUpload();
        Upload(0, 1, 3);
        service.CompleteUpload(id, Profile(3, 1));

        time.Advance(TimeSpan.FromHours(1));
        var second = Guid.NewGuid();
        service.SaveConfig(second.ToString("D"), Config(fileName: "second.csv"), userId: 7);

        time.Advance(TimeSpan.FromHours(1));
        var third = Guid.NewGuid();
        service.SaveConfig(third.ToString("D"), Config(fileName: "third.csv"), UserId);
        service.Cancel(third.ToString("D"), UserId);

        var list = service.ListImports();

        Assert.Multiple(() =>
        {
            Assert.That(list.Truncated, Is.False);
            Assert.That(list.Imports.Select(i => i.FileName), Is.EqualTo(new[] { "third.csv", "second.csv", "contacts.csv" }));
            Assert.That(list.Imports.Select(i => i.State), Is.EqualTo(new[] { "Cancelled", "Uploading", "Uploaded" }));
            Assert.That(list.Imports.Select(i => i.CreatedBy), Is.EqualTo(new[] { "administrator", "", "administrator" }));
            Assert.That(list.Imports.Select(i => i.CanContinue), Is.EqualTo(new[] { false, true, true }));
            Assert.That(list.Imports.Select(i => i.HasFiles), Is.EqualTo(new[] { false, false, true }));
            Assert.That(list.Imports[2].RowsValid, Is.EqualTo(3));
            Assert.That(list.Imports[2].JobId, Is.EqualTo(id));
            Assert.That(list.Imports[2].FileSize, Is.EqualTo(1234));
            Assert.That(users.Requests.Single(), Is.EquivalentTo(new[] { UserId, 7 }));
        });
    }

    [Test]
    public void ListImports_Shows_A_Processing_Job_Without_Heartbeat_As_Paused()
    {
        StartUpload();
        temp.Store.UpdateStatus(jobId, s => s with { State = ImportJobState.Running, HeartbeatUtc = TestData.Now });
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.That(service.ListImports().Imports.Single().State, Is.EqualTo("Paused"));
    }

    [Test]
    public void ListImports_Skips_Jobs_That_Cannot_Be_Read_And_Foreign_Folders()
    {
        StartUpload();
        var broken = Guid.NewGuid();
        service.SaveConfig(broken.ToString("D"), Config(fileName: "broken.csv"), UserId);
        File.WriteAllText(temp.JobPath(broken, "status.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(temp.Root, "not-a-job"));

        var list = service.ListImports();

        Assert.That(list.Imports.Select(i => i.FileName), Is.EqualTo(new[] { "contacts.csv" }));
    }

    [Test]
    public void ListImports_Is_Capped()
    {
        for (int i = 0; i < ImportCommandService.MaxListedImports + 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            service.SaveConfig(Guid.NewGuid().ToString("D"), Config(fileName: $"file{i}.csv"), UserId);
        }

        var list = service.ListImports();

        Assert.Multiple(() =>
        {
            Assert.That(list.Truncated, Is.True);
            Assert.That(list.Imports, Has.Count.EqualTo(ImportCommandService.MaxListedImports));
            Assert.That(list.Imports[0].FileName, Is.EqualTo($"file{ImportCommandService.MaxListedImports + 2}.csv"));
        });
    }

    [Test]
    public void List_And_Load_Run_The_Lazy_Cleanup_Sweep()
    {
        // An unfinished job without activity for longer than the retention is abandoned by the sweep.
        StartUpload();
        Upload(0, 1, 3);
        time.Advance(TimeSpan.FromDays(temp.Options.Value.AbandonedJobRetentionDays + 1));

        var list = service.ListImports();

        Assert.Multiple(() =>
        {
            Assert.That(list.Imports.Single().State, Is.EqualTo("Cancelled"));
            Assert.That(temp.Store.HasChunks(jobId), Is.False);
        });
    }

    [Test]
    public void Messages_Come_From_The_Resource_File()
    {
        var texts = new ResourceImportTexts();

        Assert.Multiple(() =>
        {
            Assert.That(service.GetJob("x").Message, Is.EqualTo("The import ID is not valid."));
            Assert.That(texts.Get(ImportTextKeys.INVALID_STATE, "Uploaded"), Is.EqualTo("This action is not possible while the import is Uploaded."));
            foreach (string key in new[]
            {
                ImportTextKeys.INVALID_JOB_ID, ImportTextKeys.JOB_NOT_FOUND, ImportTextKeys.JOB_UNREADABLE, ImportTextKeys.INVALID_REQUEST,
                ImportTextKeys.INVALID_STATE, ImportTextKeys.INVALID_CONFIG, ImportTextKeys.JOB_ID_MISMATCH, ImportTextKeys.TRUNCATE_INDEX,
            })
            {
                Assert.That(texts.Get(key), Is.Not.EqualTo(key).And.Not.Empty, key);
            }
        });
    }

    private sealed class FakeUserNameProvider : IImportUserNameProvider
    {
        public Dictionary<int, string> Names { get; } = [];

        public List<IReadOnlyCollection<int>> Requests { get; } = [];

        public IReadOnlyDictionary<int, string> GetUserNames(IReadOnlyCollection<int> userIds)
        {
            Requests.Add(userIds);

            return Names.Where(n => userIds.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value);
        }
    }
}
