using System.Globalization;

using CMS.ContactManagement;

using CsvHelper;

using Kentico.Xperience.Contacts.Importer.Generator;

using static Kentico.Xperience.Contacts.Importer.Services.ImportService;

string? solutionFolder = FindSolutionFolder();

ArgumentNullException.ThrowIfNull(solutionFolder);

using var writer = new StreamWriter(Path.Combine(solutionFolder, "data\\contact_sample.csv"));
using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
csv.Context.RegisterClassMap<ContactInfoMap>();

csv.WriteHeader<ContactInfo>();
await csv.NextRecordAsync();

foreach (var contact in ContactsGenerator.Generate(2500))
{
    csv.WriteRecord(contact);
    await csv.NextRecordAsync();
}

await csv.FlushAsync();


static string? FindSolutionFolder()
{
    // Get the current directory where the application is running
    string? currentDirectory = Directory.GetCurrentDirectory();

    // Navigate up the directory tree until the solution file is found
    while (currentDirectory != null)
    {
        // Check if the current directory contains a .sln file
        string[] solutionFiles = Directory.GetFiles(currentDirectory, "*.sln");
        if (solutionFiles.Length > 0)
        {
            return currentDirectory;
        }

        // Move up to the parent directory
        currentDirectory = Directory.GetParent(currentDirectory)?.FullName;
    }

    // If no solution file is found, return null or an empty string
    return null;
}
