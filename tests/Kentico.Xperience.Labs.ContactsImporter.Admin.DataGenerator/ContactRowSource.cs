namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator;

public enum CellKind
{
    Empty,
    Text,
    Date,
    DateTime,
    Bool,
}

/// <summary>One value of a generated row. Writers format it for their file type.</summary>
public readonly record struct Cell(CellKind Kind, string? Text = null, DateTime Value = default, bool Flag = false)
{
    public static readonly Cell Empty = new(CellKind.Empty);

    public static Cell Of(string text) => new(CellKind.Text, text);
}

/// <summary>Counts of what the generator wrote.</summary>
public sealed class GenerationStats
{
    public long Rows { get; set; }
    public long NewKeys { get; set; }
    public long Duplicates { get; set; }
    public long InvalidEmails { get; set; }
    public long EmptyCells { get; set; }
}

/// <summary>
/// Deterministic contact rows. Output depends only on the options (seed included), never on the file
/// format, so a CSV and an XLSX of the same seed hold the same data.
/// <list type="bullet">
/// <item>Every row is one of: new email (next key), duplicate (email of an earlier key of this run), invalid email.</item>
/// <item>Email and names are derived from the key number only (not the seed), so runs with overlapping <c>--key-offset</c> ranges share emails + names.</item>
/// <item>Other values come from the seeded random generator; duplicates get new values (merge tests: last non-empty wins).</item>
/// </list>
/// </summary>
public sealed class ContactRowSource
{
    public const int EmailColumn = 0;

    private static readonly string[] firstNames =
    [
        "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth",
        "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen",
        "Daniel", "Nancy", "Matthew", "Lisa", "Anthony", "Betty", "Mark", "Sandra", "Paul", "Ashley",
        "José", "Zoë", "François", "Søren", "Łukasz", "Jiří", "Ángel", "Björn", "Chloé", "Mei",
    ];

    private static readonly string[] lastNames =
    [
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
        "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin",
        "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson",
        "Müller", "Novák", "O'Brien", "Dvořák", "Kowalski", "Nguyễn", "Øster", "Fernández", "Schmidt", "Rossi",
    ];

    private static readonly string[] companies =
    [
        "Acme Corp", "Globex", "Initech", "Umbrella Ltd", "Stark Industries", "Wayne Enterprises", "Hooli",
        "Smith, Jones & Co", "The \"Best\" Coffee", "Dancing Goat", "Contoso", "Fabrikam", "Northwind Traders",
        "Tailspin Toys", "Wide World Importers", "Adventure Works", "Blue Yonder Airlines", "Café Müller GmbH",
    ];

    private static readonly string[] jobTitles =
    [
        "Marketing Manager", "Sales Representative", "CEO", "CTO", "Developer", "Designer", "Product Owner",
        "Account Executive", "Support Specialist", "Data Analyst", "Content Editor", "HR Generalist",
        "Office Manager", "Consultant", "Barista",
    ];

    private static readonly string[] cities =
    [
        "New York", "London", "Brno", "Prague", "Berlin", "Paris", "Madrid", "Rome", "Sydney", "Toronto",
        "Chicago", "Austin", "Seattle", "Amsterdam", "Vienna", "Zürich", "København", "Kraków", "São Paulo", "Tokyo",
    ];

    private static readonly string[] domains = ["example.com", "example.org", "example.net", "mail.example.com"];

    private static readonly string[] words =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet",
        "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango",
    ];

    private static readonly DateTime birthdayStart = new(1940, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private const int BirthdayDays = 24_000;
    private static readonly DateTime consentStart = new(2018, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private const int ConsentSeconds = 8 * 365 * 24 * 3600;

    private readonly GeneratorOptions options;
    private readonly Random random;
    private readonly Cell[] cells;
    private long nextKey;
    private long rowIndex;

    public ContactRowSource(GeneratorOptions options)
    {
        this.options = options;
        random = new Random(options.Seed);
        Columns = BuildColumns(options);
        cells = new Cell[Columns.Count];
    }

    public IReadOnlyList<string> Columns { get; }

    public GenerationStats Stats { get; } = new();

    public static IReadOnlyList<string> BuildColumns(GeneratorOptions options)
    {
        var columns = new List<string> { "Email", "FirstName", "LastName", "Company", "JobTitle", "City", "Phone", "Birthday" };
        for (int i = 1; i <= options.CustomColumns; i++)
        {
            columns.Add($"Custom{i}");
        }
        if (options.ConsentColumns)
        {
            columns.Add("ConsentAgreed");
            columns.Add("ConsentDate");
        }

        return columns;
    }

    /// <summary>Email of a key number (0-based within the run; <c>--key-offset</c> added).</summary>
    public string EmailOf(long key)
    {
        long number = key + options.KeyOffset;
        (string first, string last) = NamesOf(number);

        return $"{Ascii(first)}.{Ascii(last)}.{number}@{domains[(int)(Mix(number) >> 8) & 3]}";
    }

    /// <summary>
    /// Next row. The returned array is reused by the next call.
    /// </summary>
    public Cell[] Next()
    {
        double kindRoll = random.NextDouble() * 100;
        long key;
        if (kindRoll < options.InvalidEmailsPercent)
        {
            key = -1;
        }
        else if (kindRoll < options.InvalidEmailsPercent + options.DuplicatesPercent && nextKey > 0)
        {
            key = random.NextInt64(nextKey);
            Stats.Duplicates++;
        }
        else
        {
            key = nextKey++;
            Stats.NewKeys++;
        }

        long nameNumber = key >= 0 ? key + options.KeyOffset : rowIndex + options.KeyOffset;
        (string first, string last) = NamesOf(nameNumber);
        if (key >= 0)
        {
            cells[EmailColumn] = Cell.Of(EmailOf(key));
        }
        else
        {
            cells[EmailColumn] = Cell.Of(InvalidEmail(first, last, rowIndex));
            Stats.InvalidEmails++;
        }

        int c = 1;
        cells[c++] = Maybe(Cell.Of(first));
        cells[c++] = Maybe(Cell.Of(last));
        cells[c++] = Maybe(Cell.Of(companies[random.Next(companies.Length)]));
        cells[c++] = Maybe(Cell.Of(jobTitles[random.Next(jobTitles.Length)]));
        cells[c++] = Maybe(Cell.Of(cities[random.Next(cities.Length)]));
        cells[c++] = Maybe(Cell.Of($"+1 555-{random.Next(10_000):0000}"));
        cells[c++] = Maybe(new Cell(CellKind.Date, Value: birthdayStart.AddDays(random.Next(BirthdayDays))));
        for (int i = 0; i < options.CustomColumns; i++)
        {
            cells[c++] = Maybe(Cell.Of($"{words[random.Next(words.Length)]} {words[random.Next(words.Length)]} {random.Next(100)}"));
        }
        if (options.ConsentColumns)
        {
            bool agreed = random.Next(3) > 0;
            var date = consentStart.AddSeconds(random.Next(ConsentSeconds));
            cells[c++] = Maybe(new Cell(CellKind.Bool, Flag: agreed));
            cells[c] = agreed ? Maybe(new Cell(CellKind.DateTime, Value: date)) : Cell.Empty;
        }

        rowIndex++;
        Stats.Rows++;

        return cells;
    }

    private Cell Maybe(Cell cell)
    {
        double roll = random.NextDouble() * 100;
        if (roll < options.EmptyValuesPercent)
        {
            Stats.EmptyCells++;

            return Cell.Empty;
        }

        return cell;
    }

    private static string InvalidEmail(string first, string last, long row) => (row % 4) switch
    {
        0 => $"{Ascii(first)}.{Ascii(last)}.{row}(at)example.com",
        1 => $"{Ascii(first)}.{Ascii(last)}.{row}@",
        2 => $"{Ascii(first)} {Ascii(last)} {row}",
        _ => $"{Ascii(first)}.{Ascii(last)}.{row}@@example.com",
    };

    private static (string First, string Last) NamesOf(long number)
    {
        ulong hash = Mix(number);

        return (firstNames[(int)(hash % (ulong)firstNames.Length)], lastNames[(int)((hash >> 16) % (ulong)lastNames.Length)]);
    }

    /// <summary>SplitMix64 finalizer: stable across runtimes (unlike <see cref="string.GetHashCode()"/>).</summary>
    private static ulong Mix(long value)
    {
        ulong z = (ulong)value + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

        return z ^ (z >> 31);
    }

    /// <summary>Lowercase ASCII letters of a name for the email local part (<c>Zoë</c> → <c>zo</c>, <c>O'Brien</c> → <c>obrien</c>).</summary>
    private static string Ascii(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int length = 0;
        foreach (char ch in name)
        {
            if (char.IsAsciiLetter(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
            }
        }

        return length == 0 ? "x" : new string(buffer[..length]);
    }
}
