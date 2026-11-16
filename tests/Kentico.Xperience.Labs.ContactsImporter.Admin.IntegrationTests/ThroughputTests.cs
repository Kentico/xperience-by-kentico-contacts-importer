using System.Diagnostics;
using System.Globalization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// Rough throughput measurement, not a pass/fail test. Runs only when <see cref="RowsVariable"/> is set, for example
/// <c>$env:CONTACTSIMPORTER_PERF_ROWS = 100000</c>; prints rows per second of each phase.
/// </summary>
public class ThroughputTests : ImportIntegrationTest
{
    public const string RowsVariable = "CONTACTSIMPORTER_PERF_ROWS";

    [Test]
    public void Measure_Analyze_And_Execute_Throughput()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(RowsVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int rowCount) || rowCount < 1)
        {
            Assert.Ignore($"Set {RowsVariable} to the number of rows to measure.");
        }

        string[] targets = [Email, FirstName, LastName, "ContactCompanyName", "ContactCity"];
        using var harness = new ImportHarness();

        var created = Measure(harness, targets, rowCount, "A");
        var unchanged = Measure(harness, targets, rowCount, "A");
        var updated = Measure(harness, targets, rowCount, "B");

        Assert.Multiple(() =>
        {
            Assert.That(created.Progress.Aggregates.Created, Is.EqualTo(rowCount));
            Assert.That(unchanged.Progress.Aggregates.Unchanged, Is.EqualTo(rowCount));
            Assert.That(updated.Progress.Aggregates.Updated, Is.EqualTo(rowCount));
            Assert.That(ContactCount(), Is.EqualTo(rowCount));
        });

        TestContext.Out.WriteLine($"Rows: {rowCount}, batch size: {harness.Temp.Options.Value.BatchSize}, {targets.Length} mapped fields, key ContactEmail");
        Report("create   ", rowCount, created);
        Report("unchanged", rowCount, unchanged);
        Report("update   ", rowCount, updated);
    }

    private static (ImportProgress Progress, TimeSpan Analyze, TimeSpan Execute) Measure(ImportHarness harness, string[] targets, int rowCount, string version)
    {
        string?[][] rows = new string?[rowCount][];
        for (int i = 0; i < rowCount; i++)
        {
            string number = i.ToString(CultureInfo.InvariantCulture);
            rows[i] = [$"user{number}@throughput.test", $"First{version}{number}", $"Last{version}{number}", $"Company {number}", "Springfield"];
        }

        var jobId = harness.Upload(Email, targets, rows, rowsPerChunk: 25_000);

        var watch = Stopwatch.StartNew();
        var analysis = harness.Run(jobId, ImportPhase.Analyze);
        var analyze = watch.Elapsed;
        Assert.That(analysis.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), analysis.Error);
        Assert.That(harness.Processor.Confirm(jobId).Outcome, Is.EqualTo(ImportConfirmOutcome.Confirmed));

        watch.Restart();
        var progress = harness.Run(jobId, ImportPhase.Execute);
        var execute = watch.Elapsed;
        Assert.That(progress.Outcome, Is.EqualTo(ImportProcessOutcome.PhaseFinished), progress.Error);

        return (progress, analyze, execute);
    }

    private static void Report(string name, int rowCount, (ImportProgress Progress, TimeSpan Analyze, TimeSpan Execute) run) =>
        TestContext.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: analyze {run.Analyze.TotalSeconds:F1} s ({rowCount / run.Analyze.TotalSeconds:F0} rows/s), execute {run.Execute.TotalSeconds:F1} s ({rowCount / run.Execute.TotalSeconds:F0} rows/s)"));
}
