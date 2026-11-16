using CMS.Membership;

using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Labs.ContactsImporter.Admin;

[assembly: UIPage(
    parentType: typeof(ContactsImporterApplicationPage),
    slug: ImportsListPage.SLUG,
    uiPageType: typeof(ImportsListPage),
    name: "Imports",
    templateName: ImportsListPage.TEMPLATE_NAME,
    order: 100,
    Icon = Icons.ArrowUpLine)]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Recent imports with "New import", continue and "Delete files". Opening the list needs the View permission;
/// everything that changes an import needs <see cref="ContactsImporterPermissions.IMPORT"/>.
/// </summary>
[UIEvaluatePermission(SystemPermissions.VIEW)]
internal sealed class ImportsListPage(
    IImportCommandService commands,
    IUIPermissionEvaluator permissionEvaluator,
    IPageLinkGenerator pageLinkGenerator) : Page<ImportsListClientProperties>
{
    public const string SLUG = "imports";
    public const string TEMPLATE_NAME = "@kentico/xperience-labs-contacts-importer-admin/ImportsList";

    /// <summary>Job ID the client replaces in <see cref="ImportsListClientProperties.ImportPathTemplate"/>.</summary>
    internal const string JOB_ID_PLACEHOLDER = "00000000-0000-0000-0000-000000000000";

    public override async Task<ImportsListClientProperties> ConfigureTemplateProperties(ImportsListClientProperties properties)
    {
        // UI guard only: the commands check the permission themselves.
        properties.CanImport = (await permissionEvaluator.Evaluate(ContactsImporterPermissions.IMPORT)).Succeeded;
        properties.ImportPathTemplate = pageLinkGenerator.GetPath<ImportPage>(new PageParameterValues { { typeof(ImportPage), JOB_ID_PLACEHOLDER } });
        properties.JobIdPlaceholder = JOB_ID_PLACEHOLDER;

        return properties;
    }

    [PageCommand(CommandName = ImportCommands.LIST_IMPORTS, Permission = SystemPermissions.VIEW)]
    public Task<ICommandResponse<ImportListResponse>> ListImports() =>
        Task.FromResult(ResponseFrom(commands.ListImports()));

    [PageCommand(CommandName = ImportCommands.DELETE_FILES, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<ImportActionResponse>> DeleteFiles(ImportJobRequest request) =>
        Task.FromResult(ResponseFrom(commands.DeleteFiles(request?.JobId)));
}

internal sealed class ImportsListClientProperties : TemplateClientProperties
{
    /// <summary>The user has the Import permission. The client hides the actions when not.</summary>
    public bool CanImport { get; set; }

    /// <summary>Admin path of the import page with <see cref="JobIdPlaceholder"/> in place of the job ID.</summary>
    public string ImportPathTemplate { get; set; } = "";

    public string JobIdPlaceholder { get; set; } = "";
}
