using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// <see cref="IImportFileSystem"/> over System.IO. CMS.IO needs an initialized Xperience application,
/// so unit tests run the store against this implementation in a temporary directory.
/// </summary>
internal sealed class PhysicalImportFileSystem : IImportFileSystem
{
    public string ResolveRoot(string storagePath) => storagePath;

    public string Combine(string path, string name) => Path.Combine(path, name);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public void WriteAllBytes(string path, ReadOnlySpan<byte> content) => File.WriteAllBytes(path, content);

    public void ReplaceFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: true);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public IReadOnlyList<string> GetFileNames(string path) =>
        Directory.Exists(path) ? [.. Directory.GetFiles(path).Select(f => Path.GetFileName(f))] : [];

    public IReadOnlyList<(string Name, long Length)> GetFileSizes(string path) =>
        Directory.Exists(path) ? [.. new DirectoryInfo(path).GetFiles().Select(file => (file.Name, file.Length))] : [];

    public IReadOnlyList<string> GetDirectoryNames(string path) =>
        Directory.Exists(path) ? [.. Directory.GetDirectories(path).Select(d => Path.GetFileName(d))] : [];

    public DateTime? GetDirectoryLastWriteTimeUtc(string path) =>
        Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : null;
}

/// <summary>
/// File system that runs test code at exact points of another component's file operations, so tests can place the
/// write of a second application instance between the read and the write of the first one without threads or timing.
/// Hooks get the file name (not the path).
/// </summary>
internal sealed class HookedImportFileSystem(IImportFileSystem inner) : IImportFileSystem
{
    /// <summary>Runs after a file was read completely.</summary>
    public Action<string>? AfterRead { get; set; }

    /// <summary>Runs before a file is moved over the named destination file.</summary>
    public Action<string>? BeforeReplace { get; set; }

    /// <summary>Runs after a file was moved over the named destination file.</summary>
    public Action<string>? AfterReplace { get; set; }

    /// <summary>Number of times each destination file name was replaced.</summary>
    public Dictionary<string, int> Replaced { get; } = [];

    /// <summary>
    /// A hook that runs <paramref name="action"/> the <paramref name="occurrence"/>-th time it is called for <paramref name="fileName"/>.
    /// </summary>
    public static Action<string> On(string fileName, Action action, int occurrence = 1)
    {
        int calls = 0;

        return name =>
        {
            if (name == fileName && ++calls == occurrence)
            {
                action();
            }
        };
    }

    public string ResolveRoot(string storagePath) => inner.ResolveRoot(storagePath);

    public string Combine(string path, string name) => inner.Combine(path, name);

    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public byte[] ReadAllBytes(string path)
    {
        byte[] content = inner.ReadAllBytes(path);
        AfterRead?.Invoke(Path.GetFileName(path));

        return content;
    }

    public Stream OpenRead(string path) => inner.OpenRead(path);

    public void WriteAllBytes(string path, ReadOnlySpan<byte> content) => inner.WriteAllBytes(path, content);

    public void ReplaceFile(string sourcePath, string destinationPath)
    {
        string name = Path.GetFileName(destinationPath);
        BeforeReplace?.Invoke(name);
        inner.ReplaceFile(sourcePath, destinationPath);
        Replaced[name] = Replaced.GetValueOrDefault(name) + 1;
        AfterReplace?.Invoke(name);
    }

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public IReadOnlyList<string> GetFileNames(string path) => inner.GetFileNames(path);

    public IReadOnlyList<(string Name, long Length)> GetFileSizes(string path) => inner.GetFileSizes(path);

    public IReadOnlyList<string> GetDirectoryNames(string path) => inner.GetDirectoryNames(path);

    public DateTime? GetDirectoryLastWriteTimeUtc(string path) => inner.GetDirectoryLastWriteTimeUtc(path);
}

internal sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

    public void Advance(TimeSpan time) => UtcNow += time;
}

internal sealed class FakeContactFieldSchemaProvider(params ContactFieldDefinition[] fields) : IContactFieldSchemaProvider
{
    public IReadOnlyList<ContactFieldDefinition> Fields { get; set; } = fields;

    public IReadOnlyList<ContactFieldDefinition> GetMappableFields() => Fields;
}

/// <summary>
/// Temporary storage root with a store on it; deleted on dispose.
/// </summary>
internal sealed class TempStore : IDisposable
{
    public TempStore(Action<ContactsImporterOptions>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "contacts-importer-tests", Guid.NewGuid().ToString("N"));
        var settings = new ContactsImporterOptions { StoragePath = Root };
        configure?.Invoke(settings);
        Options = Microsoft.Extensions.Options.Options.Create(settings);
        Store = new ImportJobStore(new PhysicalImportFileSystem(), Options);
    }

    public string Root { get; }

    public IOptions<ContactsImporterOptions> Options { get; }

    public ImportJobStore Store { get; }

    /// <summary>
    /// Another store over the same folder, as a second application instance of a web farm has it.
    /// </summary>
    public ImportJobStore NewStore(IImportFileSystem? fileSystem = null) => new(fileSystem ?? new PhysicalImportFileSystem(), Options);

    public string JobPath(Guid jobId, params string[] parts) => Path.Combine([Root, jobId.ToString("D"), .. parts]);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal static class TestData
{
    public static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    public static readonly ContactFieldDefinition[] Fields =
    [
        new("ContactEmail", "Email", "text", 254),
        new("ContactFirstName", "First name", "text", 100),
        new("ContactLastName", "Last name", "text", 100),
        new("ContactNotes", "Notes", "longtext", null),
        new("ContactBirthday", "Birthday", "datetime", null),
        new("CustomerID", "Customer ID", "text", 50),
        new("ContactSource", "Source", "text", 200),
    ];

    public static ImportJob Job(Guid? jobId = null, string keyField = "ContactEmail", params string[] targets)
    {
        if (targets.Length == 0)
        {
            targets = ["ContactEmail", "ContactFirstName"];
        }

        return new ImportJob
        {
            JobId = jobId ?? Guid.NewGuid(),
            CreatedByUserId = 53,
            CreatedUtc = Now,
            File = new ImportFileDescriptor { Name = "contacts.csv", Size = 1234, Fingerprint = "0a1b2c" },
            KeyField = keyField,
            Mapping = [.. targets.Select(t => new ImportFieldMapping { Source = "col " + t, Target = t })],
        };
    }

    public static string Record(long row, string email, string firstName = "Jane") =>
        "{\"r\":" + row + ",\"k\":\"" + email + "\",\"v\":{\"ContactEmail\":\"" + email + "\",\"ContactFirstName\":\"" + firstName + "\"}}";

    public static byte[] Gzip(string text) => Gzip(Encoding.UTF8.GetBytes(text));

    public static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    public static string Sha256Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static UploadChunkPayload Payload(Guid jobId, int index, params string[] lines)
    {
        byte[] gzipped = Gzip(string.Join('\n', lines));

        return new UploadChunkPayload
        {
            JobId = jobId.ToString("D"),
            Index = index,
            Sha256 = Sha256Hex(gzipped),
            Rows = lines.Length,
            Data = Convert.ToBase64String(gzipped),
        };
    }
}

/// <summary>
/// Makes deleting a job's uploaded parts fail, on every OS: Windows keeps a part open without sharing; Linux and macOS
/// make the parts folder read-only (deleting a file needs write permission on its folder). Undone on dispose.
/// </summary>
internal sealed class UndeletableChunks : IDisposable
{
    private readonly FileStream? openPart;
    private readonly string? folder;
    private readonly UnixFileMode mode;

    public UndeletableChunks(string chunksFolder)
    {
        if (OperatingSystem.IsWindows())
        {
            openPart = File.Open(Directory.GetFiles(chunksFolder)[0], FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            folder = chunksFolder;
            mode = File.GetUnixFileMode(chunksFolder);
            File.SetUnixFileMode(chunksFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        openPart?.Dispose();
        if (folder is not null && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, mode);
        }
    }
}
