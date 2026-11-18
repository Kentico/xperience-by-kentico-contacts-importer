using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Kentico.Xperience.Labs.ContactsImporter.Admin.Resources;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// The server texts are in <c>Resources/ContactsImporterResources.resx</c> and the code refers to them by the constants
/// of <see cref="ImportTextKeys"/>. These checks stand in for compile-time checking of the keys: every constant has a
/// text, every text has a constant, and a text with placeholders is only used through an <see cref="ImportMessages"/>
/// method that takes exactly its arguments.
/// </summary>
internal sealed partial class ImportResourcesTests
{
    private static readonly string adminProjectPath = AdminProject();

    private static readonly Dictionary<string, string> resources = ReadResx(Path.Combine(adminProjectPath, "Resources", "ContactsImporterResources.resx"));

    private static readonly List<string> keyConstants = [.. KeyFields().Select(field => (string)field.GetRawConstantValue()!)];

    [Test]
    public void Every_Key_Constant_Has_A_Text() => Assert.That(keyConstants.Where(key => !resources.ContainsKey(key)), Is.Empty);

    [Test]
    public void Every_Text_Has_A_Key_Constant() => Assert.That(resources.Keys.Where(key => !keyConstants.Contains(key)), Is.Empty);

    [Test]
    public void Keys_Use_The_Importer_Prefix_And_Placeholders_Are_Numbered_From_Zero()
    {
        var problems = resources
            .Where(entry => !entry.Key.StartsWith(ContactsImporterResources.KEY_PREFIX, StringComparison.Ordinal)
                || !Placeholders(entry.Value).Distinct().Order().SequenceEqual(Enumerable.Range(0, PlaceholderCount(entry.Value))))
            .Select(entry => entry.Key);

        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void Every_Message_Method_Passes_The_Arguments_Of_Its_Text()
    {
        var problems = new List<string>();
        var keysWithMethods = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in typeof(ImportMessages).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(ImportMessage) && !m.IsSpecialName))
        {
            var message = (ImportMessage)method.Invoke(null, [.. method.GetParameters().Select(p => SampleArgument(p.ParameterType))])!;
            keysWithMethods.Add(message.Key);
            if (!resources.TryGetValue(message.Key, out string? text) || message.Arguments.Count != PlaceholderCount(text))
            {
                problems.Add($"{method.Name}: {message.Key}");
            }
        }

        foreach (var property in typeof(ImportMessages).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            var message = (ImportMessage)property.GetValue(null)!;
            if (!resources.TryGetValue(message.Key, out string? text) || PlaceholderCount(text) != 0)
            {
                problems.Add($"{property.Name}: {message.Key}");
            }
        }

        problems.AddRange(resources.Keys.Where(key => PlaceholderCount(resources[key]) > 0 && !keysWithMethods.Contains(key)).Select(key => $"{key} has placeholders but no ImportMessages method"));

        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void Texts_With_Placeholders_Are_Used_Only_Through_Their_Message_Method()
    {
        var withPlaceholders = KeyFields().Where(field => PlaceholderCount(resources.GetValueOrDefault((string)field.GetRawConstantValue()!, "")) > 0).Select(field => field.Name).ToList();
        var misuse = SourceFiles()
            .Where(file => !file.EndsWith("ImportTexts.cs", StringComparison.Ordinal))
            .SelectMany(file => withPlaceholders.Where(name => File.ReadAllText(file).Contains($"ImportTextKeys.{name}", StringComparison.Ordinal)).Select(name => $"{Path.GetFileName(file)}: {name}"));

        Assert.That(misuse, Is.Empty);
    }

    [Test]
    public void Code_Refers_To_Keys_Only_Through_The_Constants()
    {
        var literals = SourceFiles()
            .Where(file => !file.EndsWith("ImportTexts.cs", StringComparison.Ordinal) && !file.Contains($"{Path.DirectorySeparatorChar}Resources{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains($"\"{ContactsImporterResources.KEY_PREFIX}", StringComparison.Ordinal))
            .Select(Path.GetFileName);

        Assert.That(literals, Is.Empty);
    }

    [Test]
    public void Every_Chunk_Reject_Reason_Has_Its_Own_Text()
    {
        var problems = Enum.GetValues<ChunkRejectReason>()
            .Where(reason => ImportTextKeys.ChunkRejected(reason) is var key
                && (!resources.ContainsKey(key) || !key.EndsWith("." + reason.ToString().ToLowerInvariant(), StringComparison.Ordinal)));

        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void English_Text_Of_A_Message_Is_Formatted_From_The_Resources() => Assert.Multiple(() =>
                                                                                    {
                                                                                        Assert.That(ImportMessages.ChunkMissing(4).ToString(), Is.EqualTo("Uploaded data chunk 4 is missing. Upload the file again."));
                                                                                        Assert.That(ImportMessages.FieldMappedTwice("ContactEmail"), Is.EqualTo(new ImportMessage(ImportTextKeys.FIELD_MAPPED_TWICE, "ContactEmail")));
                                                                                        Assert.That(ImportMessage.FromStored(ImportTextKeys.CHUNK_MISSING, ["4"]), Is.EqualTo(ImportMessages.ChunkMissing(4)));
                                                                                    });

    private static IEnumerable<FieldInfo> KeyFields() =>
        typeof(ImportTextKeys).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral && field.FieldType == typeof(string));

    private static object SampleArgument(Type type)
    {
        if (type == typeof(string))
        {
            return "x";
        }

        return type == typeof(ImportJobState) ? ImportJobState.Uploaded : Convert.ChangeType(1, type, CultureInfo.InvariantCulture);
    }

    private static int PlaceholderCount(string text) => Placeholders(text).Select(index => index + 1).DefaultIfEmpty(0).Max();

    private static IEnumerable<int> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(data => (string)data.Attribute("name")!, data => (string)data.Element("value")!, StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(adminProjectPath, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Client{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// The admin project folder, written into this assembly at build time (<c>AssemblyMetadata</c> in the project file).
    /// Not from <c>[CallerFilePath]</c>: CI builds map source paths (<c>ContinuousIntegrationBuild</c>), and the build
    /// output can be anywhere.
    /// </summary>
    private static string AdminProject() =>
        typeof(ImportResourcesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "AdminProjectDirectory").Value!;

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
