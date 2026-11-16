using System.Globalization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// How values of a contact field are converted, compared and passed to SQL.
/// </summary>
internal enum ImportValueKind
{
    Text,
    Integer,
    LongInteger,
    Double,
    Decimal,
    DateTime,
    Date,
    Boolean,
    Guid,
}

/// <summary>
/// A mapped contact field of a job being processed.
/// </summary>
/// <param name="Index">Position in the job mapping; also the position of the field's value in a record.</param>
/// <param name="Name">Contact field (database column) name. Comes from the contact class definition, never from a file.</param>
/// <param name="Kind">Value kind derived from the field data type.</param>
/// <param name="MaxLength">Maximum length of text values; <c>null</c> = unlimited.</param>
/// <param name="DecimalDigits">Total digits of a decimal field.</param>
/// <param name="DecimalScale">Decimal places of a decimal field.</param>
/// <param name="Reference">Table and column the values must exist in; <c>null</c> when the field is not a reference the import checks.</param>
internal sealed record ImportField(int Index, string Name, ImportValueKind Kind, int? MaxLength = null, int DecimalDigits = ImportField.DefaultDecimalDigits, int DecimalScale = ImportField.DefaultDecimalScale, ContactFieldReference? Reference = null)
{
    /// <summary>Size Xperience gives a decimal field without an explicit size.</summary>
    public const int DefaultDecimalDigits = 19;

    /// <summary>Decimal places Xperience gives a decimal field without an explicit precision.</summary>
    public const int DefaultDecimalScale = 4;

    private const int MaxSizedTextLength = 4000;

    /// <summary>
    /// SQL type the value has inside <c>OPENJSON(...) WITH (...)</c>. Built from the class definition only.
    /// </summary>
    public string SqlType => Kind switch
    {
        ImportValueKind.Text => MaxLength is > 0 and <= MaxSizedTextLength
            ? string.Create(CultureInfo.InvariantCulture, $"nvarchar({MaxLength})")
            : "nvarchar(max)",
        ImportValueKind.Integer => "int",
        ImportValueKind.LongInteger => "bigint",
        ImportValueKind.Double => "float",
        ImportValueKind.Decimal => string.Create(CultureInfo.InvariantCulture, $"decimal({DecimalDigits},{DecimalScale})"),
        ImportValueKind.DateTime => "datetime2(7)",
        ImportValueKind.Date => "date",
        ImportValueKind.Boolean => "bit",
        ImportValueKind.Guid => "uniqueidentifier",
        _ => throw new InvalidOperationException($"Unknown value kind {Kind}."),
    };

    /// <summary>
    /// Maps an Xperience field data type to a value kind; <c>null</c> for types the importer cannot write.
    /// </summary>
    public static ImportValueKind? GetKind(string dataType) => dataType.ToLowerInvariant() switch
    {
        "text" or "longtext" => ImportValueKind.Text,
        "integer" => ImportValueKind.Integer,
        "longinteger" => ImportValueKind.LongInteger,
        "double" => ImportValueKind.Double,
        "decimal" => ImportValueKind.Decimal,
        "datetime" => ImportValueKind.DateTime,
        "date" => ImportValueKind.Date,
        "boolean" => ImportValueKind.Boolean,
        "guid" => ImportValueKind.Guid,
        _ => null,
    };

    /// <summary>
    /// Whether a field of the Xperience data type can be the dedupe key: sized text, whole numbers and GUIDs.
    /// </summary>
    public static bool IsKeyDataType(string dataType) => dataType.ToLowerInvariant() is "text" or "integer" or "longinteger" or "guid";
}

/// <summary>
/// Everything the processing of one job needs to know: mapped fields with their types, the key, and the options.
/// Built once per processing call from <c>job.json</c> and the current contact class definition.
/// </summary>
internal sealed class ImportProcessingContext
{
    private const int MaxDecimalDigits = 38;

    private ImportProcessingContext(ImportJob job, ImportField[] fields, int keyIndex, int emailIndex, string? sourceField)
    {
        Options = job.Options;
        Fields = fields;
        KeyIndex = keyIndex;
        EmailIndex = emailIndex;
        SourceField = sourceField;
        SourceValue = sourceField is null ? null : job.Options.SourceValue;
        FieldIndexes = fields.ToDictionary(f => f.Name, f => f.Index, StringComparer.Ordinal);
        ReferenceGroups = [.. fields
            .Where(f => f.Reference is not null)
            .GroupBy(f => (Schema: f.Reference!.SchemaName ?? "", Table: f.Reference.TableName!, Column: f.Reference.ColumnName!, f.Kind), ReferenceTargetComparer.Instance)
            .Select(group => (IReadOnlyList<ImportField>)[.. group])];
    }

    public ImportOptions Options { get; }

    /// <summary>Mapped fields in mapping order.</summary>
    public IReadOnlyList<ImportField> Fields { get; }

    public IReadOnlyDictionary<string, int> FieldIndexes { get; }

    /// <summary>
    /// Mapped fields whose values must exist in another table (<see cref="ImportField.Reference"/>), grouped by the
    /// referenced table and column (and value kind): one existence check per group and batch. Mapping order.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ImportField>> ReferenceGroups { get; }

    public int KeyIndex { get; }

    public ImportField KeyField => Fields[KeyIndex];

    /// <summary>
    /// Index of the email field when it is mapped and is not the key, otherwise -1. Only then can a stored row have an
    /// invalid email: with email as the key, such rows never leave the browser and the upload validation rejects them.
    /// </summary>
    public int EmailIndex { get; }

    /// <summary>Contact field stamped with <see cref="SourceValue"/>; <c>null</c> = no stamp.</summary>
    public string? SourceField { get; }

    public string? SourceValue { get; }

    public bool ClearEmptyValues => Options.EmptyValues == EmptyValuesBehavior.Clear;

    public bool StampUpdatedContacts => SourceField is not null && Options.SourceStamp == SourceStampBehavior.CreatedAndUpdated;

    /// <summary>
    /// Builds the context. Fails (with a message free of contact data) when the job no longer matches the contact
    /// class: a mapped field is gone or has a data type the importer cannot write, or the key or source field is not usable.
    /// </summary>
    public static bool TryCreate(ImportJob job, IReadOnlyList<ContactFieldDefinition> mappableFields, out ImportProcessingContext? context, out string? error)
    {
        context = null;
        var definitions = mappableFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var fields = new ImportField[job.Mapping.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < fields.Length; i++)
        {
            string target = job.Mapping[i].Target;
            if (!definitions.TryGetValue(target, out var definition))
            {
                error = "A mapped contact field no longer exists or cannot be imported. Map the columns again.";
                return false;
            }

            if (!names.Add(target))
            {
                error = $"Contact field '{definition.Name}' is mapped more than once.";
                return false;
            }

            if (ImportField.GetKind(definition.DataType) is not { } kind)
            {
                error = $"Contact field '{definition.Name}' has a data type that cannot be imported.";
                return false;
            }

            fields[i] = CreateField(i, definition, kind);
        }

        int keyIndex = Array.FindIndex(fields, f => string.Equals(f.Name, job.KeyField, StringComparison.Ordinal));
        if (keyIndex < 0)
        {
            error = "The key field is not one of the mapped contact fields.";
            return false;
        }

        if (!definitions[fields[keyIndex].Name].CanBeKey)
        {
            error = $"Contact field '{fields[keyIndex].Name}' cannot be the key field: only text, whole number and GUID fields can.";
            return false;
        }

        string? sourceField = job.Options.SourceField;
        if (sourceField is not null)
        {
            if (!definitions.TryGetValue(sourceField, out var source) || !source.IsText)
            {
                error = "The import source field is not a text contact field.";
                return false;
            }

            if (string.IsNullOrEmpty(job.Options.SourceValue) || job.Options.SourceValue.Length > (source.MaxLength ?? int.MaxValue))
            {
                error = "The import source value is missing or too long for the field.";
                return false;
            }
        }

        int emailIndex = Array.FindIndex(fields, f => KeyNormalizer.IsEmailField(f.Name));
        error = null;
        context = new ImportProcessingContext(job, fields, keyIndex, emailIndex == keyIndex ? -1 : emailIndex, sourceField);

        return true;
    }

    private static ImportField CreateField(int index, ContactFieldDefinition definition, ImportValueKind kind)
    {
        if (kind != ImportValueKind.Decimal)
        {
            return new ImportField(index, definition.Name, kind, kind == ImportValueKind.Text ? definition.MaxLength : null)
            {
                Reference = definition.Reference is { CanBeChecked: true } reference ? reference : null,
            };
        }

        int digits = definition.MaxLength is > 0 and <= MaxDecimalDigits ? definition.MaxLength.Value : ImportField.DefaultDecimalDigits;
        int scale = definition.Precision is { } precision && precision >= 0 && precision <= digits ? precision : Math.Min(ImportField.DefaultDecimalScale, digits);

        return new ImportField(index, definition.Name, kind, null, digits, scale);
    }

    private sealed class ReferenceTargetComparer : IEqualityComparer<(string Schema, string Table, string Column, ImportValueKind Kind)>
    {
        public static ReferenceTargetComparer Instance { get; } = new();

        public bool Equals((string Schema, string Table, string Column, ImportValueKind Kind) x, (string Schema, string Table, string Column, ImportValueKind Kind) y) =>
            x.Kind == y.Kind
            && string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Table, string Column, ImportValueKind Kind) obj) =>
            HashCode.Combine(
                obj.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Column));
    }
}
