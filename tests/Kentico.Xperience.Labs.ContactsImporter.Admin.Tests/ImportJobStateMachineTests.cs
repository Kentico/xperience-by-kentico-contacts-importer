namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

internal class ImportJobStateMachineTests
{
    private static readonly DateTime now = TestData.Now;
    private static readonly TimeSpan stale = TimeSpan.FromSeconds(60);

    private static ImportJobStatus Status(ImportJobState state) => new() { State = state, UpdatedUtc = now.AddHours(-1) };

    private static IEnumerable<TestCaseData> TransitionMatrix()
    {
        var allowed = new Dictionary<ImportJobState, ImportJobState[]>
        {
            [ImportJobState.Configuring] = [ImportJobState.Uploading, ImportJobState.Failed, ImportJobState.Cancelled],
            [ImportJobState.Uploading] = [ImportJobState.Uploaded, ImportJobState.Configuring, ImportJobState.Failed, ImportJobState.Cancelled],
            [ImportJobState.Uploaded] = [ImportJobState.Analyzing, ImportJobState.Configuring, ImportJobState.Failed, ImportJobState.Cancelled],
            [ImportJobState.Analyzing] = [ImportJobState.Analyzed, ImportJobState.Uploaded, ImportJobState.Configuring, ImportJobState.Failed, ImportJobState.Cancelled],
            [ImportJobState.Analyzed] = [ImportJobState.Confirmed, ImportJobState.Uploaded, ImportJobState.Configuring, ImportJobState.Failed, ImportJobState.Cancelled],
            [ImportJobState.Confirmed] = [ImportJobState.Running, ImportJobState.Uploaded, ImportJobState.Configuring, ImportJobState.Failed, ImportJobState.Cancelled],
            [ImportJobState.Running] = [ImportJobState.Completed, ImportJobState.Failed, ImportJobState.Cancelled],
            [ImportJobState.Paused] = [],
            [ImportJobState.Completed] = [],
            [ImportJobState.Failed] = [],
            [ImportJobState.Cancelled] = [],
        };

        foreach (var from in Enum.GetValues<ImportJobState>())
        {
            foreach (var to in Enum.GetValues<ImportJobState>())
            {
                yield return new TestCaseData(from, to, allowed[from].Contains(to)).SetName($"Transition_{from}_To_{to}");
            }
        }
    }

    [TestCaseSource(nameof(TransitionMatrix))]
    public void CanTransition_Follows_The_Lifecycle(ImportJobState from, ImportJobState to, bool expected) => Assert.Multiple(() =>
                                                                                                                   {
                                                                                                                       Assert.That(ImportJobStateMachine.CanTransition(from, to), Is.EqualTo(expected));
                                                                                                                       Assert.That(ImportJobStateMachine.TryTransition(Status(from), to, now, out var updated), Is.EqualTo(expected));
                                                                                                                       Assert.That(updated.State, Is.EqualTo(expected ? to : from));
                                                                                                                   });

    [Test]
    public void Transition_Throws_When_Not_Allowed() =>
        Assert.That(
            () => ImportJobStateMachine.Transition(Status(ImportJobState.Completed), ImportJobState.Running, now),
            Throws.InvalidOperationException.With.Message.Contains("Completed").And.Message.Contains("Running"));

    [TestCase(ImportJobState.Completed, true)]
    [TestCase(ImportJobState.Failed, true)]
    [TestCase(ImportJobState.Cancelled, true)]
    [TestCase(ImportJobState.Running, false)]
    [TestCase(ImportJobState.Paused, false)]
    [TestCase(ImportJobState.Configuring, false)]
    public void IsTerminal(ImportJobState state, bool expected) =>
        Assert.That(ImportJobStateMachine.IsTerminal(state), Is.EqualTo(expected));

    [Test]
    public void Transition_Sets_Updated_Time()
    {
        var updated = ImportJobStateMachine.Transition(Status(ImportJobState.Configuring), ImportJobState.Uploading, now);

        Assert.That(updated.UpdatedUtc, Is.EqualTo(now));
    }

    [Test]
    public void Finishing_Sets_Finished_Time_And_Releases_Driver()
    {
        var running = Status(ImportJobState.Running) with { DriverToken = "token", HeartbeatUtc = now };

        var completed = ImportJobStateMachine.Transition(running, ImportJobState.Completed, now);
        var failed = ImportJobStateMachine.Transition(running, ImportJobState.Failed, now, ImportMessages.ChunkMissing(3));
        var failedWithoutReason = ImportJobStateMachine.Transition(running, ImportJobState.Failed, now);
        var cancelled = ImportJobStateMachine.Transition(running, ImportJobState.Cancelled, now);

        Assert.Multiple(() =>
        {
            Assert.That(completed.FinishedUtc, Is.EqualTo(now));
            Assert.That(completed.DriverToken, Is.Null);
            Assert.That(completed.Error, Is.Null);
            Assert.That(failed.FinishedUtc, Is.EqualTo(now));
            Assert.That(failed.Error, Is.EqualTo(ImportTextKeys.CHUNK_MISSING));
            Assert.That(failed.ErrorArguments, Is.EqualTo(new[] { "3" }));
            Assert.That(failed.ErrorMessage, Is.EqualTo(ImportMessages.ChunkMissing(3)));
            Assert.That(failedWithoutReason.ErrorMessage, Is.EqualTo(ImportMessages.Failed));
            Assert.That(cancelled.FinishedUtc, Is.EqualTo(now));
            Assert.That(cancelled.Error, Is.Null);
        });
    }

    [Test]
    public void Starting_Analysis_And_Run_Resets_Checkpoint_To_The_Phase()
    {
        var uploaded = Status(ImportJobState.Uploaded) with
        {
            Checkpoint = new ImportCheckpoint { Phase = ImportPhase.Execute, ChunkIndex = 4, RowOffset = 99 },
            Aggregates = new Dictionary<string, long> { ["created"] = 5 },
        };

        var analyzing = ImportJobStateMachine.Transition(uploaded, ImportJobState.Analyzing, now);
        var analyzed = ImportJobStateMachine.Transition(analyzing with { Aggregates = new Dictionary<string, long> { ["new"] = 7 } }, ImportJobState.Analyzed, now);
        var confirmed = ImportJobStateMachine.Transition(analyzed, ImportJobState.Confirmed, now);
        var running = ImportJobStateMachine.Transition(confirmed, ImportJobState.Running, now);

        Assert.Multiple(() =>
        {
            Assert.That(analyzing.Checkpoint, Is.EqualTo(new ImportCheckpoint { Phase = ImportPhase.Analyze }));
            Assert.That(analyzing.Aggregates, Is.Empty);
            Assert.That(analyzed.Aggregates, Has.Count.EqualTo(1), "analysis results are kept for review");
            Assert.That(confirmed.Aggregates, Has.Count.EqualTo(1));
            Assert.That(running.Checkpoint, Is.EqualTo(new ImportCheckpoint { Phase = ImportPhase.Execute }));
            Assert.That(running.Aggregates, Is.Empty);
        });
    }

    [Test]
    public void GetEffectiveState_Derives_Paused_From_Stale_Heartbeat_Only_While_Driven()
    {
        var fresh = now.AddSeconds(-60);
        var old = now.AddSeconds(-61);

        Assert.Multiple(() =>
        {
            Assert.That(ImportJobStateMachine.GetEffectiveState(Status(ImportJobState.Running) with { HeartbeatUtc = fresh }, now, stale), Is.EqualTo(ImportJobState.Running));
            Assert.That(ImportJobStateMachine.GetEffectiveState(Status(ImportJobState.Running) with { HeartbeatUtc = old }, now, stale), Is.EqualTo(ImportJobState.Paused));
            Assert.That(ImportJobStateMachine.GetEffectiveState(Status(ImportJobState.Running), now, stale), Is.EqualTo(ImportJobState.Paused), "no heartbeat yet");
            Assert.That(ImportJobStateMachine.GetEffectiveState(Status(ImportJobState.Analyzing) with { HeartbeatUtc = old }, now, stale), Is.EqualTo(ImportJobState.Paused));
            Assert.That(ImportJobStateMachine.GetEffectiveState(Status(ImportJobState.Analyzed) with { HeartbeatUtc = old }, now, stale), Is.EqualTo(ImportJobState.Analyzed));
            Assert.That(ImportJobStateMachine.GetEffectiveState(Status(ImportJobState.Uploading), now, stale), Is.EqualTo(ImportJobState.Uploading));
            Assert.That(ImportJobStateMachine.GetEffectiveState(Status(ImportJobState.Completed) with { HeartbeatUtc = old }, now, stale), Is.EqualTo(ImportJobState.Completed));
        });
    }

    [Test]
    public void ClassifyConfigChange_Distinguishes_Mapping_From_Options()
    {
        var job = TestData.Job();
        var sameContent = job with { Mapping = [.. job.Mapping.Select(m => m with { })], Options = job.Options with { } };

        Assert.Multiple(() =>
        {
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, sameContent), Is.EqualTo(ImportConfigChange.None));
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, job with { Options = job.Options with { EmptyValues = EmptyValuesBehavior.Clear } }), Is.EqualTo(ImportConfigChange.Options));
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, job with { Options = job.Options with { RecipientListId = 5 } }), Is.EqualTo(ImportConfigChange.Options));
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, job with { KeyField = "ContactFirstName" }), Is.EqualTo(ImportConfigChange.Mapping));
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, job with { DateFormat = "dd/MM/yyyy" }), Is.EqualTo(ImportConfigChange.Mapping));
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, job with { File = job.File with { Fingerprint = "ff" } }), Is.EqualTo(ImportConfigChange.Mapping));
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, job with { Mapping = [.. job.Mapping.Reverse()] }), Is.EqualTo(ImportConfigChange.Mapping));
            Assert.That(ImportJobStateMachine.ClassifyConfigChange(job, job with { Mapping = [.. job.Mapping.Take(1)] }), Is.EqualTo(ImportConfigChange.Mapping));
            Assert.That(
                ImportJobStateMachine.ClassifyConfigChange(job, job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent" } }),
                Is.EqualTo(ImportConfigChange.Mapping),
                "consent columns are applied in the browser, like the field mapping");
            Assert.That(
                ImportJobStateMachine.ClassifyConfigChange(
                    job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent" } },
                    job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent", Date = "date" } }),
                Is.EqualTo(ImportConfigChange.Mapping));
            Assert.That(
                ImportJobStateMachine.ClassifyConfigChange(
                    job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent" } },
                    job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent" } }),
                Is.EqualTo(ImportConfigChange.None));
            Assert.That(
                ImportJobStateMachine.ClassifyConfigChange(job, job with { KeyField = "ContactFirstName", Options = job.Options with { NewContacts = NewContactsBehavior.Skip } }),
                Is.EqualTo(ImportConfigChange.Mapping),
                "a mapping change wins over an options change");
        });
    }

    [TestCase(ImportJobState.Configuring, ImportJobState.Configuring, false)]
    [TestCase(ImportJobState.Uploading, ImportJobState.Configuring, true)]
    [TestCase(ImportJobState.Uploaded, ImportJobState.Configuring, true)]
    [TestCase(ImportJobState.Analyzing, ImportJobState.Configuring, true)]
    [TestCase(ImportJobState.Analyzed, ImportJobState.Configuring, true)]
    [TestCase(ImportJobState.Confirmed, ImportJobState.Configuring, true)]
    public void Mapping_Change_Requires_New_Upload(ImportJobState state, ImportJobState expectedState, bool expectedDeleteChunks)
    {
        var status = Status(state) with
        {
            Chunks = [new ImportChunkInfo { Index = 0, Sha256 = "aa", Rows = 1 }],
            Profile = new ImportProfile { RowsRead = 10, ChunkCount = 1 },
            DriverToken = "token",
            HeartbeatUtc = now,
        };

        var result = ImportJobStateMachine.ApplyConfigChange(status, ImportConfigChange.Mapping, now);

        Assert.Multiple(() =>
        {
            Assert.That(result.Allowed, Is.True);
            Assert.That(result.Status.State, Is.EqualTo(expectedState));
            Assert.That(result.DeleteChunks, Is.EqualTo(expectedDeleteChunks));
            Assert.That(result.Status.UpdatedUtc, Is.EqualTo(now));
            if (expectedDeleteChunks)
            {
                Assert.That(result.Status.Chunks, Is.Empty);
                Assert.That(result.Status.Profile, Is.EqualTo(new ImportProfile()));
                Assert.That(result.Status.DriverToken, Is.Null);
                Assert.That(result.Status.HeartbeatUtc, Is.Null);
            }
        });
    }

    [TestCase(ImportJobState.Configuring, ImportJobState.Configuring)]
    [TestCase(ImportJobState.Uploading, ImportJobState.Uploading)]
    [TestCase(ImportJobState.Uploaded, ImportJobState.Uploaded)]
    [TestCase(ImportJobState.Analyzing, ImportJobState.Uploaded)]
    [TestCase(ImportJobState.Analyzed, ImportJobState.Uploaded)]
    [TestCase(ImportJobState.Confirmed, ImportJobState.Uploaded)]
    public void Options_Change_After_Analysis_Returns_To_Uploaded_And_Keeps_Upload(ImportJobState state, ImportJobState expectedState)
    {
        var chunks = new[] { new ImportChunkInfo { Index = 0, Sha256 = "aa", Rows = 1 } };
        var status = Status(state) with
        {
            Chunks = chunks,
            Profile = new ImportProfile { RowsRead = 10, ChunkCount = 1 },
            Checkpoint = new ImportCheckpoint { ChunkIndex = 3 },
            Aggregates = new Dictionary<string, long> { ["new"] = 1 },
        };

        var result = ImportJobStateMachine.ApplyConfigChange(status, ImportConfigChange.Options, now);

        Assert.Multiple(() =>
        {
            Assert.That(result.Allowed, Is.True);
            Assert.That(result.DeleteChunks, Is.False);
            Assert.That(result.Status.State, Is.EqualTo(expectedState));
            Assert.That(result.Status.Chunks, Is.EqualTo(chunks));
            Assert.That(result.Status.Profile.RowsRead, Is.EqualTo(10));
            if (expectedState != state)
            {
                Assert.That(result.Status.Checkpoint, Is.EqualTo(new ImportCheckpoint()));
                Assert.That(result.Status.Aggregates, Is.Empty);
            }
        });
    }

    [TestCase(ImportJobState.Running)]
    [TestCase(ImportJobState.Completed)]
    [TestCase(ImportJobState.Failed)]
    [TestCase(ImportJobState.Cancelled)]
    public void Config_Change_Is_Refused_Once_Running_Or_Finished(ImportJobState state)
    {
        var status = Status(state);

        var mapping = ImportJobStateMachine.ApplyConfigChange(status, ImportConfigChange.Mapping, now);
        var options = ImportJobStateMachine.ApplyConfigChange(status, ImportConfigChange.Options, now);
        var none = ImportJobStateMachine.ApplyConfigChange(status, ImportConfigChange.None, now);

        Assert.Multiple(() =>
        {
            Assert.That(mapping.Allowed, Is.False);
            Assert.That(mapping.Status, Is.SameAs(status));
            Assert.That(options.Allowed, Is.False);
            Assert.That(none.Allowed, Is.True, "saving an unchanged configuration is harmless");
            Assert.That(none.Status, Is.SameAs(status));
        });
    }

    [Test]
    public void First_Driver_Gets_A_Token_And_Heartbeat()
    {
        var result = ImportJobStateMachine.AcquireDriver(Status(ImportJobState.Uploaded), null, false, "instance-1", now, stale);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ImportDriverOutcome.Acquired));
            Assert.That(result.DriverToken, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Status.DriverToken, Is.EqualTo(result.DriverToken));
            Assert.That(result.Status.HeartbeatUtc, Is.EqualTo(now));
            Assert.That(result.Status.LastInstance, Is.EqualTo("instance-1"));
        });
    }

    [Test]
    public void New_Driver_Keeps_The_Token_The_Browser_Made()
    {
        string proposed = Guid.NewGuid().ToString("N");
        var abandoned = Status(ImportJobState.Running) with { DriverToken = Guid.NewGuid().ToString("N"), HeartbeatUtc = now.AddMinutes(-5) };

        var first = ImportJobStateMachine.AcquireDriver(Status(ImportJobState.Uploaded), proposed, false, null, now, stale);
        var takeOver = ImportJobStateMachine.AcquireDriver(abandoned, proposed, true, null, now, stale);

        Assert.Multiple(() =>
        {
            Assert.That(first.DriverToken, Is.EqualTo(proposed), "a tab reloaded during its first call still has the token");
            Assert.That(first.Status.DriverToken, Is.EqualTo(proposed));
            Assert.That(takeOver.Outcome, Is.EqualTo(ImportDriverOutcome.Acquired));
            Assert.That(takeOver.DriverToken, Is.EqualTo(proposed));
        });
    }

    [TestCase("not-a-guid")]
    [TestCase("{0f8fad5b-d9cb-469f-a165-70867728950e}")]
    [TestCase("0f8fad5bd9cb469fa16570867728950e00")]
    public void New_Driver_Gets_A_Server_Token_For_A_Malformed_Proposal(string proposed)
    {
        var result = ImportJobStateMachine.AcquireDriver(Status(ImportJobState.Uploaded), proposed, false, null, now, stale);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ImportDriverOutcome.Acquired));
            Assert.That(result.DriverToken, Is.Not.EqualTo(proposed));
            Assert.That(Guid.TryParseExact(result.DriverToken, "N", out _), Is.True);
        });
    }

    [Test]
    public void Current_Driver_Keeps_Driving_Even_After_A_Long_Break()
    {
        var status = Status(ImportJobState.Running) with { DriverToken = "mine", HeartbeatUtc = now.AddHours(-5), LastInstance = "instance-1" };

        var result = ImportJobStateMachine.AcquireDriver(status, "mine", false, "instance-2", now, stale);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ImportDriverOutcome.Acquired));
            Assert.That(result.DriverToken, Is.EqualTo("mine"));
            Assert.That(result.Status.HeartbeatUtc, Is.EqualTo(now));
            Assert.That(result.Status.LastInstance, Is.EqualTo("instance-2"));
        });
    }

    [TestCase(null)]
    [TestCase("other")]
    public void Second_Driver_Is_Refused_While_Heartbeat_Is_Fresh(string? token)
    {
        var status = Status(ImportJobState.Running) with { DriverToken = "mine", HeartbeatUtc = now.AddSeconds(-10) };

        var polite = ImportJobStateMachine.AcquireDriver(status, token, false, null, now, stale);
        var forceful = ImportJobStateMachine.AcquireDriver(status, token, true, null, now, stale);

        Assert.Multiple(() =>
        {
            Assert.That(polite.Outcome, Is.EqualTo(ImportDriverOutcome.ActiveElsewhere));
            Assert.That(forceful.Outcome, Is.EqualTo(ImportDriverOutcome.ActiveElsewhere), "take over is only possible when the heartbeat is stale");
            Assert.That(forceful.DriverToken, Is.Null);
            Assert.That(forceful.Status, Is.SameAs(status));
        });
    }

    [Test]
    public void Stale_Job_Can_Be_Taken_Over_Only_Explicitly()
    {
        var status = Status(ImportJobState.Running) with { DriverToken = "previous", HeartbeatUtc = now.AddMinutes(-5) };

        var withoutTakeOver = ImportJobStateMachine.AcquireDriver(status, null, false, null, now, stale);
        var takeOver = ImportJobStateMachine.AcquireDriver(status, null, true, "instance-2", now, stale);
        var previousDriverReturns = ImportJobStateMachine.AcquireDriver(takeOver.Status, "previous", false, null, now.AddSeconds(5), stale);

        Assert.Multiple(() =>
        {
            Assert.That(withoutTakeOver.Outcome, Is.EqualTo(ImportDriverOutcome.StaleRequiresTakeOver));
            Assert.That(takeOver.Outcome, Is.EqualTo(ImportDriverOutcome.Acquired));
            Assert.That(takeOver.DriverToken, Is.Not.Null.And.Not.EqualTo("previous"));
            Assert.That(previousDriverReturns.Outcome, Is.EqualTo(ImportDriverOutcome.ActiveElsewhere));
        });
    }

    [Test]
    public void Finished_Job_Has_No_Driver()
    {
        var result = ImportJobStateMachine.AcquireDriver(Status(ImportJobState.Completed), null, true, null, now, stale);

        Assert.That(result.Outcome, Is.EqualTo(ImportDriverOutcome.JobFinished));
    }

    [Test]
    public void Every_Acquire_Gets_A_New_Call_Id_And_The_Same_Driver_Keeps_Its_Token()
    {
        var first = ImportJobStateMachine.AcquireDriver(Status(ImportJobState.Running), null, false, null, now, stale);
        var second = ImportJobStateMachine.AcquireDriver(first.Status, first.DriverToken, false, null, now.AddSeconds(1), stale);
        var refused = ImportJobStateMachine.AcquireDriver(second.Status, null, false, null, now.AddSeconds(2), stale);

        Assert.Multiple(() =>
        {
            Assert.That(first.CallId, Is.Not.Null.And.Not.Empty);
            Assert.That(first.Status.DriverCallId, Is.EqualTo(first.CallId));
            Assert.That(second.DriverToken, Is.EqualTo(first.DriverToken));
            Assert.That(second.CallId, Is.Not.Null.And.Not.EqualTo(first.CallId));
            Assert.That(second.Status.DriverCallId, Is.EqualTo(second.CallId));
            Assert.That(refused.CallId, Is.Null);
            Assert.That(refused.Status.DriverCallId, Is.EqualTo(second.CallId));
        });
    }

    [TestCase(0, 60)]
    [TestCase(10, 60)]
    [TestCase(30, 60)]
    [TestCase(31, 62)]
    [TestCase(100, 200)]
    [TestCase(300, 600)]
    [TestCase(5000, 600)]
    public void Heartbeat_Age_Grows_With_The_Longest_Batch_Up_To_A_Limit(double longestBatchSeconds, double expectedSeconds)
    {
        var status = Status(ImportJobState.Running) with { LongestBatchSeconds = longestBatchSeconds };

        Assert.That(ImportJobStateMachine.GetHeartbeatStale(status, stale), Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
    }

    [Test]
    public void Job_With_Slow_Batches_Is_Paused_And_Taken_Over_Later()
    {
        var status = Status(ImportJobState.Running) with { DriverToken = "mine", HeartbeatUtc = now.AddSeconds(-150), LongestBatchSeconds = 100 };
        var normal = status with { LongestBatchSeconds = 1 };

        Assert.Multiple(() =>
        {
            Assert.That(ImportJobStateMachine.IsHeartbeatStale(status, now, stale), Is.False);
            Assert.That(ImportJobStateMachine.GetEffectiveState(status, now, stale), Is.EqualTo(ImportJobState.Running));
            Assert.That(ImportJobStateMachine.AcquireDriver(status, null, true, null, now, stale).Outcome, Is.EqualTo(ImportDriverOutcome.ActiveElsewhere));
            Assert.That(ImportJobStateMachine.AcquireDriver(status, null, true, null, now.AddSeconds(51), stale).Outcome, Is.EqualTo(ImportDriverOutcome.Acquired));
            Assert.That(ImportJobStateMachine.GetEffectiveState(normal, now, stale), Is.EqualTo(ImportJobState.Paused));
            Assert.That(ImportJobStateMachine.AcquireDriver(normal, null, true, null, now, stale).Outcome, Is.EqualTo(ImportDriverOutcome.Acquired));
        });
    }

    [TestCase(ImportJobState.Uploaded, ImportJobState.Analyzing)]
    [TestCase(ImportJobState.Confirmed, ImportJobState.Running)]
    public void Starting_A_Phase_Clears_The_Previous_Error_And_Batch_Time(ImportJobState from, ImportJobState to)
    {
        var status = Status(from) with { Error = "The key field is not usable.", LongestBatchSeconds = 90, PhaseStartedByUserId = 7 };

        var started = ImportJobStateMachine.Transition(status, to, now);

        Assert.Multiple(() =>
        {
            Assert.That(started.Error, Is.Null);
            Assert.That(started.LongestBatchSeconds, Is.Zero);
            Assert.That(started.PhaseStartedUtc, Is.EqualTo(now));
            Assert.That(started.PhaseStartedByUserId, Is.Null);
        });
    }

    [Test]
    public void Returning_To_Uploaded_Clears_The_Phase_Start()
    {
        var status = Status(ImportJobState.Confirmed) with { PhaseStartedUtc = now, PhaseStartedByUserId = 7 };

        var updated = ImportJobStateMachine.Transition(status, ImportJobState.Uploaded, now);

        Assert.Multiple(() =>
        {
            Assert.That(updated.PhaseStartedUtc, Is.Null);
            Assert.That(updated.PhaseStartedByUserId, Is.Null);
        });
    }

    // Every state in both phases (PLAN D60): only a confirmed Run that has not ended counts, whatever its heartbeat.
    [TestCase(ImportJobState.Configuring, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Uploading, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Uploaded, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Analyzing, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Analyzed, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Paused, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Confirmed, ImportPhase.Analyze, true)]
    [TestCase(ImportJobState.Confirmed, ImportPhase.Execute, true)]
    [TestCase(ImportJobState.Running, ImportPhase.Execute, true)]
    [TestCase(ImportJobState.Paused, ImportPhase.Execute, true)]
    [TestCase(ImportJobState.Completed, ImportPhase.Execute, false)]
    [TestCase(ImportJobState.Failed, ImportPhase.Execute, false)]
    [TestCase(ImportJobState.Cancelled, ImportPhase.Execute, false)]
    [TestCase(ImportJobState.Failed, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Cancelled, ImportPhase.Analyze, false)]
    public void IsUnfinishedRun_Counts_Confirmed_And_Started_Runs_That_Did_Not_End(ImportJobState state, ImportPhase phase, bool expected)
    {
        foreach (var heartbeat in new DateTime?[] { now, now.AddHours(-1), null })
        {
            var status = Status(state) with { Checkpoint = new ImportCheckpoint { Phase = phase }, HeartbeatUtc = heartbeat };

            Assert.That(ImportJobStateMachine.IsUnfinishedRun(status), Is.EqualTo(expected), $"heartbeat {heartbeat}");
        }
    }

    [Test]
    public void IsUnfinishedRun_Counts_A_Running_Import_That_Shows_As_Paused()
    {
        var status = Status(ImportJobState.Running) with { Checkpoint = new ImportCheckpoint { Phase = ImportPhase.Execute }, HeartbeatUtc = now.AddHours(-1) };

        Assert.Multiple(() =>
        {
            Assert.That(ImportJobStateMachine.GetEffectiveState(status, now, stale), Is.EqualTo(ImportJobState.Paused));
            Assert.That(ImportJobStateMachine.IsUnfinishedRun(status), Is.True);
        });
    }

    [Test]
    public void BlocksRunStart_Lets_The_Job_Confirmed_First_Start()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var earlier = Status(ImportJobState.Confirmed) with { UpdatedUtc = now.AddMinutes(-2) };
        var later = Status(ImportJobState.Confirmed) with { UpdatedUtc = now };
        var sameTime = Status(ImportJobState.Confirmed) with { UpdatedUtc = now };
        var (lowId, highId) = first.CompareTo(second) < 0 ? (first, second) : (second, first);

        Assert.Multiple(() =>
        {
            Assert.That(ImportJobStateMachine.BlocksRunStart(earlier, first, later, second), Is.True, "confirmed first blocks");
            Assert.That(ImportJobStateMachine.BlocksRunStart(later, second, earlier, first), Is.False, "confirmed later does not block");
            Assert.That(ImportJobStateMachine.BlocksRunStart(sameTime, lowId, later, highId), Is.True, "same time: lower ID first");
            Assert.That(ImportJobStateMachine.BlocksRunStart(later, highId, sameTime, lowId), Is.False);
            Assert.That(ImportJobStateMachine.BlocksRunStart(later, first, later, first), Is.False, "a job never blocks itself");
        });
    }

    [TestCase(ImportJobState.Running, true)]
    [TestCase(ImportJobState.Analyzed, false)]
    [TestCase(ImportJobState.Analyzing, false)]
    [TestCase(ImportJobState.Completed, false)]
    [TestCase(ImportJobState.Cancelled, false)]
    public void BlocksRunStart_Started_Runs_Block_Whenever_They_Started(ImportJobState state, bool expected)
    {
        var other = Status(state) with { UpdatedUtc = now, Checkpoint = new ImportCheckpoint { Phase = ImportPhase.Execute } };
        var starting = Status(ImportJobState.Confirmed) with { UpdatedUtc = now.AddHours(-2) };

        Assert.That(ImportJobStateMachine.BlocksRunStart(other, Guid.NewGuid(), starting, Guid.NewGuid()), Is.EqualTo(expected));
    }

    [TestCase(ImportJobState.Completed, ImportPhase.Execute, true)]
    [TestCase(ImportJobState.Failed, ImportPhase.Execute, true)]
    [TestCase(ImportJobState.Cancelled, ImportPhase.Execute, true)]
    [TestCase(ImportJobState.Failed, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Cancelled, ImportPhase.Analyze, false)]
    [TestCase(ImportJobState.Running, ImportPhase.Execute, false)]
    [TestCase(ImportJobState.Analyzed, ImportPhase.Analyze, false)]
    public void IsEndedRun_Counts_Finished_Imports_That_Wrote_Contacts(ImportJobState state, ImportPhase phase, bool expected)
    {
        var status = Status(state) with { Checkpoint = new ImportCheckpoint { Phase = phase }, FinishedUtc = ImportJobStateMachine.IsTerminal(state) ? now : null };

        Assert.That(ImportJobStateMachine.IsEndedRun(status), Is.EqualTo(expected));
    }

    [TestCase(ImportJobState.Uploaded)]
    [TestCase(ImportJobState.Configuring)]
    [TestCase(ImportJobState.Cancelled)]
    [TestCase(ImportJobState.Failed)]
    public void Leaving_Processing_Clears_The_Call_Id(ImportJobState to)
    {
        var status = Status(ImportJobState.Analyzing) with { DriverToken = "mine", DriverCallId = "call", LongestBatchSeconds = 90 };

        var updated = ImportJobStateMachine.Transition(status, to, now);

        Assert.Multiple(() =>
        {
            Assert.That(updated.DriverToken, Is.Null);
            Assert.That(updated.DriverCallId, Is.Null);
        });
    }
}
