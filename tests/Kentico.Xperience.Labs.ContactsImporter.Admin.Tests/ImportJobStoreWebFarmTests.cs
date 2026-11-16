namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// Status updates of two application instances over one shared folder: two stores (each with its own in-process
/// lock, like two web farm instances), with the write of one placed at an exact point of the other's update.
/// </summary>
internal class ImportJobStoreWebFarmTests
{
    private const string StatusFile = ImportJobStore.StatusFileName;
    private const string FinalFile = ImportJobStore.FinalStatusFileName;

    private TempStore temp = null!;
    private HookedImportFileSystem fileSystemA = null!;
    private ImportJobStore instanceA = null!;
    private ImportJobStore instanceB = null!;
    private Guid jobId;

    [SetUp]
    public void SetUp()
    {
        temp = new TempStore();
        fileSystemA = new HookedImportFileSystem(new PhysicalImportFileSystem());
        instanceA = temp.NewStore(fileSystemA);
        instanceB = temp.NewStore();

        var job = TestData.Job();
        jobId = job.JobId;
        instanceA.CreateJob(job);
        instanceA.WriteStatus(jobId, new ImportJobStatus { State = ImportJobState.Running, DriverToken = "driver-a", HeartbeatUtc = TestData.Now, UpdatedUtc = TestData.Now });
        fileSystemA.Replaced.Clear();
    }

    [TearDown]
    public void TearDown() => temp.Dispose();

    /// <summary>A driver's checkpoint: only while the job is still running for this driver.</summary>
    private static ImportJobStatus Checkpoint(ImportJobStatus current) =>
        current.State == ImportJobState.Running && current.DriverToken == "driver-a"
            ? current with { Checkpoint = current.Checkpoint with { RowOffset = current.Checkpoint.RowOffset + 1 } }
            : current;

    private static ImportJobStatus Cancel(ImportJobStatus current) =>
        ImportJobStateMachine.IsTerminal(current.State) ? current : ImportJobStateMachine.Transition(current, ImportJobState.Cancelled, TestData.Now);

    private static ImportJobStatus TakeOver(ImportJobStatus current) => current with { DriverToken = "driver-b" };

    private static string Json(ImportJobStatus status) => System.Text.Encoding.UTF8.GetString(ImportJson.Serialize(status));

    private ImportJobStatus StatusFileContent() => ImportJson.Deserialize<ImportJobStatus>(File.ReadAllBytes(temp.JobPath(jobId, StatusFile)));

    [Test]
    public void Every_Update_Increases_The_Version_And_An_Unchanged_Status_Is_Not_Written()
    {
        var first = instanceA.UpdateStatus(jobId, Checkpoint)!;
        var second = instanceB.UpdateStatus(jobId, Checkpoint)!;
        int writes = fileSystemA.Replaced.GetValueOrDefault(StatusFile);
        var unchanged = instanceA.UpdateStatus(jobId, current => current)!;
        var refused = instanceA.UpdateStatus(jobId, current => current with { DriverToken = "driver-a" })!;

        Assert.Multiple(() =>
        {
            Assert.That(first.Version, Is.EqualTo(1));
            Assert.That(second.Version, Is.EqualTo(2));
            Assert.That(second.Checkpoint.RowOffset, Is.EqualTo(2));
            Assert.That(Json(unchanged), Is.EqualTo(Json(second)));
            Assert.That(refused.Version, Is.EqualTo(2));
            Assert.That(fileSystemA.Replaced.GetValueOrDefault(StatusFile), Is.EqualTo(writes), "No write when the update changes nothing.");
            Assert.That(File.Exists(temp.JobPath(jobId, FinalFile)), Is.False, "Only a finished job has the final status file.");
        });
    }

    [Test]
    public void Cancel_On_Another_Instance_After_The_Read_Is_Seen_Before_The_Write()
    {
        int decisions = 0;
        fileSystemA.AfterRead = HookedImportFileSystem.On(StatusFile, () => instanceB.UpdateStatus(jobId, Cancel));

        var result = instanceA.UpdateStatus(jobId, current =>
        {
            decisions++;

            return Checkpoint(current);
        })!;

        Assert.Multiple(() =>
        {
            Assert.That(decisions, Is.EqualTo(1), "The read already prefers the final status the other instance wrote.");
            Assert.That(result.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(result.Checkpoint.RowOffset, Is.Zero);
            Assert.That(StatusFileContent().State, Is.EqualTo(ImportJobState.Cancelled));
        });
    }

    [Test]
    public void Take_Over_On_Another_Instance_After_The_Read_Refuses_The_Old_Drivers_Checkpoint()
    {
        int decisions = 0;
        fileSystemA.AfterRead = HookedImportFileSystem.On(StatusFile, () => instanceB.UpdateStatus(jobId, TakeOver));

        var result = instanceA.UpdateStatus(jobId, current =>
        {
            decisions++;

            return Checkpoint(current);
        })!;

        Assert.Multiple(() =>
        {
            Assert.That(decisions, Is.EqualTo(2), "The checkpoint was decided again on the status the other instance wrote.");
            Assert.That(fileSystemA.Replaced.GetValueOrDefault(StatusFile), Is.Zero, "The old driver wrote nothing.");
            Assert.That(result.DriverToken, Is.EqualTo("driver-b"));
            Assert.That(result.Checkpoint.RowOffset, Is.Zero);
            Assert.That(Json(StatusFileContent()), Is.EqualTo(Json(result)));
        });
    }

    [Test]
    public void Cancel_On_Another_Instance_Between_The_Last_Check_And_The_Write_Still_Wins()
    {
        // The window no check can close without compare-and-swap: instance B cancels (and verifies its own write)
        // after instance A looked at the file for the last time and before A replaces it.
        ImportJobStatus? cancelled = null;
        fileSystemA.BeforeReplace = HookedImportFileSystem.On(StatusFile, () => cancelled = instanceB.UpdateStatus(jobId, Cancel));

        var result = instanceA.UpdateStatus(jobId, Checkpoint)!;

        Assert.Multiple(() =>
        {
            Assert.That(cancelled!.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(fileSystemA.Replaced[StatusFile], Is.EqualTo(2), "A's stale checkpoint did replace status.json, then A put the cancelled status back.");
            Assert.That(result.State, Is.EqualTo(ImportJobState.Cancelled), "A learns in the same call that the job is cancelled.");
            Assert.That(result.Checkpoint.RowOffset, Is.Zero);
            Assert.That(StatusFileContent().State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(instanceA.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(instanceB.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Cancelled));
        });
    }

    [Test]
    public void Finished_Job_Stays_Finished_When_An_Old_Status_Replaces_The_Status_File_Later()
    {
        // The worst case: a write computed from an old status arrives after every check of everybody, for example
        // from an instance that hung between its last check and its write.
        byte[] staleRunning = File.ReadAllBytes(temp.JobPath(jobId, StatusFile));
        instanceB.UpdateStatus(jobId, Cancel);
        File.WriteAllBytes(temp.JobPath(jobId, StatusFile), staleRunning);

        var read = instanceA.ReadStatus(jobId)!;
        var checkpoint = instanceA.UpdateStatus(jobId, Checkpoint)!;

        Assert.Multiple(() =>
        {
            Assert.That(read.State, Is.EqualTo(ImportJobState.Cancelled), "Reads prefer the final status file.");
            Assert.That(checkpoint.State, Is.EqualTo(ImportJobState.Cancelled), "The driver's checkpoint is refused.");
            Assert.That(StatusFileContent().State, Is.EqualTo(ImportJobState.Cancelled), "The next status update repairs status.json.");
        });
    }

    [Test]
    public void Write_Of_Another_Instance_On_Top_Of_This_One_Does_Not_Apply_The_Update_Twice()
    {
        // B reads A's new status and writes its own change before A checks its write: A's change is part of B's.
        int decisions = 0;
        fileSystemA.AfterReplace = HookedImportFileSystem.On(StatusFile, () => instanceB.UpdateStatus(jobId, current => current with { LastInstance = "instance-b" }));

        var result = instanceA.UpdateStatus(jobId, current =>
        {
            decisions++;

            return Checkpoint(current);
        })!;

        var stored = StatusFileContent();
        Assert.Multiple(() =>
        {
            Assert.That(decisions, Is.EqualTo(1));
            Assert.That(result.Checkpoint.RowOffset, Is.EqualTo(1));
            Assert.That(stored.Checkpoint.RowOffset, Is.EqualTo(1));
            Assert.That(stored.LastInstance, Is.EqualTo("instance-b"));
            Assert.That(stored.Version, Is.EqualTo(2));
        });
    }

    [Test]
    public void Write_Of_Another_Instance_From_The_Same_Old_Status_Is_Detected_And_The_Update_Applied_On_Top()
    {
        // B decided from the same status as A (same new version) and its write lands right after A's: A's change is
        // gone from the file. A notices when it checks its write and makes the change again on B's status.
        var sibling = instanceA.ReadStatus(jobId)! with { LastInstance = "instance-b", Version = 1 };
        fileSystemA.AfterReplace = HookedImportFileSystem.On(StatusFile, () => File.WriteAllBytes(temp.JobPath(jobId, StatusFile), ImportJson.Serialize(sibling)));

        var result = instanceA.UpdateStatus(jobId, Checkpoint)!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Checkpoint.RowOffset, Is.EqualTo(1));
            Assert.That(result.LastInstance, Is.EqualTo("instance-b"));
            Assert.That(result.Version, Is.EqualTo(2));
            Assert.That(Json(StatusFileContent()), Is.EqualTo(Json(result)));
        });
    }

    [Test]
    public void Known_Window_A_Change_That_Does_Not_Finish_The_Job_Can_Be_Overwritten_Between_The_Last_Check_And_The_Write()
    {
        // Documents what is NOT guaranteed (IImportJobStore.UpdateStatus): B's take over happens, completely, between
        // A's last look at the file and A's write. A's checkpoint replaces it and neither instance is told.
        // The processor stays correct: B's next status check finds A's token and B stops (see ImportProcessorWebFarmTests).
        ImportJobStatus? takenOver = null;
        fileSystemA.BeforeReplace = HookedImportFileSystem.On(StatusFile, () => takenOver = instanceB.UpdateStatus(jobId, TakeOver));

        var result = instanceA.UpdateStatus(jobId, Checkpoint)!;

        var stored = StatusFileContent();
        Assert.Multiple(() =>
        {
            Assert.That(takenOver!.DriverToken, Is.EqualTo("driver-b"), "B believes it drives the job.");
            Assert.That(result.DriverToken, Is.EqualTo("driver-a"));
            Assert.That(stored.DriverToken, Is.EqualTo("driver-a"), "B's take over is lost.");
            Assert.That(stored.Checkpoint.RowOffset, Is.EqualTo(1));
            Assert.That(instanceB.UpdateStatus(jobId, current => current.DriverToken == "driver-b" ? current with { LastInstance = "b" } : current)!.LastInstance, Is.Null, "B's next write checks the driver and is refused.");
        });
    }

    [Test]
    public void Finishing_A_Job_Writes_The_Final_Status_Before_The_Status_File()
    {
        bool finalExistedBeforeStatusWrite = false;
        fileSystemA.BeforeReplace = HookedImportFileSystem.On(StatusFile, () => finalExistedBeforeStatusWrite = File.Exists(temp.JobPath(jobId, FinalFile)));

        var cancelled = instanceA.UpdateStatus(jobId, Cancel)!;

        Assert.Multiple(() =>
        {
            Assert.That(finalExistedBeforeStatusWrite, Is.True);
            Assert.That(File.ReadAllText(temp.JobPath(jobId, FinalFile)), Is.EqualTo(Json(cancelled)));
            Assert.That(Json(StatusFileContent()), Is.EqualTo(Json(cancelled)));
        });
    }

    [Test]
    public void Finishing_A_Job_Is_Not_Decided_Twice_When_Another_Instance_Replaces_The_Status_File()
    {
        // A cancels; B's stale checkpoint replaces status.json right after. A must still report "cancelled by this call".
        byte[] staleRunning = File.ReadAllBytes(temp.JobPath(jobId, StatusFile));
        fileSystemA.AfterReplace = HookedImportFileSystem.On(StatusFile, () => File.WriteAllBytes(temp.JobPath(jobId, StatusFile), staleRunning));
        int decisions = 0;

        var cancelled = instanceA.UpdateStatus(jobId, current =>
        {
            decisions++;

            return Cancel(current);
        })!;

        Assert.Multiple(() =>
        {
            Assert.That(decisions, Is.EqualTo(1));
            Assert.That(cancelled.State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(StatusFileContent().State, Is.EqualTo(ImportJobState.Cancelled));
            Assert.That(instanceB.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Cancelled));
        });
    }

    [Test]
    public void Only_An_Explicit_Change_Leaves_The_Finished_State()
    {
        instanceA.UpdateStatus(jobId, Cancel);

        // Tests and repairs can still move a finished job on purpose: by an update that decides it from the finished
        // status, or by replacing the status unconditionally.
        var reopened = instanceA.UpdateStatus(jobId, current => current with { State = ImportJobState.Uploaded })!;
        bool finalFileAfterUpdate = File.Exists(temp.JobPath(jobId, FinalFile));
        instanceA.UpdateStatus(jobId, Cancel);
        instanceA.WriteStatus(jobId, new ImportJobStatus { State = ImportJobState.Configuring });

        Assert.Multiple(() =>
        {
            Assert.That(reopened.State, Is.EqualTo(ImportJobState.Uploaded));
            Assert.That(finalFileAfterUpdate, Is.False);
            Assert.That(File.Exists(temp.JobPath(jobId, FinalFile)), Is.False);
            Assert.That(instanceB.ReadStatus(jobId)!.State, Is.EqualTo(ImportJobState.Configuring));
        });
    }

    [Test]
    public void Final_Status_File_Is_Not_A_Report_Or_A_Chunk_And_Goes_With_The_Job()
    {
        instanceA.UpdateStatus(jobId, Cancel);

        Assert.Multiple(() =>
        {
            Assert.That(instanceA.ListReports(jobId), Is.Empty);
            Assert.That(instanceA.HasChunks(jobId), Is.False);
        });

        instanceA.DeleteJob(jobId);

        Assert.That(Directory.Exists(temp.JobPath(jobId)), Is.False);
    }

    [Test]
    public void Chunk_Size_Is_The_Sum_Of_The_Stored_Chunk_Data()
    {
        static ImportChunkInfo Chunk(int index) => new() { Index = index, Sha256 = new string('a', 64), Rows = 1, FirstRow = index + 1, LastRow = index + 1 };

        long empty = instanceA.GetChunksSize(jobId);
        instanceA.WriteChunk(jobId, Chunk(0), new byte[10]);
        instanceA.WriteChunk(jobId, Chunk(1), new byte[25]);
        instanceA.WriteChunk(jobId, Chunk(1), new byte[30]);

        Assert.Multiple(() =>
        {
            Assert.That(empty, Is.Zero);
            Assert.That(instanceB.GetChunksSize(jobId), Is.EqualTo(40), "Metadata files do not count; a replaced chunk counts once.");
            Assert.That(instanceB.GetChunksSize(jobId, exceptIndex: 1), Is.EqualTo(10));
            Assert.That(instanceB.GetChunksSize(Guid.NewGuid()), Is.Zero);
        });
    }
}
