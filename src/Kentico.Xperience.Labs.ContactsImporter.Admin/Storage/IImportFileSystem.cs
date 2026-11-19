namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// The file operations the job store needs. Production code uses CMS.IO (<see cref="CmsIoImportFileSystem"/>)
/// so storage path mapping applies; CMS.IO needs an initialized Xperience application, so unit tests use
/// a System.IO implementation.
/// </summary>
internal interface IImportFileSystem
{
    /// <summary>
    /// Turns the configured storage path (application-relative <c>~/...</c> or absolute) into the path
    /// passed to the other members.
    /// </summary>
    public string ResolveRoot(string storagePath);

    public string Combine(string path, string name);

    public bool FileExists(string path);

    public bool DirectoryExists(string path);

    public void CreateDirectory(string path);

    public byte[] ReadAllBytes(string path);

    public Stream OpenRead(string path);

    public void WriteAllBytes(string path, ReadOnlySpan<byte> content);

    /// <summary>
    /// Moves <paramref name="sourcePath"/> over <paramref name="destinationPath"/>, replacing an existing file.
    /// </summary>
    public void ReplaceFile(string sourcePath, string destinationPath);

    /// <summary>Deletes the file; does nothing when it does not exist.</summary>
    public void DeleteFile(string path);

    /// <summary>Deletes the directory with its content; does nothing when it does not exist.</summary>
    public void DeleteDirectory(string path);

    /// <summary>Names (not paths) of files directly in the directory; empty when it does not exist.</summary>
    public IReadOnlyList<string> GetFileNames(string path);

    /// <summary>Names (not paths) and sizes in bytes of files directly in the directory; empty when it does not exist.</summary>
    public IReadOnlyList<(string Name, long Length)> GetFileSizes(string path);

    /// <summary>Names (not paths) of directories directly in the directory; empty when it does not exist.</summary>
    public IReadOnlyList<string> GetDirectoryNames(string path);

    /// <summary>Last write time of the directory in UTC, or <c>null</c> when the storage does not track it.</summary>
    public DateTime? GetDirectoryLastWriteTimeUtc(string path);
}
