using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.ResourceManager.ContainerService;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record AksClusterInfo(string Name, string Location, string PowerState, string ResourceGroup, string Id);

public sealed record AksInfoSection(string Title, IReadOnlyList<KeyValuePair<string, string>> Items);

public sealed record AksClusterOverview(
    string ProvisioningState, string PowerState, IReadOnlyList<AksInfoSection> Sections,
    IReadOnlyList<KeyValuePair<string, string>> Tags);

public sealed record AksNodeInstance(string Name, string ProvisioningState, string PowerState, string Zone);

public sealed record AksNodePoolDetail(
    string Name, string Mode, string VmSize, int Count, string KubernetesVersion, string NodeImageVersion,
    string Scaling, string ProvisioningState, string PowerState, string OsType, string OsSku, string OsDisk,
    string Zones, string MaxPods, string MaxSurge, string MaxUnavailable, string Subnet, string ScaleDownMode,
    string Priority, string Labels, string Taints, bool AutoScaling, IReadOnlyList<string> AvailableVersions,
    IReadOnlyList<AksNodeInstance> Nodes);

public sealed record AksUpgradeInfo(string CurrentVersion, IReadOnlyList<string> AvailableVersions);

public sealed class AksClusterService
{
    private const string ManagementEndpoint = "https://management.azure.com";
    private const string ManagementScope = "https://management.azure.com/.default";
    private const string ApiVersion = "2026-06-01";
    private const string ComputeApiVersion = "2024-07-01";
    private static readonly HttpClient Http = new();

    private readonly AzureClientFactory _clientFactory;

    public AksClusterService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<List<AksClusterInfo>> ListAllClustersAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<AksClusterInfo>();
        await foreach (ContainerServiceManagedClusterResource cluster in subscription.GetContainerServiceManagedClustersAsync(ct))
        {
            result.Add(new AksClusterInfo(
                cluster.Data.Name,
                cluster.Data.Location.Name,
                cluster.Data.PowerStateCode?.ToString() ?? "Unknown",
                cluster.Id!.ResourceGroupName ?? "-",
                cluster.Id!.ToString()));
        }

        return result;
    }

    public async Task StartAsync(string resourceId, CancellationToken ct = default)
    {
        var cluster = _clientFactory.GetClient().GetContainerServiceManagedClusterResource(new Azure.Core.ResourceIdentifier(resourceId));
        await cluster.StartAsync(WaitUntil.Started, ct);
    }

    public async Task StopAsync(string resourceId, CancellationToken ct = default)
    {
        var cluster = _clientFactory.GetClient().GetContainerServiceManagedClusterResource(new Azure.Core.ResourceIdentifier(resourceId));
        await cluster.StopAsync(WaitUntil.Started, ct);
    }

    // Raw JSON throughout: the SDK's typed models throw on some responses (e.g. agentPoolProfiles
    // returned as null, or an upgrades array missing entirely for node pools already at the max).
    public async Task<AksClusterOverview> GetOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(resourceId, ct);
        var root = doc.RootElement;
        var p = root.GetProperty("properties");

        static JsonElement Obj(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;

        static string Str(JsonElement e, string key)
        {
            var v = Obj(e, key);
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } text ? text : "-",
                JsonValueKind.True => T("Yes"),
                JsonValueKind.False => T("No"),
                JsonValueKind.Number => v.ToString(),
                _ => "-",
            };
        }

        static string LastSegment(string value) => value == "-" ? value : value[(value.LastIndexOf('/') + 1)..];

        var network = Obj(p, "networkProfile");
        var aad = Obj(p, "aadProfile");
        var api = Obj(p, "apiServerAccessProfile");
        var autoUpgrade = Obj(p, "autoUpgradeProfile");
        var sku = Obj(root, "sku");
        var oidc = Obj(p, "oidcIssuerProfile");
        var isPrivate = Str(api, "enablePrivateCluster") == T("Yes");
        var current = Str(p, "currentKubernetesVersion") is var cv && cv != "-" ? cv : Str(p, "kubernetesVersion");

        static KeyValuePair<string, string> Row(string label, string value) => new(label, value);

        var sections = new List<AksInfoSection>
        {
            new(T("Basics"), new[]
            {
                Row("Provisioning", Str(p, "provisioningState")),
                Row("Power state", Obj(p, "powerState") is var ps ? Str(ps, "code") : "-"),
                Row(T("Kubernetes version"), current),
                Row("Location", Str(root, "location")),
                Row(T("Pricing tier"), Str(sku, "tier")),
                Row(T("Support plan"), Str(p, "supportPlan")),
                Row(T("Node resource group"), Str(p, "nodeResourceGroup")),
            }),
            new(T("API server and access"), new[]
            {
                Row(T("Private cluster"), isPrivate ? T("Yes") : T("No")),
                Row(T("API server FQDN"), isPrivate ? Str(p, "privateFQDN") : Str(p, "fqdn")),
                Row("Private DNS zone", LastSegment(Str(api, "privateDNSZone"))),
                Row(T("Identity"), Str(Obj(root, "identity"), "type")),
                Row(T("Kubernetes RBAC"), Str(p, "enableRBAC")),
                Row(T("Microsoft Entra ID integration"), Str(aad, "managed")),
                Row(T("Azure RBAC for Kubernetes"), Str(aad, "enableAzureRBAC")),
                Row(T("Local accounts disabled"), Str(p, "disableLocalAccounts")),
                Row("OIDC issuer", Str(oidc, "enabled")),
            }),
            new(T("Networking"), new[]
            {
                Row("Network plugin", Str(network, "networkPlugin")),
                Row("Network policy", Str(network, "networkPolicy")),
                Row("Network dataplane", Str(network, "networkDataplane")),
                Row("Service CIDR", Str(network, "serviceCidr")),
                Row("DNS service IP", Str(network, "dnsServiceIP")),
                Row("Pod CIDR", Str(network, "podCidr")),
                Row("Load balancer SKU", Str(network, "loadBalancerSku")),
                Row("Outbound type", Str(network, "outboundType")),
            }),
            new(T("Automatic upgrades"), new[]
            {
                Row(T("Kubernetes upgrade channel"), Str(autoUpgrade, "upgradeChannel")),
                Row(T("Node OS upgrade channel"), Str(autoUpgrade, "nodeOSUpgradeChannel")),
            }),
        };

        var tags = new List<KeyValuePair<string, string>>();
        if (Obj(root, "tags") is { ValueKind: JsonValueKind.Object } tagObject)
        {
            tags.AddRange(tagObject.EnumerateObject().Select(t => Row(t.Name, t.Value.ToString())));
        }

        return new AksClusterOverview(
            Str(p, "provisioningState"), Obj(p, "powerState") is var power ? Str(power, "code") : "-", sections, tags);
    }

    public async Task<AksUpgradeInfo> GetUpgradeInfoAsync(string resourceId, CancellationToken ct = default)
    {
        var cluster = _clientFactory.GetClient().GetContainerServiceManagedClusterResource(new Azure.Core.ResourceIdentifier(resourceId));
        var current = (await cluster.GetAsync(ct)).Value.Data.CurrentKubernetesVersion ?? "-";

        using var controlPlane = await GetJsonAsync($"{resourceId}/upgradeProfiles/default", ct);
        var controlPlaneProfile = controlPlane.RootElement.GetProperty("properties").GetProperty("controlPlaneProfile");
        var controlPlaneUpgrades = ReadVersions(Upgrades(controlPlaneProfile));

        return new AksUpgradeInfo(current, controlPlaneUpgrades);
    }

    public async Task<List<AksNodePoolDetail>> GetNodePoolsAsync(string resourceId, CancellationToken ct = default)
    {
        var identifier = new Azure.Core.ResourceIdentifier(resourceId);

        using var cluster = await GetJsonAsync(resourceId, ct);
        var nodeResourceGroup = cluster.RootElement.GetProperty("properties").GetProperty("nodeResourceGroup").GetString()!;

        // Instances live in the cluster's managed node resource group, one VMSS per node pool,
        // linked back to the pool through the aks-managed-poolName tag.
        var instancesByPool = new Dictionary<string, (string VmSize, List<AksNodeInstance> Nodes)>(StringComparer.OrdinalIgnoreCase);
        var scaleSetsPath = $"/subscriptions/{identifier.SubscriptionId}/resourceGroups/{nodeResourceGroup}/providers/Microsoft.Compute/virtualMachineScaleSets";
        using var scaleSets = await GetJsonAsync(scaleSetsPath, ct, ComputeApiVersion);
        foreach (var set in scaleSets.RootElement.GetProperty("value").EnumerateArray())
        {
            if (!set.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("aks-managed-poolName", out var poolTag)) continue;

            var vmSize = set.TryGetProperty("sku", out var sku) && sku.TryGetProperty("name", out var skuName) ? skuName.GetString() ?? "-" : "-";
            var nodes = new List<AksNodeInstance>();
            using var vms = await GetJsonAsync(
                $"{scaleSetsPath}/{set.GetProperty("name").GetString()}/virtualMachines", ct, ComputeApiVersion, "$expand=instanceView");
            foreach (var vm in vms.RootElement.GetProperty("value").EnumerateArray())
            {
                var props = vm.GetProperty("properties");
                var power = "-";
                if (props.TryGetProperty("instanceView", out var view) && view.TryGetProperty("statuses", out var statuses))
                {
                    power = statuses.EnumerateArray()
                        .Select(s => s.GetProperty("code").GetString() ?? "")
                        .FirstOrDefault(c => c.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))?["PowerState/".Length..] ?? "-";
                }

                var zone = vm.TryGetProperty("zones", out var zones) && zones.ValueKind == JsonValueKind.Array && zones.GetArrayLength() > 0
                    ? zones[0].GetString() ?? "-" : "-";
                nodes.Add(new AksNodeInstance(
                    vm.GetProperty("name").GetString() ?? "-",
                    props.TryGetProperty("provisioningState", out var ps) ? ps.GetString() ?? "-" : "-",
                    power, zone));
            }

            instancesByPool[poolTag.GetString()!] = (vmSize, nodes);
        }

        using var pools = await GetJsonAsync($"{resourceId}/agentPools", ct);
        var result = new List<AksNodePoolDetail>();
        foreach (var pool in pools.RootElement.GetProperty("value").EnumerateArray())
        {
            var name = pool.GetProperty("name").GetString()!;
            var p = pool.GetProperty("properties");
            string Text(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "-";

            var autoscaling = p.TryGetProperty("enableAutoScaling", out var auto) && auto.ValueKind == JsonValueKind.True;
            var scaling = autoscaling
                ? $"Auto ({(p.TryGetProperty("minCount", out var min) ? min.ToString() : "?")}-{(p.TryGetProperty("maxCount", out var max) ? max.ToString() : "?")})"
                : T("Manual");

            string Number(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.ToString() : "-";
            string Join(JsonElement e, Func<JsonProperty, string> render) =>
                e.ValueKind == JsonValueKind.Object ? string.Join(", ", e.EnumerateObject().Select(render)) : "";
            string ListOf(string key) =>
                p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", v.EnumerateArray().Select(x => x.ToString())) : "";

            using var poolProfile = await GetJsonAsync($"{resourceId}/agentPools/{name}/upgradeProfiles/default", ct);
            var availableVersions = ReadVersions(Upgrades(poolProfile.RootElement.GetProperty("properties")));

            var (vmSize, nodes) = instancesByPool.TryGetValue(name, out var found)
                ? found : (Text("vmSize"), new List<AksNodeInstance>());

            var powerState = p.TryGetProperty("powerState", out var power) && power.TryGetProperty("code", out var code)
                ? code.GetString() ?? "-" : "-";
            p.TryGetProperty("upgradeSettings", out var up);
            string UpgradeValue(string key) =>
                up.ValueKind == JsonValueKind.Object && up.TryGetProperty(key, out var v) ? v.ToString() : "-";

            var subnet = Text("vnetSubnetID");
            if (subnet != "-") subnet = subnet[(subnet.LastIndexOf('/') + 1)..];
            var osDisk = Number("osDiskSizeGB") == "-" ? "-" : $"{Number("osDiskSizeGB")} GB ({Text("osDiskType")})";
            var labels = p.TryGetProperty("nodeLabels", out var nl) ? Join(nl, x => $"{x.Name}={x.Value}") : "";
            var taints = ListOf("nodeTaints");

            result.Add(new AksNodePoolDetail(
                name, Text("mode"), vmSize != "-" ? vmSize : Text("vmSize"),
                p.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : nodes.Count,
                Text("currentOrchestratorVersion") != "-" ? Text("currentOrchestratorVersion") : Text("orchestratorVersion"),
                Text("nodeImageVersion"), scaling, Text("provisioningState"), powerState,
                Text("osType"), Text("osSKU"), osDisk,
                ListOf("availabilityZones") is { Length: > 0 } zones ? zones : T("None"),
                Number("maxPods"), UpgradeValue("maxSurge"), UpgradeValue("maxUnavailable"), subnet,
                Text("scaleDownMode"), Text("scaleSetPriority") == "-" ? "Regular" : Text("scaleSetPriority"),
                labels.Length > 0 ? labels : T("None"), taints.Length > 0 ? taints : T("None"), autoscaling, availableVersions, nodes));
        }

        return result;
    }

    public async Task UpgradeControlPlaneAsync(string resourceId, string targetVersion, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var identifier = new Azure.Core.ResourceIdentifier(resourceId);
        var data = (await client.GetContainerServiceManagedClusterResource(identifier).GetAsync(ct)).Value.Data;
        data.KubernetesVersion = targetVersion;

        var resourceGroup = client.GetResourceGroupResource(
            ResourceGroupResource.CreateResourceIdentifier(identifier.SubscriptionId!, identifier.ResourceGroupName!));
        await resourceGroup.GetContainerServiceManagedClusters().CreateOrUpdateAsync(WaitUntil.Started, data.Name, data, ct);
    }

    public Task UpgradeNodePoolAsync(string resourceId, string poolName, string targetVersion, CancellationToken ct = default) =>
        UpdateNodePoolAsync(resourceId, poolName, props => props["orchestratorVersion"] = targetVersion, ct);

    public Task ScaleNodePoolAsync(string resourceId, string poolName, int nodeCount, CancellationToken ct = default) =>
        UpdateNodePoolAsync(resourceId, poolName, props => props["count"] = nodeCount, ct);

    // GET-modify-PUT: agent pools have no partial update, so the whole current definition is sent back.
    private async Task UpdateNodePoolAsync(string resourceId, string poolName, Action<JsonNode> change, CancellationToken ct)
    {
        var url = $"{ManagementEndpoint}{resourceId}/agentPools/{poolName}?api-version={ApiVersion}";

        using var getRequest = await CreateRequestAsync(HttpMethod.Get, url, ct);
        using var getResponse = await Http.SendAsync(getRequest, ct);
        await EnsureSuccessAsync(getResponse, ct);
        var pool = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync(ct))!;
        change(pool["properties"]!);

        using var putRequest = await CreateRequestAsync(HttpMethod.Put, url, ct);
        putRequest.Content = new StringContent(pool.ToJsonString(), Encoding.UTF8, "application/json");
        using var putResponse = await Http.SendAsync(putRequest, ct);
        await EnsureSuccessAsync(putResponse, ct);
    }

    // Azure explains why it refused (e.g. pool busy, version skew) in the body; surface it.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var message = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var m))
            {
                message = m.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException(T("Azure refused the operation ({0}): {1}", (int)response.StatusCode, message));
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct, string? apiVersion = null, string? extraQuery = null)
    {
        var query = $"{(extraQuery is null ? "" : extraQuery + "&")}api-version={apiVersion ?? ApiVersion}";
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{ManagementEndpoint}{path}?{query}", ct);
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await _clientFactory.GetCredential().GetTokenAsync(new TokenRequestContext(new[] { ManagementScope }), ct);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return request;
    }

    private static JsonElement Upgrades(JsonElement profile) =>
        profile.TryGetProperty("upgrades", out var upgrades) ? upgrades : default;

    private static List<string> ReadVersions(JsonElement upgrades)
    {
        var versions = new List<string>();
        if (upgrades.ValueKind == JsonValueKind.Array)
        {
            foreach (var upgrade in upgrades.EnumerateArray())
            {
                versions.Add(upgrade.GetProperty("kubernetesVersion").GetString()!);
            }
        }

        return versions;
    }
}
