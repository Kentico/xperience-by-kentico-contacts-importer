using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Names of the contact table and its system columns, read from the contact class and type information.
/// </summary>
internal sealed record ContactTable(string TableName, string IdColumn, string LastModifiedColumn)
{
    public static ContactTable Default { get; } = new("OM_Contact", "ContactID", "ContactLastModified");
}

/// <summary>
/// Builds the set-based SQL of the importer and its JSON parameters (PLAN §7, §13).
/// </summary>
/// <remarks>
/// Statements are inline, parameterized SQL: all data travels in one JSON parameter read with <c>OPENJSON</c>,
/// so there is no parameter count limit and no value is ever part of the SQL text. Identifiers come only from the
/// contact class definition (the mappable field allow-list) and are always quoted with <see cref="QuoteIdentifier"/>.
/// </remarks>
internal static class ContactSqlBuilder
{
    public const string KeysParameter = "@keys";
    public const string RowsParameter = "@rows";
    public const string ClearEmptyParameter = "@clearEmpty";
    public const string NowParameter = "@now";
    public const string SourceParameter = "@source";

    public const string GroupIndexColumn = "__i";
    public const string ContactIdColumn = "__id";
    public const string LastModifiedColumn = "__lm";
    public const string EquivalentIndexColumn = "__e";

    private const string TextKeySqlType = "nvarchar(450)";

    /// <summary>
    /// Quotes a SQL identifier with brackets; a closing bracket inside is doubled.
    /// </summary>
    public static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Contains('\0'))
        {
            throw new ArgumentException("The identifier is not valid.", nameof(identifier));
        }

        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>Result column holding the current value of the field with the given index.</summary>
    public static string ValueColumn(int fieldIndex) => "f" + fieldIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Lookup of existing contacts by key: one row per matching contact with the key's group index, the contact ID,
    /// last modification time and the current values of the mapped fields (except the key).
    /// </summary>
    public static string BuildLookupSql(ContactTable table, ImportProcessingContext context)
    {
        var sql = new StringBuilder();
        sql.Append("SELECT k.[i] AS ").Append(QuoteIdentifier(GroupIndexColumn))
            .Append(", c.").Append(QuoteIdentifier(table.IdColumn)).Append(" AS ").Append(QuoteIdentifier(ContactIdColumn))
            .Append(", c.").Append(QuoteIdentifier(table.LastModifiedColumn)).Append(" AS ").Append(QuoteIdentifier(LastModifiedColumn));

        foreach (var field in context.Fields)
        {
            if (field.Index != context.KeyIndex)
            {
                sql.Append(", c.").Append(QuoteIdentifier(field.Name)).Append(" AS ").Append(QuoteIdentifier(ValueColumn(field.Index)));
            }
        }

        sql.Append(" FROM OPENJSON(").Append(KeysParameter).Append(") WITH ([i] int '$.i', [k] ").Append(KeySqlType(context.KeyField)).Append(" '$.k') AS k")
            .Append(" INNER JOIN ").Append(QuoteIdentifier(table.TableName)).Append(" AS c ON c.").Append(QuoteIdentifier(context.KeyField.Name)).Append(" = k.[k]");

        return sql.ToString();
    }

    /// <summary>
    /// For every text key that the database treats as equal to an earlier key of the same list: its index and the
    /// index of the first such key. Uses the default collation of the database.
    /// </summary>
    public static string BuildEquivalentKeysSql() =>
        $"SELECT a.[i] AS {QuoteIdentifier(GroupIndexColumn)}, MIN(b.[i]) AS {QuoteIdentifier(EquivalentIndexColumn)}"
        + $" FROM OPENJSON({KeysParameter}) WITH ([i] int '$.i', [k] {TextKeySqlType} '$.k') AS a"
        + $" INNER JOIN OPENJSON({KeysParameter}) WITH ([i] int '$.i', [k] {TextKeySqlType} '$.k') AS b ON a.[k] = b.[k] AND b.[i] < a.[i]"
        + " GROUP BY a.[i]";

    /// <summary>
    /// Existence check of reference values: the index of every value in <see cref="KeysParameter"/> that has no row in
    /// the table the field references (<see cref="ImportField.Reference"/>). The table and column come from the
    /// foreign keys of the contact table or the Xperience class definitions.
    /// </summary>
    public static string BuildMissingReferencesSql(ImportField field)
    {
        if (field.Reference is not { CanBeChecked: true } reference)
        {
            throw new ArgumentException("The field is not a reference that can be checked.", nameof(field));
        }

        string table = (string.IsNullOrEmpty(reference.SchemaName) ? "" : QuoteIdentifier(reference.SchemaName) + ".") + QuoteIdentifier(reference.TableName!);

        return $"SELECT k.[i] AS {QuoteIdentifier(GroupIndexColumn)}"
            + $" FROM OPENJSON({KeysParameter}) WITH ([i] int '$.i', [k] {KeySqlType(field)} '$.k') AS k"
            + $" WHERE NOT EXISTS (SELECT 1 FROM {table} AS r WHERE r.{QuoteIdentifier(reference.ColumnName!)} = k.[k])";
    }

    /// <summary>
    /// JSON for <see cref="KeysParameter"/>: <c>[{"i":0,"k":"key"},...]</c>, <c>i</c> being the position in <paramref name="keys"/>.
    /// </summary>
    public static string BuildKeysJson(ImportField keyField, IReadOnlyList<object> keys)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (int i = 0; i < keys.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("i", i);
                FieldValueConverter.WriteJson(writer, "k", keyField, keys[i]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// The update of one batch: a single statement applying the merge rule (PLAN §7) to every contact listed in
    /// <see cref="RowsParameter"/>. Per mapped field (except the key): a row that says nothing about the field keeps
    /// the value; otherwise a value replaces it, and an empty value (NULL) keeps it or, with
    /// <see cref="ClearEmptyParameter"/> = 1, clears it. Also sets the last modification time and, when configured,
    /// the import source stamp.
    /// </summary>
    public static string BuildUpdateSql(ContactTable table, ImportProcessingContext context)
    {
        var set = new StringBuilder();
        var with = new StringBuilder("[id] int '$.id'");
        foreach (var field in context.Fields)
        {
            if (!IsUpdated(context, field))
            {
                continue;
            }

            string column = "c." + QuoteIdentifier(field.Name);
            string value = "v." + QuoteIdentifier(ValueProperty(field.Index));
            string absent = "v." + QuoteIdentifier(AbsentProperty(field.Index));
            set.Append(column).Append(" = CASE WHEN ").Append(absent).Append(" = 1 THEN ").Append(column)
                .Append(" WHEN ").Append(ClearEmptyParameter).Append(" = 1 THEN ").Append(value)
                .Append(" ELSE COALESCE(").Append(value).Append(", ").Append(column).Append(") END, ");
            with.Append(", ").Append(QuoteIdentifier(ValueProperty(field.Index))).Append(' ').Append(field.SqlType).Append(" '$.").Append(ValueProperty(field.Index)).Append('\'')
                .Append(", ").Append(QuoteIdentifier(AbsentProperty(field.Index))).Append(" bit '$.").Append(AbsentProperty(field.Index)).Append('\'');
        }

        if (context.StampUpdatedContacts)
        {
            set.Append("c.").Append(QuoteIdentifier(context.SourceField!)).Append(" = ").Append(SourceParameter).Append(", ");
        }

        return $"UPDATE c SET {set}c.{QuoteIdentifier(table.LastModifiedColumn)} = {NowParameter}"
            + $" FROM {QuoteIdentifier(table.TableName)} AS c"
            + $" INNER JOIN OPENJSON({RowsParameter}) WITH ({with}) AS v ON c.{QuoteIdentifier(table.IdColumn)} = v.[id]";
    }

    /// <summary>
    /// JSON for <see cref="RowsParameter"/>: one object per contact, <c>{"id":5,"v1":"Jane","a2":1}</c>.
    /// <c>v{n}</c> = value of field n (left out when empty), <c>a{n}</c> = 1 when the row says nothing about field n.
    /// </summary>
    public static string BuildUpdateJson(ImportProcessingContext context, IReadOnlyList<ContactUpdate> updates)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var update in updates)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", update.ContactId);
                foreach (var field in context.Fields)
                {
                    if (!IsUpdated(context, field))
                    {
                        continue;
                    }

                    switch (update.States[field.Index])
                    {
                        case FieldValueState.Value:
                            FieldValueConverter.WriteJson(writer, ValueProperty(field.Index), field, update.Values[field.Index]!);
                            break;
                        case FieldValueState.Absent:
                            writer.WriteNumber(AbsentProperty(field.Index), 1);
                            break;
                        case FieldValueState.Empty:
                        default:
                            break;
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// The key is what matched, so it is never rewritten; the source field is written by the stamp when updated contacts are stamped.
    /// </summary>
    private static bool IsUpdated(ImportProcessingContext context, ImportField field) =>
        field.Index != context.KeyIndex
        && !(context.StampUpdatedContacts && string.Equals(field.Name, context.SourceField, StringComparison.Ordinal));

    private static string KeySqlType(ImportField keyField) => keyField.Kind == ImportValueKind.Text ? TextKeySqlType : keyField.SqlType;

    private static string ValueProperty(int fieldIndex) => "v" + fieldIndex.ToString(CultureInfo.InvariantCulture);

    private static string AbsentProperty(int fieldIndex) => "a" + fieldIndex.ToString(CultureInfo.InvariantCulture);
}
