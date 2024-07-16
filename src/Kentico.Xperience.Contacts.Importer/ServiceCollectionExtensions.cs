using Kentico.Xperience.Contacts.Importer;
using Kentico.Xperience.Contacts.Importer.Services;

using Microsoft.AspNetCore.Builder;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{

    public static IServiceCollection AddContactsImport(this IServiceCollection services) =>
        services.AddTransient<IImportService, ImportService>();
}

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseContactsImport(this IApplicationBuilder builder)
    {
        builder.UseWebSockets();
        return builder.UseMiddleware<ContactsImportMiddleware>();
    }
}
