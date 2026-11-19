namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Describes the contact fields an import may write to. The returned fields are the allow-list for mapping targets.
/// </summary>
internal interface IContactFieldSchemaProvider
{
    /// <summary>
    /// Returns the mappable fields of the contact class (system and custom), in class definition order.
    /// Only the fields the importer owns are left out: the ID, the GUID and the created / last modified times.
    /// Fields that reference another object have <see cref="ContactFieldDefinition.Reference"/> set.
    /// Fields with a data type the import cannot write are included with <see cref="ContactFieldDefinition.IsSupported"/> = false.
    /// </summary>
    public IReadOnlyList<ContactFieldDefinition> GetMappableFields();
}

/// <summary>
/// What a contact field references: the ID (or another key) of an object in another table.
/// </summary>
/// <param name="ObjectType">Xperience object type, for example <c>cms.country</c>; <c>null</c> when only a foreign key of the table tells about the reference.</param>
/// <param name="TableName">Referenced table; <c>null</c> when it is not known. Comes from database or class metadata, never from a file.</param>
/// <param name="ColumnName">Referenced column; <c>null</c> when it is not known.</param>
/// <param name="SchemaName">Schema of the referenced table; <c>null</c> = the default schema.</param>
internal sealed record ContactFieldReference(string? ObjectType, string? TableName = null, string? ColumnName = null, string? SchemaName = null)
{
    /// <summary>
    /// Whether the import can check that a referenced value exists: the table and column are known.
    /// </summary>
    public bool CanBeChecked => !string.IsNullOrEmpty(TableName) && !string.IsNullOrEmpty(ColumnName);
}

/// <summary>
/// A contact field that can be a mapping target.
/// </summary>
/// <param name="Name">Field (database column) name, for example <c>ContactEmail</c>.</param>
/// <param name="Caption">Display name for the mapping UI.</param>
/// <param name="DataType">Xperience field data type, for example <c>text</c>, <c>integer</c>, <c>datetime</c>.</param>
/// <param name="MaxLength">Maximum number of characters for sized text fields (total number of digits for decimal fields); <c>null</c> otherwise.</param>
/// <param name="Precision">Number of decimal places of decimal fields; <c>null</c> otherwise.</param>
/// <param name="Reference">What the field references; <c>null</c> for a field that holds plain data.</param>
internal sealed record ContactFieldDefinition(string Name, string Caption, string DataType, int? MaxLength, int? Precision = null, ContactFieldReference? Reference = null)
{
    /// <summary>
    /// The field holds the ID of another object (country, contact status, user, ...). A row whose value does not
    /// exist there is skipped (<see cref="ImportRowReasons.InvalidValue"/>).
    /// </summary>
    public bool IsReference => Reference is not null;

    /// <summary>
    /// Longest accepted raw value for fields that are not text (numbers, dates, booleans, GUIDs).
    /// </summary>
    public const int MaxNonTextValueLength = 256;

    public bool IsText => string.Equals(DataType, "text", StringComparison.OrdinalIgnoreCase)
        || string.Equals(DataType, "longtext", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Longest raw string value accepted for the field, or <c>null</c> when only the record size limits it.
    /// </summary>
    public int? MaxValueLength => IsText ? MaxLength : MaxNonTextValueLength;

    /// <summary>
    /// Whether the import can write the field's data type (text, numbers, dates, boolean, GUID). Fields of other
    /// types (binary, XML, references to content, ...) are listed so the mapping UI can show them as not available;
    /// a job that maps one is refused (<see cref="ImportJobConfigValidator"/>).
    /// </summary>
    public bool IsSupported => ImportField.GetKind(DataType) is not null;

    /// <summary>
    /// Whether the field can be the dedupe key: sized text, whole number and GUID fields. Other types have no exact,
    /// stable text form (decimals, dates, booleans) or cannot be looked up efficiently (long text).
    /// </summary>
    public bool CanBeKey => ImportField.IsKeyDataType(DataType);
}
