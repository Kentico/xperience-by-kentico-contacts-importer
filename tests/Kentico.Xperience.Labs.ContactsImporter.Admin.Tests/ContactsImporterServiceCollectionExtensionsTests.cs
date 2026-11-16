using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

public class ContactsImporterServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?>? configuration = null, Action<ContactsImporterOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddContactsImporter(configure);

        return services.BuildServiceProvider();
    }

    [Test]
    public void AddContactsImporter_Returns_Same_ServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddContactsImporter();

        Assert.That(result, Is.SameAs(services));
    }

    [Test]
    public void Options_Have_Documented_Defaults()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptions<ContactsImporterOptions>>().Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.StoragePath, Is.EqualTo("~/App_Data/contacts-importer"));
            Assert.That(options.MaxFileSizeBytes, Is.EqualTo(5L * 1024 * 1024 * 1024));
            Assert.That(options.ChunkSizeBytes, Is.EqualTo(4 * 1024 * 1024));
            Assert.That(options.MaxChunkUncompressedBytes, Is.EqualTo(128L * 1024 * 1024));
            Assert.That(options.BatchSize, Is.EqualTo(1000));
            Assert.That(options.HeartbeatStaleSeconds, Is.EqualTo(60));
            Assert.That(options.ReportRetentionDays, Is.EqualTo(7));
            Assert.That(options.ReportWithKeysRetentionHours, Is.EqualTo(24));
            Assert.That(options.AbandonedJobRetentionDays, Is.EqualTo(7));
            Assert.That(options.StatusRetentionDays, Is.EqualTo(90));
            Assert.That(options.MaxChunkCompressedBytes, Is.EqualTo(8L * 1024 * 1024));
            Assert.That(options.HeartbeatStale, Is.EqualTo(TimeSpan.FromSeconds(60)));
        });
    }

    [Test]
    public void Options_Bind_From_Configuration_Section_And_Code_Overrides_Configuration()
    {
        using var provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["ContactsImporter:StoragePath"] = "~/custom/imports",
                ["ContactsImporter:BatchSize"] = "250",
                ["ContactsImporter:StatusRetentionDays"] = "30",
            },
            options => options.BatchSize = 500);

        var options = provider.GetRequiredService<IOptions<ContactsImporterOptions>>().Value;

        Assert.Multiple(() =>
        {
            Assert.That(options.StoragePath, Is.EqualTo("~/custom/imports"));
            Assert.That(options.StatusRetentionDays, Is.EqualTo(30));
            Assert.That(options.BatchSize, Is.EqualTo(500));
            Assert.That(options.ReportRetentionDays, Is.EqualTo(7));
        });
    }

    [Test]
    public void Invalid_Options_Fail_With_All_Problems_Listed()
    {
        using var provider = BuildProvider(configure: options =>
        {
            options.StoragePath = " ";
            options.BatchSize = 0;
            options.ChunkSizeBytes = -1;
        });

        Assert.That(
            () => provider.GetRequiredService<IOptions<ContactsImporterOptions>>().Value,
            Throws.TypeOf<OptionsValidationException>()
                .With.Message.Contains(nameof(ContactsImporterOptions.StoragePath))
                .And.Message.Contains(nameof(ContactsImporterOptions.BatchSize))
                .And.Message.Contains(nameof(ContactsImporterOptions.ChunkSizeBytes)));
    }

    [Test]
    public void Services_Resolve()
    {
        using var provider = BuildProvider();

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetRequiredService<IImportJobStore>(), Is.TypeOf<ImportJobStore>());
            Assert.That(provider.GetRequiredService<IImportFileSystem>(), Is.TypeOf<CmsIoImportFileSystem>());
            Assert.That(provider.GetRequiredService<IContactFieldSchemaProvider>(), Is.TypeOf<ContactFieldSchemaProvider>());
            Assert.That(provider.GetRequiredService<IChunkIngestService>(), Is.TypeOf<ChunkIngestService>());
            Assert.That(provider.GetRequiredService<IImportJobCleanupService>(), Is.TypeOf<ImportJobCleanupService>());
            Assert.That(provider.GetRequiredService<TimeProvider>(), Is.SameAs(TimeProvider.System));
        });
    }

    [Test]
    public void Existing_Registrations_Are_Kept_And_Repeated_Calls_Do_Not_Duplicate()
    {
        var services = new ServiceCollection();
        var time = new MutableTimeProvider(TestData.Now);
        services.AddSingleton<TimeProvider>(time);

        services.AddContactsImporter();
        services.AddContactsImporter();

        Assert.Multiple(() =>
        {
            Assert.That(services.Count(s => s.ServiceType == typeof(TimeProvider)), Is.EqualTo(1));
            Assert.That(services.Count(s => s.ServiceType == typeof(IImportJobStore)), Is.EqualTo(1));
            Assert.That(services.Count(s => s.ServiceType == typeof(IValidateOptions<ContactsImporterOptions>)), Is.EqualTo(1));
        });
    }
}
