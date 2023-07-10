using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kentico.Xperience.Contacts.Importer
{
    public static class ServiceCollectionExtensions
    {
        public static void UseContactsImport(this IApplicationBuilder app)
        {
            app.UseWebSockets();

            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                if (context.Request.Path == "/contactsimport/ws")
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        //var importService = context.RequestServices.GetRequiredService<IImportService>();

                        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

                        //await DownloadAndImport(webSocket, importService);
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
    }
}
