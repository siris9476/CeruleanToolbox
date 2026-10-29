using CeruleanToolbox.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CeruleanToolbox;

/// <summary>
/// Registers CeruleanToolbox's own services (Azure SDK wrappers, connection manager) — shared
/// between the web project's <c>Program.cs</c> (a <c>WebApplicationBuilder</c>) and the desktop
/// shell's plain <c>ServiceCollection</c> (CeruleanToolbox.Desktop), which don't share a common host
/// type since the desktop shell hosts components via BlazorWebView, not Kestrel.
/// </summary>
public static class CeruleanToolboxServiceCollectionExtensions
{
    /// <param name="connectionStoreFolder">Folder under %LocalAppData% that holds the saved connections and the language; a different name gives a separate store (for demos and tests).</param>
    public static IServiceCollection AddCeruleanToolboxServices(this IServiceCollection services, string connectionStoreFolder = "CeruleanToolbox")
    {
        services.AddToolboxCore(connectionStoreFolder);
        Tr.RegisterAssembly(typeof(CeruleanToolboxServiceCollectionExtensions).Assembly);
        services.AddScoped<PageStateStore>();
        services.AddScoped<ResourceInventoryService>();
        services.AddScoped<ExtensionCatalogService>();
        services.AddScoped<VmOperationsService>();
        services.AddScoped<AppServiceOperationsService>();
        services.AddScoped<ResourceGraphQueryService>();
        services.AddScoped<DeploymentService>();
        services.AddScoped<KeyVaultManagementService>();
        services.AddScoped<KeyVaultSecretService>();
        services.AddScoped<KeyVaultKeyService>();
        services.AddScoped<KeyVaultCertificateService>();
        services.AddScoped<KeyVaultDeletedService>();
        services.AddScoped<FunctionAppService>();
        services.AddScoped<RoleAssignmentService>();
        services.AddScoped<AksClusterService>();
        services.AddScoped<ApplicationGatewayService>();
        services.AddScoped<SqlManagementService>();
        services.AddScoped<StorageManagementService>();
        services.AddScoped<AppInsightsService>();
        services.AddScoped<BastionService>();
        services.AddScoped<LogAnalyticsService>();
        services.AddScoped<AutomationService>();
        services.AddScoped<ContainerRegistryService>();
        services.AddScoped<AppConfigurationService>();
        services.AddScoped<NetworkingService>();
        services.AddScoped<ComputeExtrasService>();
        services.AddScoped<MonitoringService>();
        services.AddScoped<IdentityService>();
        services.AddScoped<TerminalSessionService>();
        services.AddScoped<ClusterReachabilityService>();
        services.AddScoped<ActivityLogService>();

        return services;
    }
}
