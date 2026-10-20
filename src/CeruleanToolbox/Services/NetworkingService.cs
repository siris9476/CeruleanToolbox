using System.Text.Json;
using Azure.ResourceManager.Cdn;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.PrivateDns;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.TrafficManager;

namespace CeruleanToolbox.Services;

public sealed record VNetSubnetInfo(string Name, string? AddressPrefix, string? NsgName);

public sealed record VNetInfo(
    string Name, string Location, string ResourceGroup, List<string> AddressSpace,
    List<VNetSubnetInfo> Subnets, string Id);

public sealed record NsgInfo(
    string Name, string Location, string ResourceGroup, int RuleCount,
    int AssociatedSubnets, int AssociatedNics, string Id);

public sealed record NsgRuleInfo(
    string Name, string Direction, string Access, string Protocol,
    string SourcePrefix, string SourcePort, string DestinationPrefix, string DestinationPort,
    int? Priority, bool IsDefault);

public sealed record PublicIpInfo(
    string Name, string Location, string ResourceGroup, string? IpAddress,
    string AllocationMethod, string? AssociatedResource, string Id);

public sealed record LoadBalancerInfo(
    string Name, string Location, string ResourceGroup, string Sku,
    List<string> FrontendIps, int BackendPoolCount, int RuleCount, string Id);

public sealed record PrivateEndpointInfo(
    string Name, string Location, string ResourceGroup, string? SubnetName,
    string? VNetName, string Id);

public sealed record NetworkInterfaceInfo(
    string Name, string Location, string ResourceGroup, string? PrivateIp,
    string? PublicIpName, string? AttachedTo, string Id);

public sealed record PrivateDnsZoneLinkInfo(string Name, string? VNetName, bool RegistrationEnabled);

public sealed record PrivateDnsZoneInfo(
    string Name, string ResourceGroup, int RecordCount,
    List<PrivateDnsZoneLinkInfo> VirtualNetworkLinks, string Id);

public sealed record TrafficManagerProfileInfo(
    string Name, string ResourceGroup, string? Status, string? RoutingMethod,
    int EndpointCount, string Id);

public sealed record NetworkWatcherInfo(string Name, string Location, string ResourceGroup, string Id);

public sealed record WafPolicyInfo(
    string Name, string Location, string ResourceGroup, string? Mode, int CustomRuleCount, string Id);

public sealed record CdnEndpointInfo(string Name, string? HostName, bool IsFrontDoor);

public sealed record CdnProfileInfo(
    string Name, string Location, string ResourceGroup, string Sku,
    List<CdnEndpointInfo> Endpoints, string Id);

public sealed class NetworkingService
{
    private const string NetworkApiVersion = "2024-05-01";
    private const string PrivateDnsApiVersion = "2020-06-01";
    private const string TrafficManagerApiVersion = "2022-04-01";
    private const string CdnApiVersion = "2023-05-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public NetworkingService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    private static List<KeyValuePair<string, string>> Tags(JsonElement resource) =>
        ArmJson.Obj(resource, "tags") is { ValueKind: JsonValueKind.Object } tags
            ? tags.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

    // A subnet id is .../virtualNetworks/{vnet}/subnets/{subnet}: show it as "vnet / subnet".
    private static string SubnetLabel(string subnetId)
    {
        if (subnetId == "-") return "-";

        var parts = subnetId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, x => string.Equals(x, "virtualNetworks", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 3 < parts.Length ? $"{parts[index + 1]} / {parts[index + 3]}" : ArmJson.LastSegment(subnetId);
    }

    public async Task<ResourceOverview> GetVNetOverviewAsync(string vnetResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(vnetResourceId, NetworkApiVersion, ct: ct);
        var vnet = doc.RootElement;
        var p = ArmJson.Obj(vnet, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var addressSpace = string.Join(", ", ArmJson.Items(ArmJson.Obj(p, "addressSpace"), "addressPrefixes").Select(a => a.GetString()));
        var dnsServers = string.Join(", ", ArmJson.Items(ArmJson.Obj(p, "dhcpOptions"), "dnsServers").Select(a => a.GetString()));
        var ddosPlan = ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "ddosProtectionPlan"), "id"));
        var subnets = ArmJson.Items(p, "subnets").ToList();
        var connectedDevices = subnets.Sum(sn => ArmJson.Items(ArmJson.Obj(sn, "properties"), "ipConfigurations").Count());

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(vnet, "location")),
                ArmJson.Row(T("Address space"), addressSpace.Length > 0 ? addressSpace : "-"),
                ArmJson.Row(T("DNS servers"), dnsServers.Length > 0 ? dnsServers : T("Azure defaults")),
                ArmJson.Row("Subnet", subnets.Count.ToString()),
                ArmJson.Row(T("Connected devices"), connectedDevices.ToString()),
                ArmJson.Row("Peering", ArmJson.Items(p, "virtualNetworkPeerings").Count().ToString()),
            }),
            new(T("Security and options"), new[]
            {
                ArmJson.Row(T("DDoS protection"), ddosPlan != "-" ? T("Plan {0}", ddosPlan) : (ArmJson.Str(p, "enableDdosProtection") == T("Yes") ? T("Active") : T("Inactive"))),
                ArmJson.Row(T("Virtual network encryption"), ArmJson.Str(ArmJson.Obj(p, "encryption"), "enabled")),
                ArmJson.Row(T("Flow timeout (minutes)"), ArmJson.Str(p, "flowTimeoutInMinutes")),
                ArmJson.Row(T("BGP community"), ArmJson.Str(ArmJson.Obj(p, "bgpCommunities"), "virtualNetworkCommunity")),
            }),
        };

        string Join(JsonElement e, string key, Func<JsonElement, string> pick) =>
            string.Join(", ", ArmJson.Items(e, key).Select(pick)) is { Length: > 0 } text ? text : "-";

        var subnetRows = subnets.Select(sn =>
        {
            var sp = ArmJson.Obj(sn, "properties");
            var prefix = ArmJson.Str(sp, "addressPrefix") != "-"
                ? ArmJson.Str(sp, "addressPrefix")
                : Join(sp, "addressPrefixes", x => x.GetString() ?? "");
            return (IReadOnlyList<string>)new[]
            {
                ArmJson.Str(sn, "name"), prefix,
                ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(sp, "networkSecurityGroup"), "id")),
                ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(sp, "routeTable"), "id")),
                ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(sp, "natGateway"), "id")),
                Join(sp, "delegations", x => ArmJson.Str(ArmJson.Obj(x, "properties"), "serviceName")),
                Join(sp, "serviceEndpoints", x => ArmJson.Str(x, "service")),
                ArmJson.Items(sp, "privateEndpoints").Count().ToString(),
                ArmJson.Items(sp, "ipConfigurations").Count().ToString(),
            };
        }).ToList();

        var peerings = ArmJson.Items(p, "virtualNetworkPeerings").Select(pe =>
        {
            var pp = ArmJson.Obj(pe, "properties");
            return (IReadOnlyList<string>)new[]
            {
                ArmJson.Str(pe, "name"), ArmJson.Str(pp, "peeringState"),
                ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(pp, "remoteVirtualNetwork"), "id")),
                ArmJson.Str(pp, "allowVirtualNetworkAccess"), ArmJson.Str(pp, "allowForwardedTraffic"),
                ArmJson.Str(pp, "allowGatewayTransit"), ArmJson.Str(pp, "useRemoteGateways"),
            };
        }).ToList();

        var tables = new List<InfoTable>
        {
            new("Subnet", new[] { T("Name"), "Prefisso", "NSG", "Route table", "NAT gateway", "Delegazione", "Service endpoint", "Endpoint privati", "IP in uso" }, subnetRows),
            new("Peering", new[] { T("Name"), T("Status"), "VNet remota", "Accesso VNet", "Traffico inoltrato", "Transito gateway", "Usa gateway remoto" }, peerings),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(vnet));
    }

    // Describes the resource an id points to, e.g. ".../loadBalancers/lb1/frontendIPConfigurations/x" -> "Load balancer lb1".
    private static string DescribeOwner(string id)
    {
        if (id == "-") return T("Not associated");

        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string After(string segment)
        {
            var index = Array.FindIndex(parts, x => string.Equals(x, segment, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "";
        }

        foreach (var (segment, label) in new[]
        {
            ("networkInterfaces", "Network interface"), ("loadBalancers", "Load balancer"), ("applicationGateways", "Application Gateway"),
            ("bastionHosts", "Bastion"), ("virtualNetworkGateways", "VPN/ExpressRoute gateway"), ("natGateways", "NAT gateway"),
            ("azureFirewalls", "Azure Firewall"), ("virtualMachines", T("Virtual machine")), ("privateEndpoints", T("Private endpoint")),
        })
        {
            if (After(segment) is { Length: > 0 } name) return $"{label} {name}";
        }

        return ArmJson.LastSegment(id);
    }

    public async Task<ResourceOverview> GetPublicIpOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, NetworkApiVersion, ct: ct);
        var ip = doc.RootElement;
        var p = ArmJson.Obj(ip, "properties");
        var sku = ArmJson.Obj(ip, "sku");
        var dns = ArmJson.Obj(p, "dnsSettings");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(ip, "location")),
                ArmJson.Row(T("IP address"), ArmJson.Str(p, "ipAddress")),
                ArmJson.Row(T("IP version"), ArmJson.Str(p, "publicIPAddressVersion")),
                ArmJson.Row(T("Allocation"), ArmJson.Str(p, "publicIPAllocationMethod")),
                ArmJson.Row("SKU", $"{ArmJson.Str(sku, "name")} ({ArmJson.Str(sku, "tier")})"),
                ArmJson.Row(T("Zones"), string.Join(", ", ArmJson.Items(ip, "zones").Select(z => z.ToString()).Order()) is { Length: > 0 } zones ? zones : T("None")),
                ArmJson.Row(T("Idle timeout (minutes)"), ArmJson.Str(p, "idleTimeoutInMinutes")),
            }),
            new(T("Association and DNS"), new[]
            {
                ArmJson.Row(T("Associated to"), DescribeOwner(ArmJson.Str(ArmJson.Obj(p, "ipConfiguration"), "id"))),
                ArmJson.Row("NAT gateway", ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "natGateway"), "id"))),
                ArmJson.Row(T("DNS name label"), ArmJson.Str(dns, "domainNameLabel")),
                ArmJson.Row("FQDN", ArmJson.Str(dns, "fqdn")),
                ArmJson.Row(T("DDoS protection"), ArmJson.Str(ArmJson.Obj(p, "ddosSettings"), "protectionMode")),
            }),
        };

        return new ResourceOverview(provisioning, sections, Array.Empty<InfoTable>(), Tags(ip));
    }

    public async Task<ResourceOverview> GetLoadBalancerOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, NetworkApiVersion, ct: ct);
        var lb = doc.RootElement;
        var p = ArmJson.Obj(lb, "properties");
        var sku = ArmJson.Obj(lb, "sku");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(lb, "location")),
                ArmJson.Row("SKU", $"{ArmJson.Str(sku, "name")} ({ArmJson.Str(sku, "tier")})"),
                ArmJson.Row("Frontend IP", ArmJson.Items(p, "frontendIPConfigurations").Count().ToString()),
                ArmJson.Row(T("Backend pools"), ArmJson.Items(p, "backendAddressPools").Count().ToString()),
                ArmJson.Row(T("Load balancing rules"), ArmJson.Items(p, "loadBalancingRules").Count().ToString()),
                ArmJson.Row(T("Health probes"), ArmJson.Items(p, "probes").Count().ToString()),
                ArmJson.Row(T("Inbound NAT rules"), ArmJson.Items(p, "inboundNatRules").Count().ToString()),
                ArmJson.Row(T("Outbound rules"), ArmJson.Items(p, "outboundRules").Count().ToString()),
            }),
        };

        // Public IP addresses are separate resources; resolve them for the frontend table.
        var frontends = new List<IReadOnlyList<string>>();
        foreach (var f in ArmJson.Items(p, "frontendIPConfigurations"))
        {
            var fp = ArmJson.Obj(f, "properties");
            var publicId = ArmJson.Str(ArmJson.Obj(fp, "publicIPAddress"), "id");
            var address = "-";
            if (publicId != "-")
            {
                try
                {
                    using var ipDoc = await _arm.GetAsync(publicId, NetworkApiVersion, ct: ct);
                    address = ArmJson.Str(ArmJson.Obj(ipDoc.RootElement, "properties"), "ipAddress");
                }
                catch (InvalidOperationException)
                {
                }
            }

            frontends.Add(new[]
            {
                ArmJson.Str(f, "name"), publicId == "-" ? T("Internal") : T("Public"), address,
                ArmJson.Str(fp, "privateIPAddress"), SubnetLabel(ArmJson.Str(ArmJson.Obj(fp, "subnet"), "id")),
            });
        }

        string Ref(JsonElement e, string key) => ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(ArmJson.Obj(e, "properties"), key), "id"));
        string Prop(JsonElement e, string key) => ArmJson.Str(ArmJson.Obj(e, "properties"), key);

        var pools = ArmJson.Items(p, "backendAddressPools").Select(b =>
        {
            var bp = ArmJson.Obj(b, "properties");
            var members = ArmJson.Items(bp, "loadBalancerBackendAddresses").Count();
            if (members == 0) members = ArmJson.Items(bp, "backendIPConfigurations").Count();
            return (IReadOnlyList<string>)new[] { ArmJson.Str(b, "name"), members.ToString() };
        }).ToList();

        var rules = ArmJson.Items(p, "loadBalancingRules").Select(r => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(r, "name"), Prop(r, "protocol"), Prop(r, "frontendPort"), Prop(r, "backendPort"),
            Ref(r, "frontendIPConfiguration"), Ref(r, "backendAddressPool"), Ref(r, "probe"),
            Prop(r, "idleTimeoutInMinutes"), Prop(r, "enableFloatingIP"),
        }).ToList();

        var probes = ArmJson.Items(p, "probes").Select(pr => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(pr, "name"), Prop(pr, "protocol"), Prop(pr, "port"), Prop(pr, "requestPath"),
            $"{Prop(pr, "intervalInSeconds")} s", Prop(pr, "numberOfProbes"),
        }).ToList();

        var natRules = ArmJson.Items(p, "inboundNatRules").Select(n => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(n, "name"), Prop(n, "protocol"), Prop(n, "frontendPort"), Prop(n, "backendPort"), Ref(n, "frontendIPConfiguration"),
        }).ToList();

        var tables = new List<InfoTable>
        {
            new("Frontend IP", new[] { T("Name"), T("Type"), T("Public IP"), T("Private IP"), "Subnet" }, frontends),
            new(T("Backend pools"), new[] { T("Name"), T("Members") }, pools),
            new(T("Load balancing rules"), new[] { T("Name"), T("Protocol"), T("Frontend port"), T("Backend port"), "Frontend", "Pool", "Probe", T("Idle timeout"), "Floating IP" }, rules),
            new(T("Health probes"), new[] { T("Name"), T("Protocol"), T("Port"), T("Path"), T("Interval"), T("Attempts") }, probes),
            new(T("Inbound NAT rules"), new[] { T("Name"), T("Protocol"), T("Frontend port"), T("Backend port"), "Frontend" }, natRules),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(lb));
    }

    public async Task<ResourceOverview> GetNetworkInterfaceOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, NetworkApiVersion, ct: ct);
        var nic = doc.RootElement;
        var p = ArmJson.Obj(nic, "properties");
        var dns = ArmJson.Obj(p, "dnsSettings");
        var provisioning = ArmJson.Str(p, "provisioningState");

        string Servers(string key) => string.Join(", ", ArmJson.Items(dns, key).Select(x => x.GetString())) is { Length: > 0 } text ? text : "";
        var dnsText = Servers("dnsServers") is { Length: > 0 } custom ? custom : T("Azure defaults");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(nic, "location")),
                ArmJson.Row(T("Attached to"), DescribeOwner(ArmJson.Str(ArmJson.Obj(p, "virtualMachine"), "id")) is var owner && owner != T("Not associated")
                    ? owner : DescribeOwner(ArmJson.Str(ArmJson.Obj(p, "privateEndpoint"), "id"))),
                ArmJson.Row(T("MAC address"), ArmJson.Str(p, "macAddress")),
                ArmJson.Row(T("Primary NIC"), ArmJson.Str(p, "primary")),
                ArmJson.Row("Network security group", ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "networkSecurityGroup"), "id"))),
            }),
            new(T("Network settings"), new[]
            {
                ArmJson.Row("Accelerated networking", ArmJson.Str(p, "enableAcceleratedNetworking")),
                ArmJson.Row("IP forwarding", ArmJson.Str(p, "enableIPForwarding")),
                ArmJson.Row(T("DNS servers"), dnsText),
                ArmJson.Row(T("Internal DNS label"), ArmJson.Str(dns, "internalDnsNameLabel")),
                ArmJson.Row(T("Internal FQDN"), ArmJson.Str(dns, "internalFqdn")),
            }),
        };

        var configs = new List<IReadOnlyList<string>>();
        foreach (var c in ArmJson.Items(p, "ipConfigurations"))
        {
            var cp = ArmJson.Obj(c, "properties");
            var publicId = ArmJson.Str(ArmJson.Obj(cp, "publicIPAddress"), "id");
            var publicAddress = "-";
            if (publicId != "-")
            {
                try
                {
                    using var ipDoc = await _arm.GetAsync(publicId, NetworkApiVersion, ct: ct);
                    publicAddress = ArmJson.Str(ArmJson.Obj(ipDoc.RootElement, "properties"), "ipAddress");
                }
                catch (InvalidOperationException)
                {
                }
            }

            configs.Add(new[]
            {
                ArmJson.Str(c, "name"), ArmJson.Str(cp, "primary"), ArmJson.Str(cp, "privateIPAddress"),
                ArmJson.Str(cp, "privateIPAllocationMethod"), publicId == "-" ? "-" : $"{ArmJson.LastSegment(publicId)} ({publicAddress})",
                SubnetLabel(ArmJson.Str(ArmJson.Obj(cp, "subnet"), "id")),
            });
        }

        var tables = new List<InfoTable>
        {
            new(T("IP configurations"), new[] { T("Name"), T("Primary"), T("Private IP"), T("Allocation"), T("Public IP"), "VNet / subnet" }, configs),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(nic));
    }

    // "/subscriptions/x/.../providers/Microsoft.Storage/storageAccounts/acc" -> "Microsoft.Storage/storageAccounts · acc"
    private static string TargetLabel(string resourceId)
    {
        if (resourceId == "-") return "-";

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, x => string.Equals(x, "providers", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 3 < parts.Length ? $"{parts[index + 1]}/{parts[index + 2]} · {parts[index + 3]}" : ArmJson.LastSegment(resourceId);
    }

    public async Task<ResourceOverview> GetPrivateEndpointOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, NetworkApiVersion, ct: ct);
        var pe = doc.RootElement;
        var p = ArmJson.Obj(pe, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var connections = ArmJson.Items(p, "privateLinkServiceConnections").Select(c => (Auto: true, Item: c))
            .Concat(ArmJson.Items(p, "manualPrivateLinkServiceConnections").Select(c => (Auto: false, Item: c)))
            .Select(x =>
            {
                var cp = ArmJson.Obj(x.Item, "properties");
                var state = ArmJson.Obj(cp, "privateLinkServiceConnectionState");
                return (IReadOnlyList<string>)new[]
                {
                    ArmJson.Str(x.Item, "name"), TargetLabel(ArmJson.Str(cp, "privateLinkServiceId")),
                    string.Join(", ", ArmJson.Items(cp, "groupIds").Select(g => g.GetString())) is { Length: > 0 } groups ? groups : "-",
                    ArmJson.Str(state, "status"), ArmJson.Str(state, "description"), x.Auto ? T("Automatic") : T("Manual"),
                };
            }).ToList();

        var nics = ArmJson.Items(p, "networkInterfaces").Select(n => ArmJson.LastSegment(ArmJson.Str(n, "id"))).ToList();
        var firstStatus = connections.Count > 0 ? connections[0][3] : "-";

        var dnsConfigs = ArmJson.Items(p, "customDnsConfigs").Select(c => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(c, "fqdn"), string.Join(", ", ArmJson.Items(c, "ipAddresses").Select(a => a.GetString())),
        }).ToList();

        // The DNS zone groups are child resources; a missing permission must not hide the rest.
        var zoneGroups = new List<IReadOnlyList<string>>();
        try
        {
            using var groupDoc = await _arm.GetAsync($"{resourceId}/privateDnsZoneGroups", NetworkApiVersion, ct: ct);
            foreach (var group in ArmJson.Items(groupDoc.RootElement, "value"))
            {
                foreach (var zone in ArmJson.Items(ArmJson.Obj(group, "properties"), "privateDnsZoneConfigs"))
                {
                    zoneGroups.Add(new[]
                    {
                        ArmJson.Str(group, "name"),
                        ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(zone, "properties"), "privateDnsZoneId")),
                    });
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row(T("Connection status"), firstStatus),
                ArmJson.Row("Location", ArmJson.Str(pe, "location")),
                ArmJson.Row(T("Virtual network / subnet"), SubnetLabel(ArmJson.Str(ArmJson.Obj(p, "subnet"), "id"))),
                ArmJson.Row("Network interface", nics.Count > 0 ? string.Join(", ", nics) : "-"),
                ArmJson.Row(T("Custom NIC name"), ArmJson.Str(p, "customNetworkInterfaceName")),
            }),
        };

        var tables = new List<InfoTable>
        {
            new(T("Service connections"), new[] { T("Name"), T("Resource"), T("Sub-resource"), T("Status"), T("Description"), T("Type") }, connections),
            new(T("DNS configuration"), new[] { "FQDN", "IP" }, dnsConfigs),
            new(T("Associated private DNS zones"), new[] { T("Group"), T("Zone") }, zoneGroups),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(pe));
    }

    public async Task<ResourceOverview> GetPrivateDnsZoneOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, PrivateDnsApiVersion, ct: ct);
        var zone = doc.RootElement;
        var p = ArmJson.Obj(zone, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row(T("Zone name"), ArmJson.Str(zone, "name")),
                ArmJson.Row("Record set", T("{0} of {1}", ArmJson.Str(p, "numberOfRecordSets"), ArmJson.Str(p, "maxNumberOfRecordSets"))),
                ArmJson.Row(T("Virtual network links"), T("{0} of {1}", ArmJson.Str(p, "numberOfVirtualNetworkLinks"), ArmJson.Str(p, "maxNumberOfVirtualNetworkLinks"))),
                ArmJson.Row(T("Of which with auto-registration"), T("{0} of {1}", ArmJson.Str(p, "numberOfVirtualNetworkLinksWithRegistration"), ArmJson.Str(p, "maxNumberOfVirtualNetworkLinksWithRegistration"))),
            }),
        };

        var links = new List<IReadOnlyList<string>>();
        try
        {
            using var linkDoc = await _arm.GetAsync($"{resourceId}/virtualNetworkLinks", PrivateDnsApiVersion, ct: ct);
            foreach (var link in ArmJson.Items(linkDoc.RootElement, "value"))
            {
                var lp = ArmJson.Obj(link, "properties");
                links.Add(new[]
                {
                    ArmJson.Str(link, "name"), ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(lp, "virtualNetwork"), "id")),
                    ArmJson.Str(lp, "registrationEnabled"), ArmJson.Str(lp, "virtualNetworkLinkState"),
                });
            }
        }
        catch (InvalidOperationException)
        {
        }

        static string Value(JsonElement r, string name)
        {
            var rp = ArmJson.Obj(r, "properties");
            string Join(string key, string field) =>
                string.Join(", ", ArmJson.Items(rp, key).Select(x => ArmJson.Str(x, field)));

            return ArmJson.Str(r, "type").Split('/').LastOrDefault() switch
            {
                "A" => Join("aRecords", "ipv4Address"),
                "AAAA" => Join("aaaaRecords", "ipv6Address"),
                "CNAME" => ArmJson.Str(ArmJson.Obj(rp, "cnameRecord"), "cname"),
                "PTR" => Join("ptrRecords", "ptrdname"),
                "MX" => Join("mxRecords", "exchange"),
                "SRV" => Join("srvRecords", "target"),
                "TXT" => string.Join(", ", ArmJson.Items(rp, "txtRecords").SelectMany(t => ArmJson.Items(t, "value")).Select(v => v.GetString())),
                "SOA" => T("(SOA record)"),
                _ => "-",
            };
        }

        var records = new List<IReadOnlyList<string>>();
        try
        {
            using var recordDoc = await _arm.GetAsync($"{resourceId}/ALL", PrivateDnsApiVersion, "$top=100", ct);
            foreach (var r in ArmJson.Items(recordDoc.RootElement, "value"))
            {
                records.Add(new[]
                {
                    ArmJson.Str(r, "name"), ArmJson.Str(r, "type").Split('/').LastOrDefault() ?? "-",
                    ArmJson.Str(ArmJson.Obj(r, "properties"), "ttl"), Value(r, ArmJson.Str(r, "name")),
                });
            }
        }
        catch (InvalidOperationException)
        {
        }

        var tables = new List<InfoTable>
        {
            new(T("Virtual network links"), new[] { T("Name"), T("Virtual network"), T("Auto-registration"), T("Status") }, links),
            new(T("Record sets (first 100)"), new[] { T("Name"), T("Type"), "TTL", T("Value") }, records),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(zone));
    }

    private async Task<JsonDocument?> TryGetAsync(string path, string apiVersion, CancellationToken ct, string? extraQuery = null)
    {
        try
        {
            return await _arm.GetAsync(path, apiVersion, extraQuery, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<ResourceOverview> GetTrafficManagerOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, TrafficManagerApiVersion, ct: ct);
        var profile = doc.RootElement;
        var p = ArmJson.Obj(profile, "properties");
        var dns = ArmJson.Obj(p, "dnsConfig");
        var monitor = ArmJson.Obj(p, "monitorConfig");
        var status = ArmJson.Str(p, "profileStatus");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Profile status"), status),
                ArmJson.Row(T("Monitoring status"), ArmJson.Str(monitor, "profileMonitorStatus")),
                ArmJson.Row(T("Routing method"), ArmJson.Str(p, "trafficRoutingMethod")),
                ArmJson.Row("FQDN", ArmJson.Str(dns, "fqdn")),
                ArmJson.Row(T("Relative DNS name"), ArmJson.Str(dns, "relativeName")),
                ArmJson.Row(T("DNS TTL (seconds)"), ArmJson.Str(dns, "ttl")),
                ArmJson.Row("Traffic View", ArmJson.Str(p, "trafficViewEnrollmentStatus")),
            }),
            new(T("Endpoint monitoring"), new[]
            {
                ArmJson.Row(T("Protocol"), ArmJson.Str(monitor, "protocol")),
                ArmJson.Row(T("Port"), ArmJson.Str(monitor, "port")),
                ArmJson.Row(T("Path"), ArmJson.Str(monitor, "path")),
                ArmJson.Row(T("Interval (seconds)"), ArmJson.Str(monitor, "intervalInSeconds")),
                ArmJson.Row(T("Timeout (seconds)"), ArmJson.Str(monitor, "timeoutInSeconds")),
                ArmJson.Row(T("Tolerated failures"), ArmJson.Str(monitor, "toleratedNumberOfFailures")),
            }),
        };

        var endpoints = ArmJson.Items(p, "endpoints").Select(e =>
        {
            var ep = ArmJson.Obj(e, "properties");
            var target = ArmJson.Str(ep, "target") != "-" ? ArmJson.Str(ep, "target") : ArmJson.LastSegment(ArmJson.Str(ep, "targetResourceId"));
            return (IReadOnlyList<string>)new[]
            {
                ArmJson.Str(e, "name"), (ArmJson.Str(e, "type").Split('/').LastOrDefault() ?? "-"), target,
                ArmJson.Str(ep, "endpointStatus"), ArmJson.Str(ep, "endpointMonitorStatus"),
                ArmJson.Str(ep, "weight"), ArmJson.Str(ep, "priority"), ArmJson.Str(ep, "endpointLocation"),
            };
        }).ToList();

        var tables = new List<InfoTable>
        {
            new("Endpoint", new[] { T("Name"), T("Type"), "Target", T("Status"), T("Monitoring status"), T("Weight"), T("Priority"), "Location" }, endpoints),
        };

        return new ResourceOverview(status, sections, tables, Tags(profile));
    }

    public async Task<ResourceOverview> GetWafPolicyOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, NetworkApiVersion, ct: ct);
        var policy = doc.RootElement;
        var p = ArmJson.Obj(policy, "properties");
        var settings = ArmJson.Obj(p, "policySettings");
        var managed = ArmJson.Obj(p, "managedRules");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(policy, "location")),
                ArmJson.Row(T("Policy status"), ArmJson.Str(settings, "state")),
                ArmJson.Row(T("Mode"), ArmJson.Str(settings, "mode")),
                ArmJson.Row(T("Associated application gateways"), ArmJson.Items(p, "applicationGateways").Count().ToString()),
                ArmJson.Row(T("Associated listeners"), ArmJson.Items(p, "httpListeners").Count().ToString()),
                ArmJson.Row(T("Associated path-based rules"), ArmJson.Items(p, "pathBasedRules").Count().ToString()),
            }),
            new(T("Settings"), new[]
            {
                ArmJson.Row(T("Request body inspection"), ArmJson.Str(settings, "requestBodyCheck")),
                ArmJson.Row(T("Maximum body size (KB)"), ArmJson.Str(settings, "maxRequestBodySizeInKb")),
                ArmJson.Row(T("Body inspection limit (KB)"), ArmJson.Str(settings, "requestBodyInspectLimitInKB")),
                ArmJson.Row(T("File upload limit (MB)"), ArmJson.Str(settings, "fileUploadLimitInMb")),
                ArmJson.Row(T("Block response status code"), ArmJson.Str(settings, "customBlockResponseStatusCode")),
            }),
        };

        var ruleSets = ArmJson.Items(managed, "managedRuleSets").Select(r => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(r, "ruleSetType"), ArmJson.Str(r, "ruleSetVersion"), ArmJson.Items(r, "ruleGroupOverrides").Count().ToString(),
        }).ToList();

        var customRules = ArmJson.Items(p, "customRules").Select(r => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(r, "name"), ArmJson.Str(r, "priority"), ArmJson.Str(r, "ruleType"), ArmJson.Str(r, "action"),
            ArmJson.Str(r, "state"), ArmJson.Items(r, "matchConditions").Count().ToString(),
        }).ToList();

        var exclusions = ArmJson.Items(managed, "exclusions").Select(e => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(e, "matchVariable"), ArmJson.Str(e, "selectorMatchOperator"), ArmJson.Str(e, "selector"),
        }).ToList();

        var gateways = ArmJson.Items(p, "applicationGateways")
            .Select(g => (IReadOnlyList<string>)new[] { ArmJson.LastSegment(ArmJson.Str(g, "id")) }).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Managed rule sets"), new[] { T("Type"), T("Version"), T("Overrides per group") }, ruleSets),
            new(T("Custom rules"), new[] { T("Name"), T("Priority"), T("Type"), T("Action"), T("Status"), T("Conditions") }, customRules),
            new(T("Exclusions"), new[] { T("Variable"), T("Operator"), T("Selector") }, exclusions),
            new(T("Associated application gateways"), new[] { T("Name") }, gateways),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(policy));
    }

    public async Task<ResourceOverview> GetCdnOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, CdnApiVersion, ct: ct);
        var profile = doc.RootElement;
        var p = ArmJson.Obj(profile, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row(T("Resource status"), ArmJson.Str(p, "resourceState")),
                ArmJson.Row(T("Type"), ArmJson.Str(profile, "kind")),
                ArmJson.Row("SKU", ArmJson.Str(ArmJson.Obj(profile, "sku"), "name")),
                ArmJson.Row("Location", ArmJson.Str(profile, "location")),
                ArmJson.Row("Front Door ID", ArmJson.Str(p, "frontDoorId")),
                ArmJson.Row(T("Origin response timeout (seconds)"), ArmJson.Str(p, "originResponseTimeoutSeconds")),
            }),
        };

        // A profile is either classic CDN (/endpoints) or Front Door (/afdEndpoints): ask both, one stays empty.
        var endpoints = new List<IReadOnlyList<string>>();
        foreach (var (segment, kind) in new[] { ("endpoints", "CDN"), ("afdEndpoints", "Front Door") })
        {
            using var epDoc = await TryGetAsync($"{resourceId}/{segment}", CdnApiVersion, ct);
            if (epDoc is null) continue;

            foreach (var e in ArmJson.Items(epDoc.RootElement, "value"))
            {
                var ep = ArmJson.Obj(e, "properties");
                endpoints.Add(new[]
                {
                    ArmJson.Str(e, "name"), kind, ArmJson.Str(ep, "hostName"),
                    ArmJson.Str(ep, "enabledState") != "-" ? ArmJson.Str(ep, "enabledState") : ArmJson.Str(ep, "resourceState"),
                    ArmJson.Str(ep, "provisioningState"),
                });
            }
        }

        var origins = new List<IReadOnlyList<string>>();
        var groups = new List<IReadOnlyList<string>>();
        using (var groupDoc = await TryGetAsync($"{resourceId}/originGroups", CdnApiVersion, ct))
        {
            foreach (var g in groupDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(groupDoc.RootElement, "value"))
            {
                var gp = ArmJson.Obj(g, "properties");
                var groupName = ArmJson.Str(g, "name");
                var probe = ArmJson.Obj(gp, "healthProbeSettings");
                groups.Add(new[]
                {
                    groupName, ArmJson.Str(gp, "provisioningState"), ArmJson.Str(probe, "probeProtocol"),
                    ArmJson.Str(probe, "probePath"), ArmJson.Str(probe, "probeIntervalInSeconds"),
                });

                using var originDoc = await TryGetAsync($"{resourceId}/originGroups/{groupName}/origins", CdnApiVersion, ct);
                foreach (var o in originDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(originDoc.RootElement, "value"))
                {
                    var op = ArmJson.Obj(o, "properties");
                    origins.Add(new[]
                    {
                        groupName, ArmJson.Str(o, "name"), ArmJson.Str(op, "hostName"), ArmJson.Str(op, "enabledState"),
                        ArmJson.Str(op, "priority"), ArmJson.Str(op, "weight"),
                    });
                }
            }
        }

        var domains = new List<IReadOnlyList<string>>();
        using (var domainDoc = await TryGetAsync($"{resourceId}/customDomains", CdnApiVersion, ct))
        {
            foreach (var d in domainDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(domainDoc.RootElement, "value"))
            {
                var dp = ArmJson.Obj(d, "properties");
                domains.Add(new[]
                {
                    ArmJson.Str(d, "name"), ArmJson.Str(dp, "hostName"),
                    ArmJson.Str(dp, "domainValidationState") != "-" ? ArmJson.Str(dp, "domainValidationState") : ArmJson.Str(dp, "customHttpsProvisioningState"),
                });
            }
        }

        var tables = new List<InfoTable>
        {
            new("Endpoint", new[] { T("Name"), T("Type"), "Host", T("Status"), "Provisioning" }, endpoints),
            new(T("Origin groups"), new[] { T("Name"), "Provisioning", T("Probe protocol"), T("Probe path"), T("Interval (s)") }, groups),
            new(T("Origins"), new[] { T("Group"), T("Name"), "Host", T("Status"), T("Priority"), T("Weight") }, origins),
            new(T("Custom domains"), new[] { T("Name"), "Host", T("Validation") }, domains),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(profile));
    }

    public async Task<ResourceOverview> GetNetworkWatcherOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, NetworkApiVersion, ct: ct);
        var watcher = doc.RootElement;
        var p = ArmJson.Obj(watcher, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var flowLogs = new List<IReadOnlyList<string>>();
        using (var flowDoc = await TryGetAsync($"{resourceId}/flowLogs", NetworkApiVersion, ct))
        {
            foreach (var f in flowDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(flowDoc.RootElement, "value"))
            {
                var fp = ArmJson.Obj(f, "properties");
                flowLogs.Add(new[]
                {
                    ArmJson.Str(f, "name"), ArmJson.LastSegment(ArmJson.Str(fp, "targetResourceId")),
                    ArmJson.LastSegment(ArmJson.Str(fp, "storageId")), ArmJson.Str(fp, "enabled"),
                    ArmJson.Str(ArmJson.Obj(fp, "retentionPolicy"), "days"),
                    ArmJson.Str(ArmJson.Obj(fp, "format"), "type"),
                });
            }
        }

        var monitors = new List<IReadOnlyList<string>>();
        using (var monitorDoc = await TryGetAsync($"{resourceId}/connectionMonitors", NetworkApiVersion, ct))
        {
            foreach (var m in monitorDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(monitorDoc.RootElement, "value"))
            {
                var mp = ArmJson.Obj(m, "properties");
                monitors.Add(new[]
                {
                    ArmJson.Str(m, "name"), ArmJson.Str(mp, "monitoringStatus"), ArmJson.Str(mp, "provisioningState"),
                    ArmJson.Items(mp, "endpoints").Count().ToString(), ArmJson.Items(mp, "testConfigurations").Count().ToString(),
                });
            }
        }

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(watcher, "location")),
                ArmJson.Row("Flow log", flowLogs.Count.ToString()),
                ArmJson.Row("Connection monitor", monitors.Count.ToString()),
            }),
        };

        var tables = new List<InfoTable>
        {
            new("Flow log", new[] { T("Name"), T("Monitored resource"), "Storage account", T("Enabled"), T("Retention (days)"), T("Format") }, flowLogs),
            new("Connection monitor", new[] { T("Name"), T("Monitoring status"), "Provisioning", "Endpoint", "Test" }, monitors),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(watcher));
    }

    public async Task<ResourceOverview> GetNsgOverviewAsync(string nsgResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(nsgResourceId, NetworkApiVersion, ct: ct);
        var nsg = doc.RootElement;
        var p = ArmJson.Obj(nsg, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var custom = ArmJson.Items(p, "securityRules").ToList();
        var inbound = custom.Count(r => ArmJson.Str(ArmJson.Obj(r, "properties"), "direction") == "Inbound");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(nsg, "location")),
                ArmJson.Row(T("Custom rules"), custom.Count.ToString()),
                ArmJson.Row(T("  of which inbound"), inbound.ToString()),
                ArmJson.Row(T("  of which outbound"), (custom.Count - inbound).ToString()),
                ArmJson.Row(T("Default rules"), ArmJson.Items(p, "defaultSecurityRules").Count().ToString()),
            }),
            new(T("Associations"), new[]
            {
                ArmJson.Row(T("Associated subnets"), ArmJson.Items(p, "subnets").Count().ToString()),
                ArmJson.Row(T("Associated network interfaces"), ArmJson.Items(p, "networkInterfaces").Count().ToString()),
                ArmJson.Row("Flow log", ArmJson.Items(p, "flowLogs").Count().ToString()),
            }),
        };

        var tables = new List<InfoTable>
        {
            new(T("Associated subnets"), new[] { "VNet / subnet" },
                ArmJson.Items(p, "subnets").Select(x => (IReadOnlyList<string>)new[] { SubnetLabel(ArmJson.Str(x, "id")) }).ToList()),
            new(T("Associated network interfaces"), new[] { "NIC" },
                ArmJson.Items(p, "networkInterfaces").Select(x => (IReadOnlyList<string>)new[] { ArmJson.LastSegment(ArmJson.Str(x, "id")) }).ToList()),
            new("Flow log", new[] { T("Name") },
                ArmJson.Items(p, "flowLogs").Select(x => (IReadOnlyList<string>)new[] { ArmJson.LastSegment(ArmJson.Str(x, "id")) }).ToList()),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(nsg));
    }

    private SubscriptionResource GetSubscription(string subscriptionId) =>
        _clientFactory.GetClient().GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

    public async Task<List<VNetInfo>> ListVNetsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<VNetInfo>();
        await foreach (VirtualNetworkResource vnet in GetSubscription(subscriptionId).GetVirtualNetworksAsync(ct))
        {
            var subnets = vnet.Data.Subnets.Select(s => new VNetSubnetInfo(
                s.Name, s.AddressPrefix, s.NetworkSecurityGroup?.Id?.Name)).ToList();

            result.Add(new VNetInfo(
                vnet.Data.Name,
                vnet.Data.Location?.Name ?? "-",
                vnet.Id.ResourceGroupName ?? "-",
                vnet.Data.AddressSpace.AddressPrefixes.ToList(),
                subnets,
                vnet.Id.ToString()));
        }

        return result;
    }

    public async Task<List<NsgInfo>> ListNsgsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<NsgInfo>();
        await foreach (NetworkSecurityGroupResource nsg in GetSubscription(subscriptionId).GetNetworkSecurityGroupsAsync(ct))
        {
            result.Add(new NsgInfo(
                nsg.Data.Name,
                nsg.Data.Location?.Name ?? "-",
                nsg.Id.ResourceGroupName ?? "-",
                nsg.Data.SecurityRules.Count,
                nsg.Data.Subnets.Count,
                nsg.Data.NetworkInterfaces.Count,
                nsg.Id.ToString()));
        }

        return result;
    }

    public async Task<List<NsgRuleInfo>> GetNsgRulesAsync(string nsgResourceId, CancellationToken ct = default)
    {
        var nsg = _clientFactory.GetClient().GetNetworkSecurityGroupResource(new Azure.Core.ResourceIdentifier(nsgResourceId));
        var data = (await nsg.GetAsync(cancellationToken: ct)).Value.Data;

        var rules = data.SecurityRules.Select(r => ToRuleInfo(r, isDefault: false))
            .Concat(data.DefaultSecurityRules.Select(r => ToRuleInfo(r, isDefault: true)))
            .OrderBy(r => r.IsDefault)
            .ThenBy(r => r.Priority)
            .ToList();

        return rules;
    }

    public async Task CreateOrUpdateNsgRuleAsync(
        string nsgResourceId, string ruleName, string direction, string access, string protocol,
        string sourcePrefix, string sourcePort, string destinationPrefix, string destinationPort,
        int priority, CancellationToken ct = default)
    {
        var nsg = _clientFactory.GetClient().GetNetworkSecurityGroupResource(new Azure.Core.ResourceIdentifier(nsgResourceId));

        var data = new SecurityRuleData
        {
            Direction = new SecurityRuleDirection(direction),
            Access = new SecurityRuleAccess(access),
            Protocol = new SecurityRuleProtocol(protocol),
            SourceAddressPrefix = sourcePrefix,
            SourcePortRange = sourcePort,
            DestinationAddressPrefix = destinationPrefix,
            DestinationPortRange = destinationPort,
            Priority = priority,
        };

        await nsg.GetSecurityRules().CreateOrUpdateAsync(Azure.WaitUntil.Completed, ruleName, data, ct);
    }

    public async Task DeleteNsgRuleAsync(string nsgResourceId, string ruleName, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the three lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteNsgRuleAsync() called for rule={ruleName} on {nsgResourceId} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        var nsg = _clientFactory.GetClient().GetNetworkSecurityGroupResource(new Azure.Core.ResourceIdentifier(nsgResourceId));
        var rule = await nsg.GetSecurityRules().GetAsync(ruleName, ct);
        await rule.Value.DeleteAsync(Azure.WaitUntil.Completed, ct);
    }

    private static NsgRuleInfo ToRuleInfo(SecurityRuleData r, bool isDefault) => new(
        r.Name,
        r.Direction?.ToString() ?? "-",
        r.Access?.ToString() ?? "-",
        r.Protocol?.ToString() ?? "-",
        r.SourceAddressPrefix ?? string.Join(",", r.SourceAddressPrefixes),
        r.SourcePortRange ?? string.Join(",", r.SourcePortRanges),
        r.DestinationAddressPrefix ?? string.Join(",", r.DestinationAddressPrefixes),
        r.DestinationPortRange ?? string.Join(",", r.DestinationPortRanges),
        r.Priority,
        isDefault);

    public async Task<List<PublicIpInfo>> ListPublicIpsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<PublicIpInfo>();
        await foreach (PublicIPAddressResource ip in GetSubscription(subscriptionId).GetPublicIPAddressesAsync(ct))
        {
            result.Add(new PublicIpInfo(
                ip.Data.Name,
                ip.Data.Location?.Name ?? "-",
                ip.Id.ResourceGroupName ?? "-",
                ip.Data.IPAddress,
                ip.Data.PublicIPAllocationMethod?.ToString() ?? "-",
                DescribeAssociation(ip.Data.IPConfiguration?.Id),
                ip.Id.ToString()));
        }

        return result;
    }

    public async Task<List<LoadBalancerInfo>> ListLoadBalancersAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<LoadBalancerInfo>();
        await foreach (LoadBalancerResource lb in GetSubscription(subscriptionId).GetLoadBalancersAsync(ct))
        {
            var frontendIps = lb.Data.FrontendIPConfigurations
                .Select(f => f.PrivateIPAddress ?? f.PublicIPAddress?.Id?.Name ?? "-")
                .ToList();

            result.Add(new LoadBalancerInfo(
                lb.Data.Name,
                lb.Data.Location?.Name ?? "-",
                lb.Id.ResourceGroupName ?? "-",
                lb.Data.Sku?.Name?.ToString() ?? "-",
                frontendIps,
                lb.Data.BackendAddressPools.Count,
                lb.Data.LoadBalancingRules.Count,
                lb.Id.ToString()));
        }

        return result;
    }

    public async Task<List<PrivateEndpointInfo>> ListPrivateEndpointsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<PrivateEndpointInfo>();
        await foreach (PrivateEndpointResource pe in GetSubscription(subscriptionId).GetPrivateEndpointsAsync(ct))
        {
            result.Add(new PrivateEndpointInfo(
                pe.Data.Name,
                pe.Data.Location?.Name ?? "-",
                pe.Id.ResourceGroupName ?? "-",
                pe.Data.Subnet?.Id?.Name,
                pe.Data.Subnet?.Id?.Parent?.Name,
                pe.Id.ToString()));
        }

        return result;
    }

    private static string? DescribeAssociation(Azure.Core.ResourceIdentifier? ipConfigId)
    {
        // An IP configuration's parent is the owning resource (NIC for a VM, or a Load Balancer's
        // frontend) — walking up one level gives a human-readable owner instead of the raw ipConfig id.
        if (ipConfigId is null) return null;
        var owner = ipConfigId.Parent;
        return owner is null ? null : $"{owner.ResourceType.Type}/{owner.Name}";
    }

    public async Task<List<NetworkInterfaceInfo>> ListNetworkInterfacesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<NetworkInterfaceInfo>();
        await foreach (NetworkInterfaceResource nic in GetSubscription(subscriptionId).GetNetworkInterfacesAsync(ct))
        {
            var ipConfig = nic.Data.IPConfigurations.FirstOrDefault();
            result.Add(new NetworkInterfaceInfo(
                nic.Data.Name,
                nic.Data.Location?.Name ?? "-",
                nic.Id.ResourceGroupName ?? "-",
                ipConfig?.PrivateIPAddress,
                ipConfig?.PublicIPAddress?.Id?.Name,
                nic.Data.VirtualMachineId?.Name,
                nic.Id.ToString()));
        }

        return result;
    }

    public async Task<List<PrivateDnsZoneInfo>> ListPrivateDnsZonesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<PrivateDnsZoneInfo>();
        await foreach (PrivateDnsZoneResource zone in GetSubscription(subscriptionId).GetPrivateDnsZonesAsync(top: null, ct))
        {
            var links = new List<PrivateDnsZoneLinkInfo>();
            await foreach (var link in zone.GetVirtualNetworkLinks().GetAllAsync(cancellationToken: ct))
            {
                links.Add(new PrivateDnsZoneLinkInfo(
                    link.Data.Name, link.Data.VirtualNetworkId?.Name, link.Data.RegistrationEnabled ?? false));
            }

            result.Add(new PrivateDnsZoneInfo(
                zone.Data.Name,
                zone.Id.ResourceGroupName ?? "-",
                (int)(zone.Data.NumberOfRecords ?? 0),
                links,
                zone.Id.ToString()));
        }

        return result;
    }

    public async Task<List<TrafficManagerProfileInfo>> ListTrafficManagerProfilesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<TrafficManagerProfileInfo>();
        await foreach (TrafficManagerProfileResource profile in GetSubscription(subscriptionId).GetTrafficManagerProfilesAsync(ct))
        {
            result.Add(new TrafficManagerProfileInfo(
                profile.Data.Name,
                profile.Id.ResourceGroupName ?? "-",
                profile.Data.ProfileStatus?.ToString(),
                profile.Data.TrafficRoutingMethod?.ToString(),
                profile.Data.Endpoints.Count,
                profile.Id.ToString()));
        }

        return result;
    }

    public async Task<List<NetworkWatcherInfo>> ListNetworkWatchersAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<NetworkWatcherInfo>();
        await foreach (NetworkWatcherResource watcher in GetSubscription(subscriptionId).GetNetworkWatchersAsync(ct))
        {
            result.Add(new NetworkWatcherInfo(
                watcher.Data.Name,
                watcher.Data.Location?.Name ?? "-",
                watcher.Id.ResourceGroupName ?? "-",
                watcher.Id.ToString()));
        }

        return result;
    }

    public async Task<List<WafPolicyInfo>> ListWafPoliciesAsync(string subscriptionId, CancellationToken ct = default)
    {
        // Raw JSON: the SDK's typed model throws while reading real policies (e.g. a numeric
        // rule-group name), which made this whole list fail.
        using var doc = await _arm.GetAsync(
            $"/subscriptions/{subscriptionId}/providers/Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies",
            NetworkApiVersion, ct: ct);

        var result = new List<WafPolicyInfo>();
        foreach (var policy in ArmJson.Items(doc.RootElement, "value"))
        {
            var id = ArmJson.Str(policy, "id");
            var p = ArmJson.Obj(policy, "properties");
            var mode = ArmJson.Str(ArmJson.Obj(p, "policySettings"), "mode");
            result.Add(new WafPolicyInfo(
                ArmJson.Str(policy, "name"),
                ArmJson.Str(policy, "location"),
                new Azure.Core.ResourceIdentifier(id).ResourceGroupName ?? "-",
                mode == "-" ? null : mode,
                ArmJson.Items(p, "customRules").Count(),
                id));
        }

        return result;
    }

    /// <summary>
    /// Purges the cache of one CDN or Front Door (Standard/Premium) endpoint for the given paths. Paths start with "/" and may end
    /// with a wildcard ("/*" = everything, "/images/*" = a folder). The call returns as soon as Azure accepts the request
    /// (202): the purge itself takes a few minutes to reach every edge location.
    /// </summary>
    public async Task PurgeCdnEndpointAsync(
        string profileId, string endpointName, bool isFrontDoor, IReadOnlyList<string> contentPaths, CancellationToken ct = default)
    {
        const int MaxPaths = 50;
        var paths = contentPaths.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        if (paths.Count == 0) throw new ArgumentException(T("Specify at least one path to purge (for example /*)."));
        if (paths.Count > MaxPaths) throw new ArgumentException(T("At most {0} paths per request: you specified {1}.", MaxPaths, paths.Count));
        var bad = paths.FirstOrDefault(p => !p.StartsWith('/') || p.Contains("://") || p.IndexOf('*') is var i && i >= 0 && i != p.Length - 1);
        if (bad is not null) throw new ArgumentException(T("Invalid path: \"{0}\". It must start with / and may have the * character only at the end (examples: /*, /images/*, /file.js).", bad));

        var segment = isFrontDoor ? "afdEndpoints" : "endpoints";
        await _arm.PostAsync(
            $"{profileId}/{segment}/{Uri.EscapeDataString(endpointName)}/purge", CdnApiVersion, ct,
            new { contentPaths = paths });
    }

    public async Task<List<CdnProfileInfo>> ListCdnProfilesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<CdnProfileInfo>();
        await foreach (ProfileResource profile in GetSubscription(subscriptionId).GetProfilesAsync(ct))
        {
            var endpoints = new List<CdnEndpointInfo>();

            // A CDN profile is either "classic" (Standard/Premium, endpoints under GetCdnEndpoints)
            // or Front Door (AFD, endpoints under GetFrontDoorEndpoints) — only one of the two ever
            // has entries for a given profile, so both are tried and whichever is empty is harmless.
            try
            {
                await foreach (var ep in profile.GetCdnEndpoints().GetAllAsync(cancellationToken: ct))
                {
                    endpoints.Add(new CdnEndpointInfo(ep.Data.Name, ep.Data.HostName, IsFrontDoor: false));
                }
            }
            catch (Azure.RequestFailedException) { }

            try
            {
                await foreach (var ep in profile.GetFrontDoorEndpoints().GetAllAsync(cancellationToken: ct))
                {
                    endpoints.Add(new CdnEndpointInfo(ep.Data.Name, ep.Data.HostName, IsFrontDoor: true));
                }
            }
            catch (Azure.RequestFailedException) { }

            result.Add(new CdnProfileInfo(
                profile.Data.Name,
                profile.Data.Location.Name,
                profile.Id.ResourceGroupName ?? "-",
                profile.Data.SkuName?.ToString() ?? "-",
                endpoints,
                profile.Id.ToString()));
        }

        return result;
    }
}
