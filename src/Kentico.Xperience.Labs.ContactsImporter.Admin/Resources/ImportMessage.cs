using System.Globalization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// A text for the user as a resource key of <c>ContactsImporterResources.resx</c> and its arguments. Services return
/// these (and store them in the job status); the UI pages turn them into text in the user's admin UI language
/// (<see cref="IImportTexts.Get(ImportMessage)"/>). Create them with <see cref="ImportMessages"/>.
/// </summary>
internal sealed class ImportMessage : IEquatable<ImportMessage>
{
    private static readonly ResourceImportTexts englishTexts = new();

    /// <param name="key">Resource key (<see cref="ImportTextKeys"/>).</param>
    /// <param name="arguments">Values of the placeholders <c>{0}</c>, <c>{1}</c>, …; stored as invariant strings.</param>
    public ImportMessage(string key, params object?[] arguments)
    {
        Key = key;
        Arguments = [.. arguments.Select(argument => Convert.ToString(argument, CultureInfo.InvariantCulture) ?? "")];
    }

    public string Key { get; }

    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// The message stored as <see cref="ImportJobStatus.Error"/> and <see cref="ImportJobStatus.ErrorArguments"/>.
    /// Jobs of earlier versions stored English text there, which comes back as a key without the importer prefix
    /// (<see cref="IImportTexts"/> shows such text as it is).
    /// </summary>
    public static ImportMessage? FromStored(string? key, IReadOnlyList<string>? arguments) =>
        key is null ? null : new ImportMessage(key, [.. arguments ?? []]);

    public bool Equals(ImportMessage? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal) && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ImportMessage);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    /// <summary>The English (en-US) text, for logs and test output. Never shown in the UI.</summary>
    public override string ToString() => englishTexts.Get(this);
}
