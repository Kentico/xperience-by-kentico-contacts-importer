using CMS.Membership;

using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.Authentication;
using Kentico.Xperience.Admin.DigitalMarketing.UIPages;
using Kentico.Xperience.Labs.ContactsImporter.Admin;

[assembly: UIPage(
    parentType: typeof(ImportsListPage),
    slug: PageParameterConstants.PARAMETERIZED_SLUG,
    uiPageType: typeof(ImportPage),
    name: "{$" + ImportTextKeys.PAGE_IMPORT + "$}",
    templateName: ImportPage.TEMPLATE_NAME,
    order: 100)]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// One import: File → Map → Upload → Options → Review → Run → Results. The job ID is part of the URL; the
/// browser picks it for a new import and the job is created on the first <c>SAVE_CONFIG</c>.
/// </summary>
/// <remarks>
/// Every command needs <see cref="ContactsImporterPermissions.IMPORT"/>; confirming an import that affects consent
/// also needs <see cref="ContactsImporterPermissions.CONFIRM_CONSENT"/>. Imports are not private to their
/// creator: any user with the permission can open, continue, cancel or delete any import.
/// </remarks>
[UIEvaluatePermission(SystemPermissions.VIEW)]
internal sealed class ImportPage(
    IImportCommandService commands,
    IUIPermissionEvaluator permissionEvaluator,
    IAuthenticatedUserAccessor authenticatedUserAccessor,
    IPageLinkGenerator pageLinkGenerator) : Page<ImportClientProperties>
{
    public const string TEMPLATE_NAME = "@kentico/xperience-labs-contacts-importer-admin/Import";

    /// <summary>Job ID from the URL. Untrusted: every command validates it.</summary>
    [PageParameter(typeof(StringPageModelBinder))]
    public string JobId { get; set; } = "";

    public override async Task<ImportClientProperties> ConfigureTemplateProperties(ImportClientProperties properties)
    {
        // UI guard only: the commands check the permission themselves.
        properties.CanImport = (await permissionEvaluator.Evaluate(ContactsImporterPermissions.IMPORT)).Succeeded;
        properties.JobId = ImportJobId.TryParse(JobId, out var jobId) ? ImportJobId.Format(jobId) : "";
        properties.ImportsListPath = pageLinkGenerator.GetPath<ImportsListPage>();
        properties.ContactGroupsPath = pageLinkGenerator.GetPath<ContactGroupsApplication>();
        // UI hint only: CONFIRM_IMPORT checks the permission itself.
        properties.CanConfirmConsent = (await permissionEvaluator.Evaluate(ContactsImporterPermissions.CONFIRM_CONSENT)).Succeeded;

        return properties;
    }

    [PageCommand(CommandName = ImportCommands.LOAD_CONFIG, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<ImportConfigResponse>> LoadConfig() =>
        Task.FromResult(ResponseFrom(commands.GetConfig()));

    [PageCommand(CommandName = ImportCommands.GET_JOB, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<ImportJobResponse>> GetJob() =>
        Task.FromResult(ResponseFrom(commands.GetJob(JobId)));

    [PageCommand(CommandName = ImportCommands.SAVE_CONFIG, Permission = ContactsImporterPermissions.IMPORT)]
    public async Task<ICommandResponse<SaveConfigResponse>> SaveConfig(SaveConfigRequest request)
    {
        var user = await authenticatedUserAccessor.Get();

        return ResponseFrom(commands.SaveConfig(JobId, request, user?.UserID ?? 0));
    }

    [PageCommand(CommandName = ImportCommands.UPLOAD_CHUNK, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<UploadChunkResponse>> UploadChunk(UploadChunkPayload payload) =>
        Task.FromResult(ResponseFrom(commands.UploadChunk(JobId, payload)));

    [PageCommand(CommandName = ImportCommands.TRUNCATE_CHUNKS, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<TruncateChunksResponse>> TruncateChunks(TruncateChunksRequest request) =>
        Task.FromResult(ResponseFrom(commands.TruncateChunks(JobId, request)));

    [PageCommand(CommandName = ImportCommands.COMPLETE_UPLOAD, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<CompleteUploadResponse>> CompleteUpload(CompleteUploadRequest request) =>
        Task.FromResult(ResponseFrom(commands.CompleteUpload(JobId, request)));

    [PageCommand(CommandName = ImportCommands.SAVE_OPTIONS, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<ImportJobResponse>> SaveOptions(ImportOptionsDto request) =>
        Task.FromResult(ResponseFrom(commands.SaveOptions(JobId, request)));

    /// <summary>
    /// Synchronous work of up to the processing time budget plus one batch on the request thread. The request
    /// cancellation token stops it after the current batch when the browser goes away.
    /// </summary>
    [PageCommand(CommandName = ImportCommands.PROCESS_NEXT, Permission = ContactsImporterPermissions.IMPORT)]
    public async Task<ICommandResponse<ImportProgressDto>> ProcessNext(ProcessNextRequest request, CancellationToken cancellationToken) =>
        ResponseFrom(commands.ProcessNext(JobId, request, await CurrentUserId(), cancellationToken));

    /// <summary>
    /// Needs <see cref="ContactsImporterPermissions.IMPORT"/>; an import that changes recipient list subscriptions or
    /// records consent also needs <see cref="ContactsImporterPermissions.CONFIRM_CONSENT"/>, evaluated here for the
    /// signed-in user and enforced by the command service.
    /// </summary>
    [PageCommand(CommandName = ImportCommands.CONFIRM_IMPORT, Permission = ContactsImporterPermissions.IMPORT)]
    public async Task<ICommandResponse<ImportJobResponse>> Confirm(ConfirmImportRequest request)
    {
        var user = await authenticatedUserAccessor.Get();
        bool canConfirmConsent = (await permissionEvaluator.Evaluate(ContactsImporterPermissions.CONFIRM_CONSENT)).Succeeded;

        return ResponseFrom(commands.Confirm(JobId, request, user?.UserID ?? 0, canConfirmConsent));
    }

    [PageCommand(CommandName = ImportCommands.GET_REPORT, Permission = ContactsImporterPermissions.IMPORT)]
    public Task<ICommandResponse<GetReportResponse>> GetReport(GetReportRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ResponseFrom(commands.GetReport(JobId, request, cancellationToken)));

    [PageCommand(CommandName = ImportCommands.CANCEL_IMPORT, Permission = ContactsImporterPermissions.IMPORT)]
    public async Task<ICommandResponse<ImportActionResponse>> Cancel() =>
        ResponseFrom(commands.Cancel(JobId, await CurrentUserId()));

    [PageCommand(CommandName = ImportCommands.DELETE_FILES, Permission = ContactsImporterPermissions.IMPORT)]
    public async Task<ICommandResponse<ImportActionResponse>> DeleteFiles() =>
        ResponseFrom(commands.DeleteFiles(JobId, await CurrentUserId()));

    /// <summary>Signed-in user, for the event log and the job; 0 when unknown.</summary>
    private async Task<int> CurrentUserId() => (await authenticatedUserAccessor.Get())?.UserID ?? 0;
}

internal sealed class ImportClientProperties : TemplateClientProperties
{
    /// <summary>The user has the Import permission. Without it the page only explains that.</summary>
    public bool CanImport { get; set; }

    /// <summary>Canonical job ID from the URL; empty when the URL does not hold a valid ID.</summary>
    public string JobId { get; set; } = "";

    /// <summary>Admin path of the imports list.</summary>
    public string ImportsListPath { get; set; } = "";

    /// <summary>Admin path of the Contact groups application (results screen guidance).</summary>
    public string ContactGroupsPath { get; set; } = "";

    /// <summary>
    /// The user has <see cref="ContactsImporterPermissions.CONFIRM_CONSENT"/>. Without it the Run dialog of an import
    /// that affects consent explains what is missing; the server refuses such a confirmation in any case.
    /// </summary>
    public bool CanConfirmConsent { get; set; }
}
