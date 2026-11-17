namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Validates a job configuration (<c>job.json</c>) received from the browser against the contact class
/// and the importer settings before it is stored.
/// </summary>
internal static class ImportJobConfigValidator
{
    internal const int MaxFileNameLength = 260;
    internal const int MaxFingerprintLength = 128;
    internal const int MaxSourceColumnLength = 1024;
    internal const int MaxDateFormatLength = 64;

    internal const string ConsentColumnsMissingError = "Consent from a file column needs a mapped consent column. Map a consent column or choose another consent option.";

    /// <summary>
    /// Returns the problems found; empty when the configuration is valid.
    /// </summary>
    /// <param name="job">Configuration to validate.</param>
    /// <param name="fields">Mappable contact fields (the allow-list for mapping targets).</param>
    /// <param name="options">Importer settings.</param>
    /// <param name="audience">
    /// Checks the selected recipient list and consent against the database. <c>null</c> = not checked here (the
    /// analysis and the import check them again when they start).
    /// </param>
    public static IReadOnlyList<string> Validate(ImportJob job, IReadOnlyList<ContactFieldDefinition> fields, ContactsImporterOptions options, IImportAudienceResolver? audience = null)
    {
        var errors = new List<string>();
        var fieldsByName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        if (job.JobId == Guid.Empty)
        {
            errors.Add("The job ID is empty.");
        }

        ValidateFile(job.File, options, errors);
        ValidateMapping(job, fieldsByName, errors);

        if (job.DateFormat.Length is 0 or > MaxDateFormatLength)
        {
            errors.Add("The date format is missing or too long.");
        }

        ValidateConsentMapping(job, errors);
        ValidateOptions(job.Options, fieldsByName, errors);

        if (job.Options.Consent == ConsentMode.BlanketWithColumnOverride && job.ConsentMapping is null)
        {
            errors.Add(ConsentColumnsMissingError);
        }

        // Only a configuration that is valid so far is worth a database query.
        if (errors.Count == 0 && audience is not null && audience.Resolve(job.Options).Error is { } audienceError)
        {
            errors.Add(audienceError);
        }

        return errors;
    }

    private static void ValidateFile(ImportFileDescriptor file, ContactsImporterOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > MaxFileNameLength)
        {
            errors.Add("The file name is missing or too long.");
        }

        if (file.Size <= 0)
        {
            errors.Add("The file is empty.");
        }
        else if (file.Size > options.MaxFileSizeBytes)
        {
            errors.Add($"The file is larger than the allowed {options.MaxFileSizeBytes} bytes.");
        }

        if (file.Fingerprint.Length is 0 or > MaxFingerprintLength || !file.Fingerprint.All(char.IsAsciiHexDigit))
        {
            errors.Add("The file fingerprint is not a hex string.");
        }
    }

    private static void ValidateMapping(ImportJob job, Dictionary<string, ContactFieldDefinition> fieldsByName, List<string> errors)
    {
        if (job.Mapping.Count == 0)
        {
            errors.Add("No columns are mapped.");
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in job.Mapping)
        {
            if (mapping.Source.Length is 0 or > MaxSourceColumnLength)
            {
                errors.Add("A mapped source column name is missing or too long.");
            }

            if (!fieldsByName.TryGetValue(mapping.Target, out var field))
            {
                errors.Add($"'{Shorten(mapping.Target)}' is not an importable contact field.");
            }
            else if (!targets.Add(mapping.Target))
            {
                errors.Add($"Contact field '{mapping.Target}' is mapped more than once.");
            }
            else if (!field.IsSupported)
            {
                errors.Add($"Contact field '{mapping.Target}' has a data type that cannot be imported.");
            }
        }

        if (!targets.Contains(job.KeyField))
        {
            errors.Add("The key field must be one of the mapped contact fields.");
        }
        else if (!fieldsByName[job.KeyField].CanBeKey)
        {
            errors.Add($"Contact field '{job.KeyField}' cannot be the key field: only text, whole number and GUID fields can.");
        }
    }

    private static void ValidateConsentMapping(ImportJob job, List<string> errors)
    {
        if (job.ConsentMapping is not { } consent)
        {
            return;
        }

        if (consent.Agreed is null && consent.Date is null)
        {
            errors.Add("The consent columns are set without a column.");
            return;
        }

        if (consent.Agreed is { Length: 0 or > MaxSourceColumnLength } || consent.Date is { Length: 0 or > MaxSourceColumnLength })
        {
            errors.Add("A consent source column name is missing or too long.");
        }

        if (consent.Agreed is not null && string.Equals(consent.Agreed, consent.Date, StringComparison.Ordinal))
        {
            errors.Add("The consent given and consent date columns must be different columns.");
        }

        if (job.Mapping.Any(m => string.Equals(m.Source, consent.Agreed, StringComparison.Ordinal) || string.Equals(m.Source, consent.Date, StringComparison.Ordinal)))
        {
            errors.Add("A consent column cannot also be mapped to a contact field.");
        }
    }

    private static void ValidateOptions(ImportOptions options, Dictionary<string, ContactFieldDefinition> fieldsByName, List<string> errors)
    {
        bool enumsDefined = Enum.IsDefined(options.NewContacts)
            && Enum.IsDefined(options.ExistingContacts)
            && Enum.IsDefined(options.EmptyValues)
            && Enum.IsDefined(options.MultipleMatches)
            && Enum.IsDefined(options.InvalidEmail)
            && Enum.IsDefined(options.Unsubscribed)
            && Enum.IsDefined(options.BouncedAddress)
            && Enum.IsDefined(options.Consent)
            && Enum.IsDefined(options.RevokedConsent)
            && Enum.IsDefined(options.SourceStamp);
        if (!enumsDefined)
        {
            errors.Add("An option has an unknown value.");
        }

        if (options.RecipientListId is <= 0)
        {
            errors.Add("The recipient list is not valid.");
        }

        if (options.ConsentId is <= 0 || (options.Consent != ConsentMode.None && options.ConsentId is null))
        {
            errors.Add("A consent must be selected when consent agreements are recorded.");
        }

        if (options.SourceField is null)
        {
            return;
        }

        if (!fieldsByName.TryGetValue(options.SourceField, out var sourceField) || !sourceField.IsText)
        {
            errors.Add("The import source field must be a text contact field.");
        }
        else if (string.IsNullOrWhiteSpace(options.SourceValue) || options.SourceValue.Length > (sourceField.MaxLength ?? int.MaxValue))
        {
            errors.Add("The import source value is missing or too long for the field.");
        }
    }

    private static string Shorten(string value) => value.Length <= 100 ? value : value[..100];
}
