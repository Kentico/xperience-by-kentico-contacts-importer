using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

public static class ContactsImporterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Contacts Importer services. Options are bound from the <c>ContactsImporter</c>
    /// configuration section; <paramref name="configure"/> runs afterwards and can override them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional changes to the options.</param>
    public static IServiceCollection AddContactsImporter(this IServiceCollection services, Action<ContactsImporterOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<ContactsImporterOptions>()
            .BindConfiguration(ContactsImporterOptions.SectionName);
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ContactsImporterOptions>, ContactsImporterOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IImportFileSystem, CmsIoImportFileSystem>();
        services.TryAddSingleton<IImportJobStore, ImportJobStore>();
        services.TryAddSingleton<IContactFieldSchemaProvider, ContactFieldSchemaProvider>();
        services.TryAddSingleton<IChunkIngestService, ChunkIngestService>();
        services.TryAddSingleton<IImportJobCleanupService, ImportJobCleanupService>();

        // Processing engine (Processing)
        services.TryAddSingleton<IContactImportRepository, ContactImportRepository>();
        services.TryAddSingleton<IImportRunLock, ImportRunLock>();
        services.TryAddSingleton<IImportCacheInvalidator, ImportCacheInvalidator>();
        services.TryAddSingleton<IImportReportReader, ImportReportReader>();
        services.TryAddSingleton<IImportAudienceResolver, ImportAudienceResolver>();
        services.TryAddSingleton<IImportEventPublisher, ImportEventPublisher>();
        services.TryAddSingleton<IImportProcessor, ImportProcessor>();

        // Admin pages (UIPages)
        services.TryAddTransient<IImportTexts, LocalizedImportTexts>();
        services.TryAddTransient<IImportUserNameProvider, ImportUserNameProvider>();
        services.TryAddTransient<IImportChoiceProvider, ImportChoiceProvider>();
        services.TryAddSingleton<IImportProcessGate, ImportProcessGate>();
        services.TryAddSingleton<IImportReportPager, ImportReportPager>();
        services.TryAddTransient<IImportCommandService, ImportCommandService>();

        return services;
    }
}
