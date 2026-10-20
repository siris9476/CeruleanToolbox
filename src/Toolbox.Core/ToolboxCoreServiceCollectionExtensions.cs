using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Toolbox.Core.Services;

namespace Toolbox.Core;

/// <summary>Registers the services every app built on Toolbox.Core needs: saved connections, Azure clients and the VM diagnostics.</summary>
public static class ToolboxCoreServiceCollectionExtensions
{
    /// <param name="connectionStoreFolder">
    /// Name of the app's own folder under <c>%LocalAppData%</c> where its connections are kept (for example "CeruleanToolbox").
    /// Each app passes its own, so apps never share a connection file.
    /// </param>
    public static IServiceCollection AddToolboxCore(this IServiceCollection services, string connectionStoreFolder)
    {
        Tr.Initialize(connectionStoreFolder);
        Tr.RegisterAssembly(typeof(ToolboxCoreServiceCollectionExtensions).Assembly);

        services.AddSingleton(_ => new ConnectionManagerService(connectionStoreFolder));
        services.AddSingleton<AzureClientFactory>();
        services.AddSingleton<ArmRestClient>();
        services.AddSingleton<ResourceGraphClient>();
        services.AddScoped<VmDiagnosticsService>();

        return services;
    }

    /// <summary>
    /// <c>POST /api/connections/{id}/export</c>: a plain HTML form post (not a Blazor call) so the browser downloads the encrypted
    /// file; Blazor Server has no built-in way to push a byte[] to the client as a download. Web apps only.
    /// </summary>
    public static IEndpointRouteBuilder MapConnectionExport(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/connections/{id}/export", async (string id, HttpRequest request, ConnectionManagerService connectionManager) =>
        {
            try
            {
                var form = await request.ReadFormAsync();
                var passphrase = form["passphrase"].ToString();
                if (string.IsNullOrEmpty(passphrase))
                {
                    return Results.BadRequest("Passphrase mancante.");
                }

                var bytes = connectionManager.ExportEncrypted(id, passphrase);
                var connection = connectionManager.GetAll().FirstOrDefault(c => c.Id == id);
                var safeName = string.Concat((connection?.Name ?? "connection").Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "connection";

                return Results.File(bytes, "application/octet-stream", $"{safeName}.ceruleantoolbox");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        return app;
    }
}
