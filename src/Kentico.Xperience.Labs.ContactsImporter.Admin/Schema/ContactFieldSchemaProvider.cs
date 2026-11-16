using System.Data;

using CMS.ContactManagement;
using CMS.DataEngine;
using CMS.FormEngine;
using CMS.Helpers;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Reads mappable contact fields from the <c>OM.Contact</c> class definition, including custom fields.
/// </summary>
internal sealed class ContactFieldSchemaProvider : IContactFieldSchemaProvider
{
    internal const string ForeignKeyColumn = "FieldColumn";
    internal const string ForeignKeySchemaColumn = "ReferencedSchema";
    internal const string ForeignKeyTableColumn = "ReferencedTable";
    internal const string ForeignKeyReferencedColumn = "ReferencedColumn";

    /// <summary>
    /// Enabled single-column foreign keys of a table: the column, and the table and column it references.
    /// </summary>
    internal const string ForeignKeysSql =
        $"""
        SELECT pc.[name] AS [{ForeignKeyColumn}], OBJECT_SCHEMA_NAME(fk.[referenced_object_id]) AS [{ForeignKeySchemaColumn}],
            OBJECT_NAME(fk.[referenced_object_id]) AS [{ForeignKeyTableColumn}], rc.[name] AS [{ForeignKeyReferencedColumn}]
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.[constraint_object_id] = fk.[object_id]
        INNER JOIN sys.columns AS pc ON pc.[object_id] = fkc.[parent_object_id] AND pc.[column_id] = fkc.[parent_column_id]
        INNER JOIN sys.columns AS rc ON rc.[object_id] = fkc.[referenced_object_id] AND rc.[column_id] = fkc.[referenced_column_id]
        WHERE fk.[parent_object_id] = OBJECT_ID(@table) AND fk.[is_disabled] = 0
            AND (SELECT COUNT(*) FROM sys.foreign_key_columns AS x WHERE x.[constraint_object_id] = fk.[object_id]) = 1
        """;

    /// <summary>
    /// Fields the importer owns and never writes from a file, by name: identity and timestamps. The ID, GUID and
    /// last modified columns are also taken from the contact type (<see cref="GetOwnedColumns"/>).
    /// </summary>
    internal static readonly IReadOnlySet<string> ExcludedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ContactInfo.ContactID),
        nameof(ContactInfo.ContactGUID),
        nameof(ContactInfo.ContactCreated),
        nameof(ContactInfo.ContactLastModified),
    };

    public IReadOnlyList<ContactFieldDefinition> GetMappableFields()
    {
        var typeInfo = ContactInfo.TYPEINFO;
        var dataClass = DataClassInfoProvider.GetDataClassInfo(typeInfo.ObjectClassName)
            ?? throw new InvalidOperationException("The contact class was not found.");
        var form = new FormInfo(dataClass.ClassFormDefinition);
        var ownedColumns = GetOwnedColumns(typeInfo);
        var dependencies = GetDependencies(typeInfo);
        var foreignKeys = GetForeignKeys(dataClass.ClassTableName);

        return [.. form.GetFields(true, true)
            .Where(field => IsMappable(field, ownedColumns))
            .Select(field => ToDefinition(field, GetReference(field, dependencies, foreignKeys, ResolveObjectType)))];
    }

    /// <summary>
    /// Columns the importer sets itself: <see cref="ExcludedFields"/> plus the ID, GUID and last modified columns the
    /// contact type declares.
    /// </summary>
    internal static IReadOnlySet<string> GetOwnedColumns(ObjectTypeInfo typeInfo)
    {
        string?[] typeColumns = [typeInfo.IDColumn, typeInfo.GUIDColumn, typeInfo.TimeStampColumn];
        var columns = new HashSet<string>(ExcludedFields, StringComparer.OrdinalIgnoreCase);
        columns.UnionWith(typeColumns.Where(column => !string.IsNullOrEmpty(column) && !string.Equals(column, ObjectTypeInfo.COLUMN_NAME_UNKNOWN, StringComparison.Ordinal))!);

        return columns;
    }

    /// <summary>
    /// Object type by column for the references the contact type declares (<see cref="ObjectTypeInfo.DependsOn"/>:
    /// state, country, contact status, owner).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> GetDependencies(ObjectTypeInfo typeInfo)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in (typeInfo.DependsOn ?? []).Where(d => !string.IsNullOrEmpty(d.DependencyColumn) && !string.IsNullOrEmpty(d.DependencyObjectType)))
        {
            dependencies.TryAdd(dependency.DependencyColumn, dependency.DependencyObjectType);
        }

        return dependencies;
    }

    /// <summary>
    /// Whether an import may write the field: every field except the primary key and the columns the importer owns.
    /// </summary>
    internal static bool IsMappable(FormFieldInfo field, IReadOnlySet<string> ownedColumns) =>
        !field.PrimaryKey && !ownedColumns.Contains(field.Name);

    /// <summary>
    /// Finds out whether a field references another object, from three sources: a foreign key of the contact table
    /// (<paramref name="foreignKeys"/>), a dependency of the contact type (<paramref name="dependencies"/>) and the
    /// reference settings of the field itself. The foreign key decides which table and column are checked, because it
    /// is what the database enforces; without one, the object type is resolved to its table and ID column, and only
    /// whole number fields are checked.
    /// </summary>
    /// <param name="field">Field of the contact class.</param>
    /// <param name="dependencies">Object type by column (<see cref="GetDependencies"/>).</param>
    /// <param name="foreignKeys">Foreign key target by column.</param>
    /// <param name="resolveObjectType">Returns the table and ID column of an object type, or <c>null</c>.</param>
    internal static ContactFieldReference? GetReference(
        FormFieldInfo field,
        IReadOnlyDictionary<string, string> dependencies,
        IReadOnlyDictionary<string, ContactFieldReference> foreignKeys,
        Func<string, (string TableName, string IdColumn)?> resolveObjectType)
    {
        string? objectType = string.IsNullOrEmpty(field.ReferenceToObjectType) ? null : field.ReferenceToObjectType;
        if (dependencies.TryGetValue(field.Name, out string? dependencyType))
        {
            objectType = dependencyType;
        }

        if (foreignKeys.TryGetValue(field.Name, out var foreignKey))
        {
            return foreignKey with { ObjectType = objectType };
        }

        if (objectType is null)
        {
            return null;
        }

        bool wholeNumber = ImportField.GetKind(field.DataType) is ImportValueKind.Integer or ImportValueKind.LongInteger;

        return wholeNumber && resolveObjectType(objectType) is { } target
            ? new ContactFieldReference(objectType, target.TableName, target.IdColumn)
            : new ContactFieldReference(objectType);
    }

    private static (string TableName, string IdColumn)? ResolveObjectType(string objectType)
    {
        var typeInfo = ObjectTypeManager.GetTypeInfo(objectType);
        if (typeInfo is null || string.IsNullOrEmpty(typeInfo.IDColumn) || string.Equals(typeInfo.IDColumn, ObjectTypeInfo.COLUMN_NAME_UNKNOWN, StringComparison.Ordinal))
        {
            return null;
        }

        string? tableName = DataClassInfoProvider.GetDataClassInfo(typeInfo.ObjectClassName)?.ClassTableName;

        return string.IsNullOrEmpty(tableName) ? null : (tableName, typeInfo.IDColumn);
    }

    private static Dictionary<string, ContactFieldReference> GetForeignKeys(string tableName)
    {
        var foreignKeys = new Dictionary<string, ContactFieldReference>(StringComparer.OrdinalIgnoreCase);
        using var data = ConnectionHelper.ExecuteQuery(ForeignKeysSql, new QueryDataParameters { { "@table", tableName } }, QueryTypeEnum.SQLQuery);
        if (data.Tables.Count == 0)
        {
            return foreignKeys;
        }

        foreach (DataRow row in data.Tables[0].Rows)
        {
            if (row[ForeignKeyColumn] is string column && row[ForeignKeyTableColumn] is string table && row[ForeignKeyReferencedColumn] is string referencedColumn)
            {
                // A column with several foreign keys: the first one is checked, the others are left to the database.
                foreignKeys.TryAdd(column, new ContactFieldReference(null, table, referencedColumn, row[ForeignKeySchemaColumn] as string));
            }
        }

        return foreignKeys;
    }

    private static ContactFieldDefinition ToDefinition(FormFieldInfo field, ContactFieldReference? reference)
    {
        string caption = ResHelper.LocalizeString(field.Caption);

        return new ContactFieldDefinition(
            field.Name,
            string.IsNullOrWhiteSpace(caption) ? field.Name : caption,
            field.DataType,
            field.Size > 0 ? field.Size : null,
            field.Precision >= 0 && string.Equals(field.DataType, FieldDataType.Decimal, StringComparison.OrdinalIgnoreCase) ? field.Precision : null,
            reference);
    }
}
