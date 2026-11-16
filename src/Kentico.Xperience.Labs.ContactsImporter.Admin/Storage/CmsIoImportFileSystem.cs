namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// <see cref="IImportFileSystem"/> over CMS.IO, so storage path mapping (Azure Blob, shared storage) applies.
/// </summary>
internal sealed class CmsIoImportFileSystem : IImportFileSystem
{
    public string ResolveRoot(string storagePath) =>
        storagePath.StartsWith('~') ? CMS.IO.StorageHelper.GetFullFolderPhysicalPath(storagePath, null) : storagePath;

    public string Combine(string path, string name) => CMS.IO.Path.Combine(path, name);

    public bool FileExists(string path) => CMS.IO.File.Exists(path);

    public bool DirectoryExists(string path) => CMS.IO.Directory.Exists(path);

    public void CreateDirectory(string path) => CMS.IO.Directory.CreateDirectory(path);

    public byte[] ReadAllBytes(string path) => CMS.IO.File.ReadAllBytes(path);

    public Stream OpenRead(string path) => CMS.IO.FileStream.New(path, CMS.IO.FileMode.Open, CMS.IO.FileAccess.Read, CMS.IO.FileShare.Read);

    public void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        using var stream = CMS.IO.FileStream.New(path, CMS.IO.FileMode.Create, CMS.IO.FileAccess.Write);
        stream.Write(content);
    }

    public void ReplaceFile(string sourcePath, string destinationPath)
    {
        // CMS.IO has no replace operation. A reader can hit the short gap between delete and move;
        // ImportJobStore retries reads for that case.
        if (CMS.IO.File.Exists(destinationPath))
        {
            CMS.IO.File.Delete(destinationPath);
        }

        CMS.IO.File.Move(sourcePath, destinationPath);
    }

    public void DeleteFile(string path)
    {
        if (CMS.IO.File.Exists(path))
        {
            CMS.IO.File.Delete(path);
        }
    }

    public void DeleteDirectory(string path)
    {
        if (CMS.IO.Directory.Exists(path))
        {
            CMS.IO.Directory.Delete(path, true);
        }
    }

    public IReadOnlyList<string> GetFileNames(string path) =>
        CMS.IO.Directory.Exists(path) ? [.. CMS.IO.Directory.GetFiles(path).Select(CMS.IO.Path.GetFileName)] : [];

    public IReadOnlyList<(string Name, long Length)> GetFileSizes(string path) =>
        CMS.IO.Directory.Exists(path) ? [.. CMS.IO.DirectoryInfo.New(path).GetFiles().Select(file => (file.Name, file.Length))] : [];

    public IReadOnlyList<string> GetDirectoryNames(string path) =>
        CMS.IO.Directory.Exists(path) ? [.. CMS.IO.Directory.GetDirectories(path).Select(GetLastSegment)] : [];

    public DateTime? GetDirectoryLastWriteTimeUtc(string path)
    {
        if (!CMS.IO.Directory.Exists(path))
        {
            return null;
        }

        var lastWriteTime = CMS.IO.DirectoryInfo.New(path).LastWriteTime;

        return lastWriteTime == DateTime.MinValue ? null : lastWriteTime.ToUniversalTime();
    }

    private static string GetLastSegment(string directoryPath) => CMS.IO.Path.GetFileName(directoryPath.TrimEnd('/', '\\'));
}
