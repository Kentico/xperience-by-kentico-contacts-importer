using System.Globalization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// An example of a value the import changes in an existing contact, collected by the analysis (PLAN D57) so the
/// Review step can show what changes. Personal data (key and values): stored with the uploaded data and deleted with
/// it (<see cref="IImportJobStore.WriteSamplePart"/>), never logged.
/// </summary>
internal sealed record ImportValueSample
{
    /// <summary>Contact field (database column) name.</summary>
    public required string Field { get; init; }

    /// <summary>The import clears the value (an empty value in the file and empty values clear fields).</summary>
    public bool Cleared { get; init; }

    /// <summary>
    /// Source file row number: the last row with the key, the row the report lists as updated.
    /// </summary>
    public long Row { get; init; }

    /// <summary>Normalized key of the row (for example the email), so the marketer can find the contact.</summary>
    public string Key { get; init; } = "";

    /// <summary>Current value of the contact as text; <c>null</c> when it has none.</summary>
    public string? Current { get; init; }

    /// <summary>Value after the import as text; <c>null</c> when the value is cleared.</summary>
    public string? New { get; init; }
}

/// <summary>
/// Rules of the value examples: at most <see cref="PerField"/> changed and <see cref="PerField"/> cleared examples per
/// field, the ones with the lowest row numbers. Any set of batches, in any order and processed any number of times,
/// gives the same examples, so a batch processed again after a lost checkpoint changes nothing.
/// </summary>
internal static class ImportValueSamples
{
    /// <summary>Examples per field and kind (changed, cleared).</summary>
    public const int PerField = 3;

    /// <summary>Longest value text stored; longer values are cut and end with an ellipsis.</summary>
    public const int MaxValueLength = 200;

    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string DateTimeMinutesFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// The examples to show from <paramref name="samples"/>: per field and kind the lowest rows, one example per row,
    /// ordered by field, then row.
    /// </summary>
    public static IReadOnlyList<ImportValueSample> Select(IEnumerable<ImportValueSample> samples)
    {
        var set = new ImportValueSampleSet();
        foreach (var sample in samples)
        {
            set.Add(sample);
        }

        return set.ToList();
    }

    /// <summary>
    /// The stored examples of the analysis of a job (<see cref="Select"/> over all parts). Parts that cannot be read
    /// are left out.
    /// </summary>
    public static IReadOnlyList<ImportValueSample> Read(IImportJobStore store, Guid jobId)
    {
        var samples = new List<ImportValueSample>();
        foreach (long firstRow in store.ListSampleParts(jobId))
        {
            try
            {
                samples.AddRange(store.ReadSamplePart(jobId, firstRow) ?? []);
            }
            catch (InvalidDataException)
            {
                // Examples only: a part being replaced or damaged is skipped.
            }
        }

        return Select(samples);
    }

    /// <summary>
    /// Text of a value for an example: dates as <c>yyyy-MM-dd</c>, date and time as <c>yyyy-MM-dd HH:mm:ss</c> (without
    /// the seconds when they are 0; a date and time at midnight, such as a birthday stored as date and time, as the date only),
    /// numbers in the invariant culture, yes/no as <c>true</c> / <c>false</c>. <c>null</c> for an empty value.
    /// </summary>
    public static string? Format(ImportField field, object? value)
    {
        if (FieldValueConverter.IsEmpty(value))
        {
            return null;
        }

        string text;
        try
        {
            text = field.Kind switch
            {
                ImportValueKind.Date => Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString(DateFormat, CultureInfo.InvariantCulture),
                ImportValueKind.DateTime => FormatDateTime(Convert.ToDateTime(value, CultureInfo.InvariantCulture)),
                ImportValueKind.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "true" : "false",
                ImportValueKind.Text or ImportValueKind.Integer or ImportValueKind.LongInteger or ImportValueKind.Double
                    or ImportValueKind.Decimal or ImportValueKind.Guid => FieldValueConverter.ToKeyText(value!),
                _ => FieldValueConverter.ToKeyText(value!),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            text = FieldValueConverter.ToKeyText(value!);
        }

        return Truncate(text);
    }

    private static string FormatDateTime(DateTime value)
    {
        if (value.TimeOfDay == TimeSpan.Zero)
        {
            return value.ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        string format = value.Second == 0 && value.Millisecond == 0 ? DateTimeMinutesFormat : DateTimeFormat;

        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>Cuts a text to <see cref="MaxValueLength"/> characters (without splitting a surrogate pair).</summary>
    public static string Truncate(string text)
    {
        if (text.Length <= MaxValueLength)
        {
            return text;
        }

        int length = MaxValueLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length), "…");
    }
}

/// <summary>
/// The value examples of an analysis, by field and kind: keeps the <see cref="ImportValueSamples.PerField"/> lowest
/// rows, one example per row.
/// </summary>
internal sealed class ImportValueSampleSet
{
    private readonly Dictionary<(string Field, bool Cleared), List<ImportValueSample>> slots = [];

    /// <summary>Number of examples in the set.</summary>
    public int Count => slots.Values.Sum(list => list.Count);

    /// <summary>
    /// Whether an example of <paramref name="field"/> from <paramref name="row"/> would be kept. Lets callers build the
    /// example (format its values) only when it is needed.
    /// </summary>
    public bool Wants(string field, bool cleared, long row)
    {
        if (!slots.TryGetValue((field, cleared), out var list))
        {
            return true;
        }

        if (list.Exists(sample => sample.Row == row))
        {
            return false;
        }

        return list.Count < ImportValueSamples.PerField || row < list[^1].Row;
    }

    /// <summary>Adds an example; returns whether it was kept. An example with a higher row may drop out.</summary>
    public bool Add(ImportValueSample sample)
    {
        if (!Wants(sample.Field, sample.Cleared, sample.Row))
        {
            return false;
        }

        if (!slots.TryGetValue((sample.Field, sample.Cleared), out var list))
        {
            list = [];
            slots.Add((sample.Field, sample.Cleared), list);
        }

        int index = list.FindIndex(existing => existing.Row > sample.Row);
        list.Insert(index < 0 ? list.Count : index, sample);
        if (list.Count > ImportValueSamples.PerField)
        {
            list.RemoveAt(list.Count - 1);
        }

        return true;
    }

    /// <summary>Examples ordered by field, cleared before changed, then row.</summary>
    public List<ImportValueSample> ToList() =>
        [.. slots.OrderBy(slot => slot.Key.Field, StringComparer.Ordinal).ThenBy(slot => !slot.Key.Cleared).SelectMany(slot => slot.Value)];
}
