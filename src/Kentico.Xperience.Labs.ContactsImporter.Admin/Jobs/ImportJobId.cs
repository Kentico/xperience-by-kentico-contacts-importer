namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Job ID format. Only the canonical GUID form (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>) is accepted,
/// so a client-supplied ID can never contain path characters.
/// </summary>
internal static class ImportJobId
{
    public static bool TryParse(string? value, out Guid jobId)
    {
        jobId = Guid.Empty;

        // Guid parsing ignores surrounding white space, so the length is checked too.
        return value is { Length: 36 } && Guid.TryParseExact(value, "D", out jobId) && jobId != Guid.Empty;
    }

    public static string Format(Guid jobId) => jobId.ToString("D");
}
