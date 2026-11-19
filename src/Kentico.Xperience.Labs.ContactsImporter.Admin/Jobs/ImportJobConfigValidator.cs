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

    internal static ImportMessage ConsentColumnsMissingError => ImportMessages.ConsentColumnsMissing;

    /// <summary>
    /// Returns the problems found (messages for the user, without values of the file); empty when the configuration is valid.
    /// </summary>
    /// <param name="job">Configuration to validate.</param>
    /// <param name="fields">Mappable contact fields (the allow-list for mapping targets).</param>
    /// <param name="options">Importer settings.</param>
    /// <param name="audience">
    /// Checks the selected recipient list and consent against the database. <c>null</c> = not checked here (the
    /// analysis and the import check them again when they start).
    /// </param>
    public static IReadOnlyList<ImportMessage> Validate(ImportJob job, IReadOnlyList<ContactFieldDefinition> fields, ContactsImporterOptions options, IImportAudienceResolver? audience = null)
    {
        var errors = new List<ImportMessage>();
        var fieldsByName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        if (job.JobId == Guid.Empty)
        {
            errors.Add(ImportMessages.JobIdEmpty);
        }

        ValidateFile(job.File, options, errors);
        ValidateMapping(job, fieldsByName, errors);

        if (job.DateFormat.Length is 0 or > MaxDateFormatLength)
        {
            errors.Add(ImportMessages.DateFormatInvalid);
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

    private static void ValidateFile(ImportFileDescriptor file, ContactsImporterOptions options, List<ImportMessage> errors)
    {
        if (string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > MaxFileNameLength)
        {
            errors.Add(ImportMessages.FileNameInvalid);
        }

        if (file.Size <= 0)
        {
            errors.Add(ImportMessages.FileEmpty);
        }
        else if (file.Size > options.MaxFileSizeBytes)
        {
            errors.Add(ImportMessages.FileTooLarge(options.MaxFileSizeBytes));
        }

        if (file.Fingerprint.Length is 0 or > MaxFingerprintLength || !file.Fingerprint.All(char.IsAsciiHexDigit))
        {
            errors.Add(ImportMessages.FingerprintInvalid);
        }
    }

    private static void ValidateMapping(ImportJob job, Dictionary<string, ContactFieldDefinition> fieldsByName, List<ImportMessage> errors)
    {
        if (job.Mapping.Count == 0)
        {
            errors.Add(ImportMessages.NoMapping);
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in job.Mapping)
        {
            if (mapping.Source.Length is 0 or > MaxSourceColumnLength)
            {
                errors.Add(ImportMessages.SourceColumnInvalid);
            }

            if (!fieldsByName.TryGetValue(mapping.Target, out var field))
            {
                errors.Add(ImportMessages.FieldNotImportable(Shorten(mapping.Target)));
            }
            else if (!targets.Add(mapping.Target))
            {
                errors.Add(ImportMessages.FieldMappedTwice(mapping.Target));
            }
            else if (!field.IsSupported)
            {
                errors.Add(ImportMessages.FieldTypeNotSupported(mapping.Target));
            }
        }

        if (!targets.Contains(job.KeyField))
        {
            errors.Add(ImportMessages.KeyFieldNotMapped);
        }
        else if (!fieldsByName[job.KeyField].CanBeKey)
        {
            errors.Add(ImportMessages.KeyFieldNotAllowed(job.KeyField));
        }
    }

    private static void ValidateConsentMapping(ImportJob job, List<ImportMessage> errors)
    {
        if (job.ConsentMapping is not { } consent)
        {
            return;
        }

        if (consent.Agreed is null && consent.Date is null)
        {
            errors.Add(ImportMessages.ConsentColumnsEmpty);
            return;
        }

        if (consent.Agreed is { Length: 0 or > MaxSourceColumnLength } || consent.Date is { Length: 0 or > MaxSourceColumnLength })
        {
            errors.Add(ImportMessages.ConsentColumnInvalid);
        }

        if (consent.Agreed is not null && string.Equals(consent.Agreed, consent.Date, StringComparison.Ordinal))
        {
            errors.Add(ImportMessages.ConsentColumnsSame);
        }

        if (job.Mapping.Any(m => string.Equals(m.Source, consent.Agreed, StringComparison.Ordinal) || string.Equals(m.Source, consent.Date, StringComparison.Ordinal)))
        {
            errors.Add(ImportMessages.ConsentColumnMapped);
        }
    }

    private static void ValidateOptions(ImportOptions options, Dictionary<string, ContactFieldDefinition> fieldsByName, List<ImportMessage> errors)
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
            errors.Add(ImportMessages.OptionUnknown);
        }

        if (options.RecipientListId is <= 0)
        {
            errors.Add(ImportMessages.RecipientListInvalid);
        }

        if (options.ConsentId is <= 0 || (options.Consent != ConsentMode.None && options.ConsentId is null))
        {
            errors.Add(ImportMessages.ConsentNotSelected);
        }

        if (options.SourceField is null)
        {
            return;
        }

        if (!fieldsByName.TryGetValue(options.SourceField, out var sourceField) || !sourceField.IsText)
        {
            errors.Add(ImportMessages.SourceFieldNotText);
        }
        else if (string.IsNullOrWhiteSpace(options.SourceValue) || options.SourceValue.Length > (sourceField.MaxLength ?? int.MaxValue))
        {
            errors.Add(ImportMessages.SourceValueInvalid);
        }
    }

    private static string Shorten(string value) => value.Length <= 100 ? value : value[..100];
}
