using Kentico.Xperience.Contacts.Importer;
using Kentico.Xperience.Contacts.Importer.Services;

using Microsoft.AspNetCore.Builder;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers services required for Xperience by Kentico contact import application.
/// </summary>
public static class ServiceCollectionExtensions
{
    [Obsolete(DeprecationMessages.PackageRenamed, DiagnosticId = DeprecationMessages.DiagnosticId, UrlFormat = DeprecationMessages.Url)]
    public static IServiceCollection AddContactsImport(this IServiceCollection services) =>
        services.AddTransient<IImportService, ImportService>();
}


/// <summary>
/// Registers middleware required for Xperience by Kentico contact import application.
/// </summary>
public static class ApplicationBuilderExtensions
{
    [Obsolete(DeprecationMessages.PackageRenamed, DiagnosticId = DeprecationMessages.DiagnosticId, UrlFormat = DeprecationMessages.Url)]
    public static IApplicationBuilder UseContactsImport(this IApplicationBuilder builder)
    {
        builder.UseWebSockets();
        return builder.UseMiddleware<ContactsImportMiddleware>();
    }
}
