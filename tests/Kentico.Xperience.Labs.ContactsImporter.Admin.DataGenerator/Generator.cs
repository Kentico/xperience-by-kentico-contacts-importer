using System.Diagnostics;
using System.IO.Compression;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator;

/// <summary>Result of one generator run.</summary>
public sealed record GenerationResult(string Path, long Bytes, TimeSpan Elapsed, GenerationStats Stats);

/// <summary>Writes the contact file: rows → format writer → optional gz / single-entry zip → file.</summary>
public static class Generator
{
    public static GenerationResult Run(GeneratorOptions options, TextWriter? progress = null)
    {
        options.Validate();
        string path = options.ResolveOutPath();
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);

        var stopwatch = Stopwatch.StartNew();
        var source = new ContactRowSource(options);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, RowWriters.BufferSize))
        {
            switch (options.Compress)
            {
                case Compression.Gz:
                {
                    using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true);
                    WriteRows(options, source, gzip, folder, progress);
                    break;
                }
                case Compression.Zip:
                {
                    using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                    var entry = RowWriters.CreateEntry(zip, options.ZipEntryName(path));
                    using var entryStream = entry.Open();
                    WriteRows(options, source, entryStream, folder, progress);
                    break;
                }
                case Compression.None:
                    WriteRows(options, source, file, folder, progress);
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        stopwatch.Stop();

        return new GenerationResult(path, new FileInfo(path).Length, stopwatch.Elapsed, source.Stats);
    }

    private static void WriteRows(GeneratorOptions options, ContactRowSource source, Stream output, string tempFolder, TextWriter? progress)
    {
        using var writer = RowWriters.Create(options, output, tempFolder);
        writer.WriteHeader(source.Columns);
        long step = Math.Max(options.Rows / 10, 1);
        for (long i = 1; i <= options.Rows; i++)
        {
            writer.WriteRow(source.Next());
            if (progress is not null && i % step == 0)
            {
                progress.WriteLine($"  {i:N0} / {options.Rows:N0} rows");
            }
        }
        writer.Complete();
    }
}
