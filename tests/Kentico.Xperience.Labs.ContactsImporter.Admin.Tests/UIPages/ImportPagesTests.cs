using System.Reflection;

using CMS.Membership;

using Kentico.Membership;

using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.Authentication;
using Kentico.Xperience.Admin.Base.UIPages;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.UIPages;

/// <summary>
/// The pages are thin: these tests pin what must never be forgotten on them (a permission on every
/// command, the registered names) and that they pass the job ID from the URL to the command service.
/// </summary>
internal class ImportPagesTests
{
    private static readonly Type[] pageTypes = [typeof(ImportsListPage), typeof(ImportPage)];

    private static IEnumerable<(Type Page, MethodInfo Method, PageCommandAttribute Command)> PageCommands() =>
        pageTypes.SelectMany(page => page
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => (page, method, command: method.GetCustomAttribute<PageCommandAttribute>()))
            .Where(m => m.command is not null)
            .Select(m => (m.page, m.method, m.command!)));

    [Test]
    public void Every_Page_Command_Requires_A_Permission()
    {
        var commands = PageCommands().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(commands, Is.Not.Empty);
            foreach (var (page, method, command) in commands)
            {
                Assert.That(command.Permission, Is.Not.Null.And.Not.Empty, $"{page.Name}.{method.Name}");
            }
        });
    }

    [Test]
    public void Only_The_List_Command_Works_With_The_View_Permission()
    {
        var viewCommands = PageCommands().Where(c => c.Command.Permission == SystemPermissions.VIEW).Select(c => c.Command.CommandName);
        var otherPermissions = PageCommands().Where(c => c.Command.Permission != SystemPermissions.VIEW).Select(c => c.Command.Permission).Distinct();

        Assert.Multiple(() =>
        {
            Assert.That(viewCommands, Is.EqualTo(new[] { ImportCommands.LIST_IMPORTS }));
            Assert.That(otherPermissions, Is.EqualTo(new[] { ContactsImporterPermissions.IMPORT }));
        });
    }

    [Test]
    public void Pages_Register_The_Agreed_Command_Names()
    {
        static string?[] Names(Type page) => [.. PageCommands().Where(c => c.Page == page).Select(c => c.Command.CommandName).Order()];

        Assert.Multiple(() =>
        {
            Assert.That(Names(typeof(ImportsListPage)), Is.EqualTo(new[] { "DELETE_FILES", "LIST_IMPORTS" }));
            Assert.That(Names(typeof(ImportPage)), Is.EqualTo(new[]
            {
                "CANCEL_IMPORT", "COMPLETE_UPLOAD", "CONFIRM_IMPORT", "DELETE_FILES", "GET_JOB", "GET_REPORT", "LOAD_CONFIG", "PROCESS_NEXT", "SAVE_CONFIG",
                "SAVE_OPTIONS", "TRUNCATE_CHUNKS", "UPLOAD_CHUNK",
            }));
        });
    }

    [Test]
    public void Pages_Are_Guarded_By_The_View_Permission() => Assert.Multiple(() =>
                                                                   {
                                                                       foreach (var page in pageTypes)
                                                                       {
                                                                           var guard = page.GetCustomAttribute<UIEvaluatePermissionAttribute>();
                                                                           Assert.That(guard?.Permission, Is.EqualTo(SystemPermissions.VIEW), page.Name);
                                                                       }
                                                                   });

    [Test]
    public void Application_Declares_Its_Permissions()
    {
        var permissions = typeof(ContactsImporterApplicationPage).GetCustomAttributes<UIPermissionAttribute>().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(permissions.Select(p => p.Name), Is.EquivalentTo(new[]
            {
                SystemPermissions.VIEW, "ContactsImporter.Import", "ContactsImporter.ConfirmConsent",
            }));
            // The admin rejects application permission names longer than 50 characters at startup.
            Assert.That(permissions.Select(p => p.Name), Has.All.Length.LessThanOrEqualTo(50));
            Assert.That(permissions.Where(p => p.Name != SystemPermissions.VIEW).Select(p => p.DisplayName), Is.EquivalentTo(new[]
            {
                "{$contactsimporter.permission.import$}", "{$contactsimporter.permission.confirmconsent$}",
            }));
        });
    }

    [Test]
    public void Application_Is_Registered_Under_Digital_Marketing()
    {
        var application = typeof(ContactsImporterApplicationPage).Assembly.GetCustomAttributes<UIApplicationAttribute>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(application.Type, Is.EqualTo(typeof(ContactsImporterApplicationPage)));
            // A localization expression; the admin shows the text of the user's admin UI language.
            Assert.That(application.Name, Is.EqualTo("{$contactsimporter.application.name$}"));
            Assert.That(new ResourceImportTexts().Get(ImportTextKeys.APPLICATION_NAME), Is.EqualTo("Contacts Importer (Labs)"));
            Assert.That(application.Slug, Is.EqualTo("contacts-importer"));
            Assert.That(application.Category, Is.EqualTo(BaseApplicationCategories.DIGITAL_MARKETING));
        });
    }

    [Test]
    public void Pages_Are_Registered_With_The_Client_Templates()
    {
        var pages = typeof(ImportPage).Assembly.GetCustomAttributes<UIPageAttribute>().Where(p => p is not UIApplicationAttribute).ToDictionary(p => p.Type);

        Assert.Multiple(() =>
        {
            Assert.That(pages.Keys, Is.EquivalentTo(pageTypes));
            Assert.That(pages[typeof(ImportsListPage)].ParentType, Is.EqualTo(typeof(ContactsImporterApplicationPage)));
            Assert.That(pages[typeof(ImportsListPage)].Slug, Is.EqualTo("imports"));
            Assert.That(pages[typeof(ImportsListPage)].TemplateName, Is.EqualTo("@kentico/xperience-labs-contacts-importer-admin/ImportsList"));
            Assert.That(pages[typeof(ImportPage)].ParentType, Is.EqualTo(typeof(ImportsListPage)));
            Assert.That(pages[typeof(ImportPage)].Slug, Is.EqualTo(PageParameterConstants.PARAMETERIZED_SLUG));
            Assert.That(pages[typeof(ImportPage)].TemplateName, Is.EqualTo("@kentico/xperience-labs-contacts-importer-admin/Import"));
        });
    }

    [Test]
    public async Task ImportPage_Commands_Use_The_Job_From_The_Url()
    {
        var commands = new RecordingCommandService();
        var page = new ImportPage(commands, new FakePermissions(), new NoUser(), null!) { JobId = "job-from-url" };

        await page.GetJob();
        await page.UploadChunk(new UploadChunkPayload { JobId = "job-from-payload" });
        await page.TruncateChunks(new TruncateChunksRequest { FromIndex = 2 });
        await page.CompleteUpload(new CompleteUploadRequest());
        await page.SaveOptions(new ImportOptionsDto());
        using var cancellation = new CancellationTokenSource();
        await page.ProcessNext(new ProcessNextRequest { Phase = "analyze" }, cancellation.Token);
        await page.Confirm(new ConfirmImportRequest { StatementVersion = "7" });
        await page.GetReport(new GetReportRequest { Phase = "analyze" }, cancellation.Token);
        await page.Cancel();
        await page.DeleteFiles();

        Assert.That(commands.Calls, Is.EqualTo(new[]
        {
            "GetJob job-from-url", "UploadChunk job-from-url", "TruncateChunks job-from-url", "CompleteUpload job-from-url",
            "SaveOptions job-from-url", "ProcessNext job-from-url True", "Confirm job-from-url 7 0 False", "GetReport job-from-url True",
            "Cancel job-from-url", "DeleteFiles job-from-url",
        }));
    }

    // A signed-in user (AdminApplicationUser) needs an initialized Xperience container, so these cases run without one:
    // user ID 0, which the command service refuses for imports that need the consent confirmation.
    [TestCase(true, "Confirm job 1 0 True")]
    [TestCase(false, "Confirm job 1 0 False", Description = "the user lacks the Confirm consent permission")]
    public async Task Confirm_Passes_The_Confirm_Consent_Permission_Of_The_Current_User(bool hasPermission, string expected)
    {
        var commands = new RecordingCommandService();
        var permissions = new FakePermissions();
        if (hasPermission)
        {
            permissions.Granted.Add(ContactsImporterPermissions.CONFIRM_CONSENT);
        }

        var page = new ImportPage(commands, permissions, new NoUser(), null!) { JobId = "job" };

        await page.Confirm(new ConfirmImportRequest { StatementVersion = "1" });

        Assert.Multiple(() =>
        {
            Assert.That(commands.Calls, Is.EqualTo(new[] { expected }));
            Assert.That(permissions.Evaluated, Is.EqualTo(new[] { ContactsImporterPermissions.CONFIRM_CONSENT }), "the Import permission is the command attribute");
        });
    }

    [Test]
    public void Confirm_Consent_Is_Checked_By_The_Command_Not_By_Its_Attribute()
    {
        // The attribute permission is the same for every import; the consent permission depends on the job options,
        // so the command evaluates it and the command service enforces it.
        var confirm = PageCommands().Single(c => c.Command.CommandName == ImportCommands.CONFIRM_IMPORT);

        Assert.Multiple(() =>
        {
            Assert.That(confirm.Command.Permission, Is.EqualTo(ContactsImporterPermissions.IMPORT));
            Assert.That(confirm.Method.GetParameters().Select(p => p.ParameterType), Is.EqualTo(new[] { typeof(ConfirmImportRequest) }));
        });
    }

    private sealed class FakePermissions : IUIPermissionEvaluator
    {
        public HashSet<string> Granted { get; } = [];

        public List<string> Evaluated { get; } = [];

        public Task<UIPermissionEvaluationResult> Evaluate(string permission)
        {
            Evaluated.Add(permission);

            return Task.FromResult(new UIPermissionEvaluationResult(Granted.Contains(permission)));
        }
    }

    private sealed class NoUser : IAuthenticatedUserAccessor
    {
        public Task<AdminApplicationUser> Get() => Task.FromResult<AdminApplicationUser>(null!);
    }

    [Test]
    public async Task ImportsListPage_Commands_Call_The_Command_Service()
    {
        var commands = new RecordingCommandService();
        var page = new ImportsListPage(commands, null!, null!, new NoUser());

        await page.ListImports();
        await page.DeleteFiles(new ImportJobRequest { JobId = "job-1" });
        await page.DeleteFiles(null!);

        Assert.That(commands.Calls, Is.EqualTo(new[] { "ListImports", "DeleteFiles job-1", "DeleteFiles " }));
    }

    private sealed class RecordingCommandService : IImportCommandService
    {
        public List<string> Calls { get; } = [];

        public ImportConfigResponse GetConfig()
        {
            Calls.Add("GetConfig");

            return new ImportConfigResponse
            {
                Fields = [],
                Limits = new ImportLimitsDto
                {
                    MaxFileSizeBytes = 1,
                    ChunkSizeBytes = 1,
                    MaxChunkCompressedBytes = 1,
                    MaxChunkUncompressedBytes = 1,
                    MaxNonTextValueLength = 1,
                    MaxKeyLength = 1,
                    ParallelUploads = 1,
                },
            };
        }

        public ImportListResponse ListImports()
        {
            Calls.Add("ListImports");

            return new ImportListResponse { Imports = [] };
        }

        public ImportJobResponse GetJob(string? jobId)
        {
            Calls.Add($"GetJob {jobId}");

            return new ImportJobResponse { Code = ImportCommandCodes.JOB_NOT_FOUND };
        }

        public SaveConfigResponse SaveConfig(string? jobId, SaveConfigRequest? request, int userId)
        {
            Calls.Add($"SaveConfig {jobId} {userId}");

            return new SaveConfigResponse { Code = ImportCommandCodes.OK };
        }

        public UploadChunkResponse UploadChunk(string? jobId, UploadChunkPayload? payload)
        {
            Calls.Add($"UploadChunk {jobId}");

            return new UploadChunkResponse { Code = ImportCommandCodes.STORED };
        }

        public TruncateChunksResponse TruncateChunks(string? jobId, TruncateChunksRequest? request)
        {
            Calls.Add($"TruncateChunks {jobId}");

            return new TruncateChunksResponse { Code = ImportCommandCodes.OK };
        }

        public CompleteUploadResponse CompleteUpload(string? jobId, CompleteUploadRequest? request)
        {
            Calls.Add($"CompleteUpload {jobId}");

            return new CompleteUploadResponse { Code = ImportCommandCodes.OK };
        }

        public ImportJobResponse SaveOptions(string? jobId, ImportOptionsDto? request)
        {
            Calls.Add($"SaveOptions {jobId}");

            return new ImportJobResponse { Code = ImportCommandCodes.OK };
        }

        public ImportProgressDto ProcessNext(string? jobId, ProcessNextRequest? request, int userId, CancellationToken cancellationToken = default)
        {
            // The page passes the request's cancellation token on.
            Calls.Add($"ProcessNext {jobId} {cancellationToken.CanBeCanceled}");

            return new ImportProgressDto { Outcome = "processed", Phase = "analyze", State = "Analyzing", Aggregates = new ImportAggregatesDto() };
        }

        public ImportJobResponse Confirm(string? jobId, ConfirmImportRequest? request, int userId, bool canConfirmConsent)
        {
            Calls.Add($"Confirm {jobId} {request?.StatementVersion} {userId} {canConfirmConsent}");

            return new ImportJobResponse { Code = ImportCommandCodes.OK };
        }

        public GetReportResponse GetReport(string? jobId, GetReportRequest? request, CancellationToken cancellationToken = default)
        {
            Calls.Add($"GetReport {jobId} {cancellationToken.CanBeCanceled}");

            return new GetReportResponse { Code = ImportCommandCodes.OK };
        }

        public ImportActionResponse Cancel(string? jobId, int userId)
        {
            Calls.Add($"Cancel {jobId}");

            return new ImportActionResponse { Code = ImportCommandCodes.OK };
        }

        public ImportActionResponse DeleteFiles(string? jobId, int userId)
        {
            Calls.Add($"DeleteFiles {jobId}");

            return new ImportActionResponse { Code = ImportCommandCodes.OK };
        }
    }
}
