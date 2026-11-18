using System.Globalization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator;

/// <summary>Cell → text for the text formats (CSV, TSV, NDJSON, JSON).</summary>
public static class TextFormatting
{
    public static string Date(DateTime value, DateFormat format) => value.ToString(format switch
    {
        DateFormat.Iso => "yyyy-MM-dd",
        DateFormat.Dmy => "dd.MM.yyyy",
        DateFormat.Mdy => "MM/dd/yyyy",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    }, CultureInfo.InvariantCulture);

    public static string DateTime(DateTime value, DateFormat format) => value.ToString(format switch
    {
        DateFormat.Iso => "yyyy-MM-ddTHH:mm:ss",
        DateFormat.Dmy => "dd.MM.yyyy HH:mm:ss",
        DateFormat.Mdy => "MM/dd/yyyy HH:mm:ss",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    }, CultureInfo.InvariantCulture);

    /// <summary>Text of a cell; <c>yes</c> / <c>no</c> for booleans, empty string for empty cells.</summary>
    public static string Format(Cell cell, DateFormat format) => cell.Kind switch
    {
        CellKind.Empty => "",
        CellKind.Text => cell.Text ?? "",
        CellKind.Date => Date(cell.Value, format),
        CellKind.DateTime => DateTime(cell.Value, format),
        CellKind.Bool => cell.Flag ? "yes" : "no",
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell.Kind, null),
    };
}
