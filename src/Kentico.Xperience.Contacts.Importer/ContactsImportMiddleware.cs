using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using CMS.Core;

using Kentico.Xperience.Contacts.Importer.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

using Newtonsoft.Json;

namespace Kentico.Xperience.Contacts.Importer;

public class ContactsImportMiddleware(RequestDelegate next, IImportService importService, IEventLogService eventLogService)
{
    private const string SOURCE = "Contact.Importer";
    private const string UPLOAD_PATH = "/contactsimport/upload";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path != UPLOAD_PATH)
        {
            await next(context);
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = HttpMethods.Post;
            return;
        }

        if (!TryCreateImportContext(context.Request.Query, out var importContext, out string? error))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(error, context.RequestAborted);
            return;
        }

        await UploadAndImport(context, importContext);
    }

    private async Task UploadAndImport(HttpContext context, ImportContext importContext)
    {
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        
        var progress = new ProgressWriter(context.Response, context.RequestAborted);

        await progress.Send("importStarted");

        await using var csvFile = new FileStream(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose);

        try
        {
            await context.Request.Body.CopyToAsync(csvFile, context.RequestAborted);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            eventLogService.LogException(SOURCE, "UPLOAD", e);
            await progress.Send("error", e.Message);
        }

        try
        {
            csvFile.Position = 0;
            
            await importService.RunImport(
                csvFile,
                importContext,
                (_, totalProcessed) => progress.Send("msg", $"Total processed {totalProcessed}"),
                exception => progress.Send("msg", $"{exception}"));

            await progress.Send("finished");
        }
        catch (Exception e)
        {
            eventLogService.LogException(SOURCE, "IMPORT", e);
            await progress.Send("error", e.Message);
        }
        finally
        {
            await progress.Complete();
        }
    }

    private static bool TryCreateImportContext(
        IQueryCollection query,
        [NotNullWhen(true)] out ImportContext? importContext,
        [NotNullWhen(false)] out string? error)
    {
        importContext = null;

        string? importKindQueryValue = query["importKind"];

        string importKind = string.IsNullOrEmpty(importKindQueryValue)
            ? ImportKind.InsertAndSkipExisting
            : importKindQueryValue;

        Guid? contactGroupGuid = null;
        string? contactGroupQueryValue = query["contactGroup"];
        if (!string.IsNullOrEmpty(contactGroupQueryValue))
        {
            contactGroupGuid = Guid.Parse(contactGroupQueryValue);;
        }

        Guid? recipientListGuid = null;
        string? recipientListQueryValue = query["recipientList"];
        if (!string.IsNullOrEmpty(recipientListQueryValue))
        {
            recipientListGuid = Guid.Parse(recipientListQueryValue);
        }

        string? batchSizeQueryValue = query["batchSize"];
        if (!int.TryParse(batchSizeQueryValue, NumberStyles.None, CultureInfo.InvariantCulture, out int batchSize) ||
            batchSize <= 0)
        {
            error = "The batch size must be a positive whole number.";
            return false;
        }

        string delimiter = query["delimiter"].ToString();
        if (string.IsNullOrEmpty(delimiter))
        {
            error = "Delimiter is empty.";
            return false;
        }

        importContext = new ImportContext(contactGroupGuid, recipientListGuid, batchSize, delimiter, importKind);
        error = null;

        return true;
    }

    private sealed class ProgressWriter(HttpResponse response, CancellationToken cancellationToken)
    {
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private bool completed;

        public async Task Send(string type, string payload = "")
        {
            await writeLock.WaitAsync(CancellationToken.None);

            try
            {
                if (completed)
                {
                    return;
                }

                byte[] line = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type, payload }) + "\n");
                await response.Body.WriteAsync(line, cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // client disconnected, the import carries on without reporting
                completed = true;
            }
            finally
            {
                writeLock.Release();
            }
        }

        public async Task Complete()
        {
            await writeLock.WaitAsync(CancellationToken.None);
            completed = true;
            writeLock.Release();
        }
    }
}
