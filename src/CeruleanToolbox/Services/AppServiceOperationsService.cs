using System.Text.Json;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record WebAppInfo(string Name, string Location, string State, string ResourceGroup, string Id);

public sealed class AppServiceOperationsService
{
    private const string WebApiVersion = "2023-12-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public AppServiceOperationsService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    /// <summary>Lists every App Service (function apps excluded, like the portal) across the subscription.</summary>
    public async Task<List<WebAppInfo>> ListAllWebAppsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<WebAppInfo>();
        await foreach (var site in subscription.GetWebSitesAsync(ct))
        {
            if (site.Data.Kind?.Contains("functionapp", StringComparison.OrdinalIgnoreCase) == true) continue;

            result.Add(new WebAppInfo(
                site.Data.Name, site.Data.Location.Name, site.Data.State ?? "Unknown",
                site.Id!.ResourceGroupName ?? "-", site.Id!.ToString()));
        }

        return result;
    }

    public async Task StartAsync(string resourceId, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(resourceId));
        await site.StartAsync(ct);
    }

    public async Task StopAsync(string resourceId, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(resourceId));
        await site.StopAsync(ct);
    }

    public async Task RestartAsync(string resourceId, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(resourceId));
        await site.RestartAsync(cancellationToken: ct);
    }

    /// <summary>Portal-style overview of an App Service or a Function App (both are Microsoft.Web/sites).</summary>
    public async Task<ResourceOverview> GetOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var siteDoc = await _arm.GetAsync(resourceId, WebApiVersion, ct: ct);
        var site = siteDoc.RootElement;
        var p = ArmJson.Obj(site, "properties");

        // Site configuration and plan are separate resources; a missing permission on either must
        // not hide the rest of the overview.
        using var configDoc = await TryGetAsync($"{resourceId}/config/web", ct);
        var config = configDoc is null ? default : ArmJson.Obj(configDoc.RootElement, "properties");

        var planId = ArmJson.Str(p, "serverFarmId");
        using var planDoc = planId == "-" ? null : await TryGetAsync(planId, ct);
        var plan = planDoc?.RootElement ?? default;
        var planSku = ArmJson.Obj(plan, "sku");

        var host = ArmJson.Str(p, "defaultHostName");
        var isLinux = ArmJson.Str(p, "reserved") == T("Yes");
        var state = ArmJson.Str(p, "state");

        var sections = new List<InfoSection>
        {
            new(T("Essentials"), new[]
            {
                ArmJson.Row(T("Status"), state),
                ArmJson.Row(T("Type"), ArmJson.Str(site, "kind")),
                ArmJson.Row("Location", ArmJson.Str(site, "location")),
                ArmJson.Row(T("Default URL"), host == "-" ? "-" : $"https://{host}"),
                ArmJson.Row(T("Operating system"), isLinux ? "Linux" : "Windows"),
                ArmJson.Row(T("Runtime stack"), Runtime(config)),
                ArmJson.Row(T("App Service plan"), ArmJson.LastSegment(planId)),
                ArmJson.Row(T("Pricing plan"), PlanDescription(planSku)),
                ArmJson.Row(T("Instances"), ArmJson.Str(config, "numberOfWorkers")),
                ArmJson.Row(T("Platform"), ArmJson.Str(p, "platformVersion")),
                ArmJson.Row(T("Last modified"), ArmJson.Date(ArmJson.Str(p, "lastModifiedTimeUtc"))),
            }),
            new(T("Networking"), new[]
            {
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess")),
                ArmJson.Row(T("VNet integration (subnet)"), ArmJson.LastSegment(ArmJson.Str(p, "virtualNetworkSubnetId"))),
                ArmJson.Row(T("Route all traffic through the VNet"), ArmJson.Str(p, "vnetRouteAllEnabled")),
                ArmJson.Row(T("Inbound IP addresses"), ArmJson.Str(p, "possibleInboundIpAddresses")),
                ArmJson.Row(T("Outbound IP addresses"), ArmJson.Str(p, "outboundIpAddresses").TrimEnd(',')),
                ArmJson.Row(T("IP mode"), ArmJson.Str(p, "ipMode")),
            }),
            new(T("Security and configuration"), new[]
            {
                ArmJson.Row(T("HTTPS only"), ArmJson.Str(p, "httpsOnly")),
                ArmJson.Row(T("Minimum TLS version"), ArmJson.Str(config, "minTlsVersion")),
                ArmJson.Row("FTPS", ArmJson.Str(config, "ftpsState")),
                ArmJson.Row("HTTP/2", ArmJson.Str(config, "http20Enabled")),
                ArmJson.Row("Always On", ArmJson.Str(config, "alwaysOn")),
                ArmJson.Row(T("32-bit worker"), ArmJson.Str(config, "use32BitWorkerProcess")),
                ArmJson.Row(T("Web sockets"), ArmJson.Str(config, "webSocketsEnabled")),
                ArmJson.Row(T("Client affinity (ARR)"), ArmJson.Str(p, "clientAffinityEnabled")),
                ArmJson.Row(T("Client certificates"), ArmJson.Str(p, "clientCertMode")),
                ArmJson.Row(T("Built-in authentication"), ArmJson.Str(config, "siteAuthEnabled")),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(site, "identity"), "type")),
                ArmJson.Row(T("Deployment source"), ArmJson.Str(config, "scmType")),
                ArmJson.Row(T("Remote debugging"), ArmJson.Str(config, "remoteDebuggingEnabled")),
            }),
        };

        var hostNames = ArmJson.Items(p, "enabledHostNames")
            .Select(h => (IReadOnlyList<string>)new[] { h.GetString() ?? "-" }).ToList();

        var privateEndpoints = ArmJson.Items(p, "privateEndpointConnections")
            .Select(c => (IReadOnlyList<string>)new[]
            {
                ArmJson.Str(c, "name"),
                ArmJson.Str(ArmJson.Obj(ArmJson.Obj(c, "properties"), "privateLinkServiceConnectionState"), "status"),
            }).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Host names"), new[] { T("Name") }, hostNames),
            new(T("Private endpoints"), new[] { T("Name"), T("Connection status") }, privateEndpoints),
        };

        var tags = ArmJson.Obj(site, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(state, sections, tables, tags);
    }

    private async Task<JsonDocument?> TryGetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _arm.GetAsync(path, WebApiVersion, ct: ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string PlanDescription(JsonElement sku)
    {
        var name = ArmJson.Str(sku, "name");
        if (name == "-") return "-";

        var tier = ArmJson.Str(sku, "tier");
        var capacity = ArmJson.Str(sku, "capacity");
        return $"{name}{(tier == "-" ? "" : $" ({tier})")}{(capacity == "-" ? "" : " · " + T("Instances: {0}", capacity))}";
    }

    // Exactly one of these language fields is filled in, depending on the stack.
    private static string Runtime(JsonElement config)
    {
        foreach (var key in new[] { "linuxFxVersion", "windowsFxVersion" })
        {
            if (ArmJson.Str(config, key) is var value && value != "-") return value;
        }

        var parts = new List<string>();
        void Add(string label, string key)
        {
            if (ArmJson.Str(config, key) is var value && value != "-") parts.Add($"{label} {value}");
        }

        Add(".NET", "netFrameworkVersion");
        Add("Node", "nodeVersion");
        Add("Python", "pythonVersion");
        Add("PHP", "phpVersion");
        Add("Java", "javaVersion");
        Add("PowerShell", "powerShellVersion");
        return parts.Count > 0 ? string.Join(", ", parts) : "-";
    }
}
