using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

internal sealed class ContactsImporterOptionsValidator : IValidateOptions<ContactsImporterOptions>
{
    public ValidateOptionsResult Validate(string? name, ContactsImporterOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.StoragePath))
        {
            errors.Add($"{nameof(ContactsImporterOptions.StoragePath)} must not be empty.");
        }

        RequirePositive(errors, options.MaxFileSizeBytes, nameof(ContactsImporterOptions.MaxFileSizeBytes));
        RequirePositive(errors, options.ChunkSizeBytes, nameof(ContactsImporterOptions.ChunkSizeBytes));
        RequirePositive(errors, options.MaxChunkUncompressedBytes, nameof(ContactsImporterOptions.MaxChunkUncompressedBytes));
        RequirePositive(errors, options.BatchSize, nameof(ContactsImporterOptions.BatchSize));
        RequirePositive(errors, options.MinBatchSize, nameof(ContactsImporterOptions.MinBatchSize));
        if (!(options.TargetBatchSeconds > 0) || double.IsInfinity(options.TargetBatchSeconds))
        {
            errors.Add($"{nameof(ContactsImporterOptions.TargetBatchSeconds)} must be greater than 0.");
        }

        RequirePositive(errors, options.ProcessingTimeBudgetSeconds, nameof(ContactsImporterOptions.ProcessingTimeBudgetSeconds));
        RequirePositive(errors, options.AnalysisValidHours, nameof(ContactsImporterOptions.AnalysisValidHours));
        RequirePositive(errors, options.HeartbeatStaleSeconds, nameof(ContactsImporterOptions.HeartbeatStaleSeconds));
        RequirePositive(errors, options.ReportRetentionDays, nameof(ContactsImporterOptions.ReportRetentionDays));
        RequirePositive(errors, options.ReportWithKeysRetentionHours, nameof(ContactsImporterOptions.ReportWithKeysRetentionHours));
        RequirePositive(errors, options.AbandonedJobRetentionDays, nameof(ContactsImporterOptions.AbandonedJobRetentionDays));
        RequirePositive(errors, options.StatusRetentionDays, nameof(ContactsImporterOptions.StatusRetentionDays));

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void RequirePositive(List<string> errors, long value, string name)
    {
        if (value <= 0)
        {
            errors.Add($"{name} must be greater than 0.");
        }
    }
}
