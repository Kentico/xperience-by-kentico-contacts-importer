using System.Globalization;
using System.Text.Json;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Converts raw file values (trimmed strings) to typed values per field data type, compares them with database
/// values, and writes them as JSON for <c>OPENJSON</c>.
/// </summary>
/// <remarks>
/// Typed values are: <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="double"/>, <see cref="decimal"/>,
/// <see cref="DateTime"/> (date and time or date only), <see cref="bool"/> and <see cref="Guid"/>.
/// </remarks>
internal static class FieldValueConverter
{
    private const string SqlDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    private const string SqlDateFormat = "yyyy-MM-dd";

    // The browser sends dates as yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss[.fraction][Z|+HH:mm].
    private static readonly string[] dateTimeFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>
    /// Converts a non-empty raw value. Returns <c>false</c> when the value does not fit the field.
    /// </summary>
    public static bool TryConvert(ImportField field, string raw, out object value)
    {
        value = raw;
        switch (field.Kind)
        {
            case ImportValueKind.Text:
                return field.MaxLength is not { } maxLength || raw.Length <= maxLength;

            case ImportValueKind.Integer:
                if (int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int intValue))
                {
                    value = intValue;
                    return true;
                }

                return false;

            case ImportValueKind.LongInteger:
                if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long longValue))
                {
                    value = longValue;
                    return true;
                }

                return false;

            case ImportValueKind.Double:
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue) && double.IsFinite(doubleValue))
                {
                    value = doubleValue;
                    return true;
                }

                return false;

            case ImportValueKind.Decimal:
                return TryConvertDecimal(field, raw, out value);

            case ImportValueKind.DateTime:
            case ImportValueKind.Date:
                return TryConvertDateTime(raw, field.Kind == ImportValueKind.Date, out value);

            case ImportValueKind.Boolean:
                return TryConvertBoolean(raw, out value);

            case ImportValueKind.Guid:
                if (Guid.TryParse(raw, out var guidValue))
                {
                    value = guidValue;
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// A database value that counts as empty: NULL or an empty string.
    /// </summary>
    public static bool IsEmpty(object? databaseValue) => databaseValue is null or DBNull or "";

    /// <summary>
    /// Whether a database value equals a typed file value. Text is compared ordinally, so a change of letter case is a change.
    /// </summary>
    public static bool AreEqual(ImportField field, object? databaseValue, object value)
    {
        if (IsEmpty(databaseValue))
        {
            return false;
        }

        try
        {
            return field.Kind switch
            {
                ImportValueKind.Text => string.Equals(Convert.ToString(databaseValue, CultureInfo.InvariantCulture), (string)value, StringComparison.Ordinal),
                ImportValueKind.Integer => Convert.ToInt64(databaseValue, CultureInfo.InvariantCulture) == (int)value,
                ImportValueKind.LongInteger => Convert.ToInt64(databaseValue, CultureInfo.InvariantCulture) == (long)value,
                ImportValueKind.Double => Convert.ToDouble(databaseValue, CultureInfo.InvariantCulture).CompareTo((double)value) == 0,
                ImportValueKind.Decimal => Convert.ToDecimal(databaseValue, CultureInfo.InvariantCulture) == (decimal)value,
                ImportValueKind.DateTime or ImportValueKind.Date => Convert.ToDateTime(databaseValue, CultureInfo.InvariantCulture).Ticks == ((DateTime)value).Ticks,
                ImportValueKind.Boolean => Convert.ToBoolean(databaseValue, CultureInfo.InvariantCulture) == (bool)value,
                ImportValueKind.Guid => databaseValue is Guid guid ? guid == (Guid)value : Guid.TryParse(Convert.ToString(databaseValue, CultureInfo.InvariantCulture), out var parsed) && parsed == (Guid)value,
                _ => false,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // The column holds something the field type does not describe: treat as different, the update then writes the file value.
            return false;
        }
    }

    /// <summary>
    /// Writes a typed value as a JSON property in the form the field's <see cref="ImportField.SqlType"/> reads through <c>OPENJSON ... WITH</c>.
    /// </summary>
    public static void WriteJson(Utf8JsonWriter writer, string propertyName, ImportField field, object value)
    {
        switch (field.Kind)
        {
            case ImportValueKind.Text:
                writer.WriteString(propertyName, (string)value);
                break;
            case ImportValueKind.Integer:
                writer.WriteNumber(propertyName, (int)value);
                break;
            case ImportValueKind.LongInteger:
                writer.WriteNumber(propertyName, (long)value);
                break;
            case ImportValueKind.Double:
                writer.WriteNumber(propertyName, (double)value);
                break;
            case ImportValueKind.Decimal:
                writer.WriteString(propertyName, ((decimal)value).ToString(CultureInfo.InvariantCulture));
                break;
            case ImportValueKind.DateTime:
                writer.WriteString(propertyName, ((DateTime)value).ToString(SqlDateTimeFormat, CultureInfo.InvariantCulture));
                break;
            case ImportValueKind.Date:
                writer.WriteString(propertyName, ((DateTime)value).ToString(SqlDateFormat, CultureInfo.InvariantCulture));
                break;
            case ImportValueKind.Boolean:
                writer.WriteNumber(propertyName, (bool)value ? 1 : 0);
                break;
            case ImportValueKind.Guid:
                writer.WriteString(propertyName, ((Guid)value).ToString("D"));
                break;
            default:
                throw new InvalidOperationException($"Unknown value kind {field.Kind}.");
        }
    }

    /// <summary>
    /// Text that identifies a typed key value: two rows with the same text have the same key.
    /// </summary>
    public static string ToKeyText(object value) => value switch
    {
        string text => text,
        Guid guid => guid.ToString("D"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool TryConvertDecimal(ImportField field, string raw, out object value)
    {
        value = raw;
        if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return false;
        }

        // SQL Server rounds to the column scale the same way (half away from zero).
        decimal rounded = Math.Round(parsed, Math.Min(field.DecimalScale, 28), MidpointRounding.AwayFromZero);
        int integerDigits = field.DecimalDigits - field.DecimalScale;
        if (integerDigits < 28 && Math.Abs(decimal.Truncate(rounded)) >= Pow10(integerDigits))
        {
            return false;
        }

        value = rounded;

        return true;
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    private static bool TryConvertDateTime(string raw, bool dateOnly, out object value)
    {
        value = raw;

        // No time zone conversion: the wall-clock time of the file is stored, an offset is dropped.
        if (!DateTimeOffset.TryParseExact(raw, dateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        var wallClock = DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified);
        value = dateOnly ? wallClock.Date : wallClock;

        return true;
    }

    private static bool TryConvertBoolean(string raw, out object value)
    {
        value = raw;
        if (raw is "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (raw is "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        return false;
    }
}
