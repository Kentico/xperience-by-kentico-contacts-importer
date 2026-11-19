namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>CLI entry: 0 = file written, 1 = generation failed, 2 = invalid arguments.</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            output.WriteLine(GeneratorOptions.Usage);

            return 0;
        }

        GeneratorOptions options;
        try
        {
            options = GeneratorOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine("Run with --help for usage.");

            return 2;
        }

        try
        {
            output.WriteLine($"Writing {options.Rows:N0} rows ({options.Format.ToString().ToLowerInvariant()}, compress {options.Compress.ToString().ToLowerInvariant()}) to {options.ResolveOutPath()}");
            var result = Generator.Run(options, options.Quiet ? null : error);
            var stats = result.Stats;
            output.WriteLine($"Done in {result.Elapsed.TotalSeconds:N1} s: {result.Bytes:N0} bytes ({result.Bytes / 1024d / 1024d:N1} MB)");
            output.WriteLine($"Rows {stats.Rows:N0}: new emails {stats.NewKeys:N0}, duplicates {stats.Duplicates:N0}, invalid emails {stats.InvalidEmails:N0}, empty cells {stats.EmptyCells:N0}");

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"Generation failed: {ex.Message}");

            return 1;
        }
    }
}
