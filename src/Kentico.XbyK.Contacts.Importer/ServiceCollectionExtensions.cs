using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kentico.Xperience.Contacts.Importer
{
    using System.Net.WebSockets;
    using System.Text;
    using Kentico.Xperience.Contacts.Importer.Auxiliary;
    using Kentico.Xperience.Contacts.Importer.Services;
    using Microsoft.Extensions.DependencyInjection;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public static class ServiceCollectionExtensions
    {
        public static void AddContactsImport(this IServiceCollection services)
        {
            services.AddTransient<IImportService, ImportService>();
        }

        public static void UseContactsImport(this IApplicationBuilder app)
        {
            app.UseWebSockets();

            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                if (context.Request.Path == "/contactsimport/ws")
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        var importService = context.RequestServices.GetRequiredService<IImportService>();

                        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                        await DownloadAndImport(webSocket, importService);
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
            });
        }

        private record Message(string Type, HeaderPayload? Payload);

        private record HeaderPayload(string ImportKind, Guid? ContactGroup, int? BatchSize, string Delimiter);
        private static async Task DownloadAndImport(WebSocket webSocket, IImportService importService)
        {
            async Task SendProgressReport(string message)
            {
                var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "msg", payload = $"{message}" }));
                await webSocket.SendAsync(new ArraySegment<byte>(payload, 0, payload.Length), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            async Task SendTooFastReport()
            {
                var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "toofast", payload =  $"" }));
                await webSocket.SendAsync(new ArraySegment<byte>(payload, 0, payload.Length), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            async Task SendProgressFinished()
            {
                var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "finished", payload = $"" }));
                await webSocket.SendAsync(new ArraySegment<byte>(payload, 0, payload.Length), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            var header = (await ReceiveHeader(webSocket))?.ToObject<Message>()?.Payload;
            var context = new ImportContext(
                header?.ContactGroup,
                header?.BatchSize ?? 50000,
                header?.Delimiter ?? ",",
                header?.ImportKind ?? ImportKind.InsertAndSkipExisting
            );
            
            var msg = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "headerConfirmed", payload = "" }));
            await webSocket.SendAsync(new ArraySegment<byte>(msg, 0, msg.Length), WebSocketMessageType.Text, true, CancellationToken.None);
            
            var consumerIsRunning = true;
            var ms = new AsynchronousStream(1024 * 32 * 500);
            var consumerTask = Task.Run(async () =>
            {
                try
                {
                    await importService.RunImport(ms, context, async (result, totalProcessed) => { await SendProgressReport($"Total processed {totalProcessed} CachedBlocks: {ms.CachedBlocks}"); });
                    await SendProgressReport($"...finished");
                    await SendProgressFinished();
                }
                catch (Exception ex)
                {
                    await SendProgressReport($"{ex}");
                }
                finally
                {
                    consumerIsRunning = false;
                }
            });

            WebSocketReceiveResult? receiveResult = null;
            try
            {
                var bufferSize = 1024 * 32;
                while (true)
                {
                    if (!consumerIsRunning)
                    {
                        break;
                    }

                    var buffer = new byte[bufferSize];
                    receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (!receiveResult.CloseStatus.HasValue)
                    {
                        var data = new ArraySegment<byte>(buffer);

                        ms.Write(data.Array, data.Offset, receiveResult.Count);
                        ms.Flush();

                        var count = receiveResult.Count;
                        var response = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "progress", payload = count }));
                        await webSocket.SendAsync(new ArraySegment<byte>(response, 0, response.Length), WebSocketMessageType.Text, true, CancellationToken.None);

                        if (ms.CachedBlocks > 8500)
                        {
                            await SendTooFastReport();
                            await SendProgressReport($"Too fast, waiting 3s CachedBlocks: {ms.CachedBlocks}");
                            await Task.Delay(3000);
                        }
                        // if (receiveResult.Count < bufferSize)
                        // {
                        //     break;
                        // }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                await SendProgressReport($"{ex}");
                await SendProgressFinished();
            }
            finally
            {
                ms.CompleteWriting();
                await consumerTask;
                if (receiveResult != null)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        receiveResult.CloseStatusDescription,
                        CancellationToken.None);
                }
                // ms.Dispose();
            }
        }

        private static async Task<JObject?> ReceiveHeader(WebSocket webSocket)
        {
            var ms = new MemoryStream();
            const int bufferSize = 1024 * 32;
            while (true)
            {
                var buffer = new byte[bufferSize];
                var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (!receiveResult.CloseStatus.HasValue)
                {
                    var data = new ArraySegment<byte>(buffer);
        
                    ms.Write(data.Array, data.Offset, receiveResult.Count);
                    ms.Flush();
        
                    // var count = receiveResult.Count;
                    // var response = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "progress", payload = count }));
                    // await webSocket.SendAsync(new ArraySegment<byte>(response, 0, response.Length), WebSocketMessageType.Text, true, CancellationToken.None);
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
            
            var serializer = new Newtonsoft.Json.JsonSerializer();
            using var sr = new StreamReader(ms);
            var msg = sr.ReadToEnd();
            // using var jsonTextReader = new JsonTextReader(sr);
            
            // var deserialized = serializer.Deserialize<JsonDocument>(jsonTextReader);
            var deserialized = JObject.Parse(msg);
            return deserialized;
        }
    }
}