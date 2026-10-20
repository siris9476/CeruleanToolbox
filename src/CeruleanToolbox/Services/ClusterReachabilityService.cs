using System.Text.Json;

namespace CeruleanToolbox.Services;

/// <summary>
/// Estimates which VMs can reach a private AKS cluster's API server from ARM metadata alone: the VM's virtual network
/// must resolve the cluster's private DNS zone (the zone has a link to it) and must be the cluster's own network or a
/// connected peer of it. NSGs, firewalls and custom routes are not evaluated, so this is a strong hint, not a guarantee.
/// </summary>
public sealed class ClusterReachabilityService
{
    private readonly ArmRestClient _arm;

    public ClusterReachabilityService(ArmRestClient arm)
    {
        _arm = arm;
    }

    /// <summary>
    /// Ids (lower-case) of the Linux VMs in the cluster's subscription that reach it, or null when the cluster does not
    /// use a customer-managed private DNS zone (public cluster, or a system-managed zone) and nothing can be inferred.
    /// </summary>
    public async Task<HashSet<string>?> GetLinuxVmIdsReachingClusterAsync(string clusterId, CancellationToken ct = default)
    {
        using var cluster = await _arm.GetAsync(clusterId, "2024-05-01", ct: ct);
        var props = Child(cluster.RootElement, "properties");
        var zoneId = Text(Child(Child(props, "apiServerAccessProfile"), "privateDNSZone"));
        if (zoneId is null || !zoneId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)) return null;

        var nodeSubnet = Text(Child(FirstItem(Child(props, "agentPoolProfiles")), "vnetSubnetID"));
        if (nodeSubnet is null) return null;

        var clusterVnet = VnetOfSubnet(nodeSubnet);

        // Networks that resolve the cluster's private zone.
        var linked = new HashSet<string>();
        using (var links = await _arm.GetAsync(zoneId + "/virtualNetworkLinks", "2024-06-01", ct: ct))
        {
            foreach (var link in Items(links.RootElement))
            {
                var vnetId = Text(Child(Child(Child(link, "properties"), "virtualNetwork"), "id"));
                if (vnetId is not null) linked.Add(vnetId.ToLowerInvariant());
            }
        }

        // Networks that can route to the cluster's network: itself and its connected peers.
        var routable = new HashSet<string> { clusterVnet };
        using (var vnet = await _arm.GetAsync(clusterVnet, "2024-05-01", ct: ct))
        {
            foreach (var peering in Items(Child(Child(vnet.RootElement, "properties"), "virtualNetworkPeerings")))
            {
                var peeringProps = Child(peering, "properties");
                var remote = Text(Child(Child(peeringProps, "remoteVirtualNetwork"), "id"));
                if (remote is not null && Text(Child(peeringProps, "peeringState")) == "Connected")
                {
                    routable.Add(remote.ToLowerInvariant());
                }
            }
        }

        var reaching = linked.Where(routable.Contains).ToHashSet();
        var subscription = clusterId.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
        var result = new HashSet<string>();

        using var vms = await _arm.GetAsync($"/subscriptions/{subscription}/providers/Microsoft.Compute/virtualMachines", "2024-03-01", ct: ct);
        foreach (var vm in Items(vms.RootElement))
        {
            var vmProps = Child(vm, "properties");
            if (Text(Child(Child(Child(vmProps, "storageProfile"), "osDisk"), "osType")) != "Linux") continue;

            var vmId = Text(Child(vm, "id"));
            if (vmId is null) continue;

            foreach (var nic in Items(Child(Child(vmProps, "networkProfile"), "networkInterfaces")))
            {
                var nicId = Text(Child(nic, "id"));
                if (nicId is null) continue;

                using var nicDoc = await _arm.GetAsync(nicId, "2024-05-01", ct: ct);
                var onReachingVnet = Items(Child(Child(nicDoc.RootElement, "properties"), "ipConfigurations")).Any(ip =>
                {
                    var subnet = Text(Child(Child(Child(ip, "properties"), "subnet"), "id"));
                    return subnet is not null && reaching.Contains(VnetOfSubnet(subnet));
                });

                if (onReachingVnet)
                {
                    result.Add(vmId.ToLowerInvariant());
                    break;
                }
            }
        }

        return result;
    }

    private static string VnetOfSubnet(string subnetId)
    {
        var index = subnetId.IndexOf("/subnets/", StringComparison.OrdinalIgnoreCase);
        return (index < 0 ? subnetId : subnetId[..index]).ToLowerInvariant();
    }

    // Tolerant JSON access: ARM omits properties that have their default value, so a missing one is just "nothing".
    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static JsonElement FirstItem(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 ? array[0] : default;

    private static IEnumerable<JsonElement> Items(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object) element = Child(element, "value");
        return element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    private static string? Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
