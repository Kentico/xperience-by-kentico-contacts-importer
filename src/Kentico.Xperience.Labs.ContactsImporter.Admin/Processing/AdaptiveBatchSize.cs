using Microsoft.Data.SqlClient;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Batch size of the import (execute phase) that follows the measured database time of each batch toward
/// <see cref="ContactsImporterOptions.TargetBatchSeconds"/> (PLAN D58). Pure functions; the size is stored in
/// <see cref="ImportCheckpoint.BatchSize"/>.
/// </summary>
internal static class AdaptiveBatchSize
{
    /// <summary>A batch slower than the target times this gets a smaller successor.</summary>
    internal const double SlowFactor = 1.2;

    /// <summary>A full batch faster than the target times this gets a larger successor.</summary>
    internal const double FastFactor = 0.8;

    /// <summary>Largest growth from one batch to the next.</summary>
    internal const double MaxGrowth = 1.5;

    /// <summary>Largest shrink from one batch to the next after a slow batch.</summary>
    internal const double MaxShrink = 0.25;

    // Timeout, deadlock victim, Azure SQL resource limits and service busy: the database is overloaded.
    private static readonly HashSet<int> loadSqlErrors = [-2, 1205, 10928, 10929, 40501];

    /// <summary>
    /// Rows of the next batch of a phase: the stored adapted size for an import, otherwise the configured size.
    /// Clamped to the current options, so changed options take effect for a running import.
    /// </summary>
    public static int Current(ImportPhase phase, ImportCheckpoint checkpoint, ContactsImporterOptions options) =>
        phase == ImportPhase.Execute && options.AdaptiveBatchSize && checkpoint.BatchSize is { } size
            ? Math.Clamp(size, options.EffectiveMinBatchSize, options.BatchSize)
            : options.BatchSize;

    /// <summary>
    /// Size of the batch after one that asked for <paramref name="current"/> rows.
    /// </summary>
    /// <param name="current">Rows the batch asked for.</param>
    /// <param name="rows">Rows the batch had; fewer than <paramref name="current"/> at the end of a chunk.</param>
    /// <param name="seconds">Database time of the batch.</param>
    /// <param name="targetSeconds">Database time a batch aims for.</param>
    /// <param name="min">Smallest size.</param>
    /// <param name="max">Largest size.</param>
    /// <param name="loadError">The batch failed with a database timeout, deadlock or resource limit.</param>
    /// <returns>
    /// Halved after a load error. Scaled by target / actual when slower than the target (at most to a quarter),
    /// grown by target / actual when a full batch was clearly faster (at most ×1.5, at least one row), kept otherwise.
    /// Always within <paramref name="min"/> and <paramref name="max"/>.
    /// </returns>
    public static int Next(int current, int rows, double seconds, double targetSeconds, int min, int max, bool loadError)
    {
        min = Math.Max(1, Math.Min(min, max));
        current = Math.Clamp(current, min, max);

        if (loadError)
        {
            return Math.Max(min, current / 2);
        }

        if (!(seconds > 0) || !(targetSeconds > 0) || double.IsInfinity(seconds))
        {
            return current;
        }

        double ratio = targetSeconds / seconds;
        long next = current;
        if (seconds > targetSeconds * SlowFactor)
        {
            next = (long)Math.Floor(current * Math.Max(ratio, MaxShrink));
        }
        else if (seconds < targetSeconds * FastFactor && rows >= current)
        {
            next = Math.Max(current + 1L, (long)Math.Floor(current * Math.Min(ratio, MaxGrowth)));
        }

        return (int)Math.Clamp(next, min, max);
    }

    /// <summary>
    /// Whether an exception says the database is overloaded (SQL or <see cref="TimeoutException"/> timeout, deadlock, resource limit): the next batch is smaller.
    /// </summary>
    public static bool IsLoadError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }

            if (current is SqlException sqlException && sqlException.Errors.Cast<SqlError>().Any(error => loadSqlErrors.Contains(error.Number)))
            {
                return true;
            }
        }

        return false;
    }
}
