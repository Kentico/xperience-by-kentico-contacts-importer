using System.Globalization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator;

public enum OutputFormat
{
    Csv,
    Tsv,
    Ndjson,
    Json,
    Xlsx,
}

public enum Compression
{
    None,
    Gz,
    Zip,
}

public enum DateFormat
{
    Iso,
    Dmy,
    Mdy,
}

/// <summary>Command line options of the generator.</summary>
public sealed record GeneratorOptions
{
    /// <summary>Excel sheet row limit (header row included).</summary>
    public const int XlsxMaxRows = 1_048_576;

    public long Rows { get; init; } = 10_000;
    public OutputFormat Format { get; init; } = OutputFormat.Csv;
    public Compression Compress { get; init; } = Compression.None;
    public string? Out { get; init; }
    public int Seed { get; init; } = 1;
    public double DuplicatesPercent { get; init; }
    public double InvalidEmailsPercent { get; init; }
    public double EmptyValuesPercent { get; init; }
    public int CustomColumns { get; init; }
    public bool ConsentColumns { get; init; }
    public DateFormat DateFormat { get; init; } = DateFormat.Iso;
    public long KeyOffset { get; init; }
    public bool XlsxSharedStrings { get; init; } = true;
    public bool Quiet { get; init; }

    public string Extension => Format switch
    {
        OutputFormat.Csv => ".csv",
        OutputFormat.Tsv => ".tsv",
        OutputFormat.Ndjson => ".ndjson",
        OutputFormat.Json => ".json",
        OutputFormat.Xlsx => ".xlsx",
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Output path: <see cref="Out"/> as given, or <c>contacts-{rows}{ext}[.gz|.zip]</c> in the current folder.</summary>
    public string ResolveOutPath()
    {
        if (!string.IsNullOrWhiteSpace(Out))
        {
            return Path.GetFullPath(Out);
        }

        string suffix = Compress switch
        {
            Compression.Gz => ".gz",
            Compression.Zip => ".zip",
            Compression.None => "",
            _ => throw new InvalidOperationException(),
        };

        return Path.GetFullPath($"contacts-{Rows}{Extension}{suffix}");
    }

    /// <summary>Name of the single data entry of a <c>.zip</c> output: the output name without <c>.zip</c>, with the format extension.</summary>
    public string ZipEntryName(string outPath)
    {
        string name = Path.GetFileName(outPath);
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : $"contacts{Extension}";
    }

    public const string Usage = """
        Usage: DataGenerator [options]

          --rows <n>              data rows (default 10000)
          --format <f>            csv | tsv | ndjson | json | xlsx (default csv)
          --compress <c>          none | gz | zip (default none; gz not for xlsx)
          --out <path>            output file (default contacts-<rows>.<ext>[.gz|.zip])
          --seed <int>            random seed; same options + seed = same bytes (default 1)
          --duplicates <pct>      % of rows that repeat an earlier email (default 0)
          --invalid-emails <pct>  % of rows with an invalid email (default 0)
          --empty-values <pct>    % of non-email cells left empty (default 0)
          --custom-columns <n>    extra text columns Custom1..CustomN (default 0)
          --consent-columns       add ConsentAgreed (yes/no) + ConsentDate columns
          --date-format <f>       iso | dmy | mdy for text formats (default iso; xlsx uses date cells)
          --key-offset <n>        first email number (default 0); runs with overlapping
                                  ranges share emails, e.g. offset 0 and offset 500000
          --xlsx-shared-strings <true|false>  shared strings table (default true) or inline strings
          --quiet                 no progress output
          --help                  this text
        """;

    /// <summary>Parses command line arguments. Throws <see cref="ArgumentException"/> with a user-facing message.</summary>
    public static GeneratorOptions Parse(IReadOnlyList<string> args)
    {
        var options = new GeneratorOptions();
        var queue = new Queue<string>(args);
        while (queue.TryDequeue(out string? name))
        {
            string Value() => queue.TryDequeue(out string? value) ? value : throw new ArgumentException($"Missing value for {name}.");

            options = name switch
            {
                "--rows" => options with { Rows = ParseLong(name, Value(), 1) },
                "--format" => options with { Format = ParseEnum<OutputFormat>(name, Value()) },
                "--compress" => options with { Compress = ParseEnum<Compression>(name, Value()) },
                "--out" => options with { Out = Value() },
                "--seed" => options with { Seed = (int)ParseLong(name, Value(), int.MinValue, int.MaxValue) },
                "--duplicates" => options with { DuplicatesPercent = ParsePercent(name, Value()) },
                "--invalid-emails" => options with { InvalidEmailsPercent = ParsePercent(name, Value()) },
                "--empty-values" => options with { EmptyValuesPercent = ParsePercent(name, Value()) },
                "--custom-columns" => options with { CustomColumns = (int)ParseLong(name, Value(), 0, 1000) },
                "--consent-columns" => options with { ConsentColumns = true },
                "--date-format" => options with { DateFormat = ParseEnum<DateFormat>(name, Value()) },
                "--key-offset" => options with { KeyOffset = ParseLong(name, Value(), 0) },
                "--xlsx-shared-strings" => options with { XlsxSharedStrings = ParseBool(name, Value()) },
                "--quiet" => options with { Quiet = true },
                _ => throw new ArgumentException($"Unknown option {name}."),
            };
        }

        options.Validate();

        return options;
    }

    public void Validate()
    {
        if (Rows < 1)
        {
            throw new ArgumentException("--rows must be at least 1.");
        }
        if (Format == OutputFormat.Xlsx && Rows + 1 > XlsxMaxRows)
        {
            throw new ArgumentException($"--rows for xlsx is at most {XlsxMaxRows - 1} (Excel sheet limit {XlsxMaxRows} rows incl. header).");
        }
        if (Format == OutputFormat.Xlsx && Compress == Compression.Gz)
        {
            throw new ArgumentException("--compress gz is not supported for xlsx (use zip).");
        }
        if (DuplicatesPercent + InvalidEmailsPercent > 100)
        {
            throw new ArgumentException("--duplicates + --invalid-emails must be at most 100.");
        }
    }

    private static long ParseLong(string name, string value, long min, long max = long.MaxValue)
    {
        string digits = value.Replace("_", "");
        if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) || result < min || result > max)
        {
            throw new ArgumentException($"{name} must be an integer between {min} and {max}.");
        }

        return result;
    }

    private static double ParsePercent(string name, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || result < 0 || result > 100)
        {
            throw new ArgumentException($"{name} must be a number between 0 and 100.");
        }

        return result;
    }

    private static bool ParseBool(string name, string value) => value.ToLowerInvariant() switch
    {
        "true" or "on" or "yes" or "1" => true,
        "false" or "off" or "no" or "0" => false,
        _ => throw new ArgumentException($"{name} must be true or false."),
    };

    private static T ParseEnum<T>(string name, string value)
        where T : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: true, out T result) || !Enum.IsDefined(result) || int.TryParse(value, out _))
        {
            throw new ArgumentException($"{name} must be one of: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
        }

        return result;
    }
}
