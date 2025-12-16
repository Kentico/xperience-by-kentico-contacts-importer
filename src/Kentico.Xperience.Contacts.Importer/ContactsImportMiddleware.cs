using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using CMS.Core;

using Kentico.Xperience.Contacts.Importer.Auxiliary;
using Kentico.Xperience.Contacts.Importer.Services.ImportService;

using Microsoft.AspNetCore.Http;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kentico.Xperience.Contacts.Importer;

public class ContactsImportMiddleware(RequestDelegate next, IImportService importService, IEventLogService eventLogService)
{
    private const string SOURCE = "Contact.Importer";

    private readonly RequestDelegate next = next;
    private readonly IImportService importService = importService;
    private readonly IEventLogService eventLogService = eventLogService;


    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/contactsimport/ws")
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await DownloadAndImport(webSocket, importService, eventLogService, default);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        }
        else
        {
            await next(context);
        }
    }


    private sealed record Message(string Type, HeaderPayload? Payload);


    private sealed record HeaderPayload(string ImportKind, Guid? ContactGroup, int? BatchSize, string Delimiter);


    private static async Task DownloadAndImport(
        WebSocket webSocket,
        IImportService importService,
        IEventLogService logService,
        CancellationToken cancellationToken)
    {
        async Task SendProgressReport(string message)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "msg", payload = $"{message}" }));
                await webSocket.SendAsync(
                    new ArraySegment<byte>(payload, 0, payload.Length),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }

        async Task SendTooFastReport()
        {
            if (webSocket.State == WebSocketState.Open)
            {
                byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "toofast", payload = $"" }));
                await webSocket.SendAsync(
                    new ArraySegment<byte>(payload, 0, payload.Length),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }

        async Task SendProgressFinished()
        {
            if (webSocket.State == WebSocketState.Open)
            {
                byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "finished", payload = $"" }));
                await webSocket.SendAsync(
                    new ArraySegment<byte>(payload, 0, payload.Length),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }

        async Task SendConfirmHeader()
        {
            if (webSocket.State == WebSocketState.Open)
            {
                byte[] msg = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "headerConfirmed", payload = "" }));
                await webSocket.SendAsync(
                    new ArraySegment<byte>(msg, 0, msg.Length),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }

        var header = (await ReceiveHeader(webSocket, default))?.ToObject<Message>()?.Payload;
        var context = new ImportContext(
            header?.ContactGroup,
            header?.BatchSize ?? 50000,
            header?.Delimiter ?? ",",
            header?.ImportKind ?? ImportKind.InsertAndSkipExisting
        );

        await SendConfirmHeader();

        bool consumerIsRunning = true;
        var ms = new AsynchronousStream(1024 * 32 * 500);

        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await importService.RunImport(ms, context, async (result, totalProcessed) =>
                    await SendProgressReport($"Total processed {totalProcessed} CachedBlocks: {ms.CachedBlocks}"),
                    async exception => await SendProgressReport($"{exception}"));
                await SendProgressReport("...finished");
            }
            finally
            {
                consumerIsRunning = false;
            }
        }, cancellationToken);

        var producerTask = Task.Run(async () =>
        {
            WebSocketReceiveResult? receiveResult = null;
            int bufferSize = 1024 * 32;

            while (true)
            {
                if (!consumerIsRunning)
                {
                    break;
                }

                byte[] buffer = new byte[bufferSize];
                receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (receiveResult.MessageType == WebSocketMessageType.Text)
                {
                    string messageText = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    var message = JsonConvert.DeserializeObject<Message>(messageText);

                    if (message is not null && message.Type == "done")
                    {
                        break; // End of communication, close socket
                    }
                }

                if (!receiveResult.CloseStatus.HasValue)
                {
                    var data = new ArraySegment<byte>(buffer);

                    if (data.Array != null)
                    {
                        await ms.WriteAsync(data, cancellationToken);
                        await ms.FlushAsync(cancellationToken);
                    }

                    int count = receiveResult.Count;
                    byte[] response = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "progress", payload = count }));
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(response, 0, response.Length),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);

                    if (ms.CachedBlocks > 3500)
                    {
                        await SendTooFastReport();
                        await SendProgressReport($"Too fast, waiting 3s CachedBlocks: {ms.CachedBlocks}");
                        await Task.Delay(3000, cancellationToken);
                    }
                }
                else
                {
                    break;
                }
            }
        }, cancellationToken);

        bool socketAvailable = true;

        try
        {
            await producerTask;
        }
        catch (SocketException se)
        {
            socketAvailable = false;
            logService.LogException(SOURCE, "PRODUCER", se);
        }
        catch (Exception e)
        {
            logService.LogException(SOURCE, "PRODUCER", e);
            await SendProgressReport($"{e}");
        }
        finally
        {
            ms.TryCompleteWriting();
        }

        try
        {
            await consumerTask;
        }
        catch (SocketException se)
        {
            logService.LogException(SOURCE, "CONSUMER", se);
            socketAvailable = false;
        }
        catch (Exception e)
        {
            logService.LogException(SOURCE, "CONSUMER", e);
            await SendProgressReport($"{e}");
        }

#pragma warning disable S2589 // Boolean expressions should not be gratuitous
        if (socketAvailable)
        {
            await SendProgressFinished();
            await Task.Delay(1000, cancellationToken);
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Standard closing",
                CancellationToken.None);
        }
#pragma warning restore S2589 // Boolean expressions should not be gratuitous
    }


    private static async Task<JObject?> ReceiveHeader(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        const int bufferSize = 1024 * 32;

        while (true)
        {
            byte[] buffer = new byte[bufferSize];
            var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (!receiveResult.CloseStatus.HasValue)
            {
                var data = new ArraySegment<byte>(buffer);

                if (data.Array != null)
                {
                    await ms.WriteAsync(data, cancellationToken);
                    await ms.FlushAsync(cancellationToken);
                }
            }
            else
            {
                break;
            }

            if (receiveResult.EndOfMessage)
            {
                break;
            }
        }

        ms.Seek(0, SeekOrigin.Begin);

        using var sr = new StreamReader(ms);
        string msg = await sr.ReadToEndAsync();
        var deserialized = JObject.Parse(msg);

        return deserialized;
    }
}
