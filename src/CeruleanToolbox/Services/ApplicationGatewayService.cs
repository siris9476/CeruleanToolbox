using System.Text.Json;
using Azure;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record ApplicationGatewayInfo(string Name, string Location, string OperationalState, string ResourceGroup, string Id);

public sealed class ApplicationGatewayService
{
    private const string NetworkApiVersion = "2024-05-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public ApplicationGatewayService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<List<ApplicationGatewayInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<ApplicationGatewayInfo>();
        await foreach (ApplicationGatewayResource gateway in subscription.GetApplicationGatewaysAsync(ct))
        {
            result.Add(new ApplicationGatewayInfo(
                gateway.Data.Name,
                gateway.Data.Location?.Name ?? "-",
                gateway.Data.OperationalState?.ToString() ?? "Unknown",
                gateway.Id!.ResourceGroupName ?? "-",
                gateway.Id!.ToString()));
        }

        return result;
    }

    public async Task StartAsync(string resourceId, CancellationToken ct = default)
    {
        var gateway = _clientFactory.GetClient().GetApplicationGatewayResource(new Azure.Core.ResourceIdentifier(resourceId));
        await gateway.StartAsync(WaitUntil.Started, ct);
    }

    public async Task StopAsync(string resourceId, CancellationToken ct = default)
    {
        var gateway = _clientFactory.GetClient().GetApplicationGatewayResource(new Azure.Core.ResourceIdentifier(resourceId));
        await gateway.StopAsync(WaitUntil.Started, ct);
    }

    /// <summary>Portal-style overview of an application gateway, with its listeners, rules, pools and probes.</summary>
    public async Task<ResourceOverview> GetOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, NetworkApiVersion, ct: ct);
        var gw = doc.RootElement;
        var p = ArmJson.Obj(gw, "properties");
        var sku = ArmJson.Obj(p, "sku");
        var autoscale = ArmJson.Obj(p, "autoscaleConfiguration");
        var waf = ArmJson.Obj(p, "webApplicationFirewallConfiguration");
        var sslPolicy = ArmJson.Obj(p, "sslPolicy");
        var state = ArmJson.Str(p, "operationalState");

        string Name(JsonElement e) => ArmJson.Str(e, "name");
        string Props(JsonElement e, string key) => ArmJson.Str(ArmJson.Obj(e, "properties"), key);
        string Ref(JsonElement e, string key) => ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(ArmJson.Obj(e, "properties"), key), "id"));

        var capacity = autoscale.ValueKind == JsonValueKind.Object
            ? $"Autoscale {ArmJson.Str(autoscale, "minCapacity")}-{ArmJson.Str(autoscale, "maxCapacity")}"
            : T("Manual ({0} instances)", ArmJson.Str(sku, "capacity"));

        var firewallPolicy = ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "firewallPolicy"), "id"));
        var wafText = firewallPolicy != "-"
            ? $"Policy {firewallPolicy}"
            : waf.ValueKind == JsonValueKind.Object
                ? T("{0}, mode {1}, {2} {3}", ArmJson.Str(waf, "enabled") == T("Yes") ? T("Active") : T("Inactive"), ArmJson.Str(waf, "firewallMode"), ArmJson.Str(waf, "ruleSetType"), ArmJson.Str(waf, "ruleSetVersion"))
                : T("Not configured");

        // Frontend IPs reference separate public-IP resources; resolve the address for display.
        var frontends = new List<IReadOnlyList<string>>();
        foreach (var f in ArmJson.Items(p, "frontendIPConfigurations"))
        {
            var publicId = ArmJson.Str(ArmJson.Obj(ArmJson.Obj(f, "properties"), "publicIPAddress"), "id");
            var publicIp = "-";
            if (publicId != "-")
            {
                try
                {
                    using var ip = await _arm.GetAsync(publicId, NetworkApiVersion, ct: ct);
                    publicIp = ArmJson.Str(ArmJson.Obj(ip.RootElement, "properties"), "ipAddress");
                }
                catch (InvalidOperationException)
                {
                }
            }

            frontends.Add(new[] { Name(f), publicId == "-" ? T("Private") : T("Public"), publicIp, Props(f, "privateIPAddress") });
        }

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Operational state"), state),
                ArmJson.Row("Provisioning", ArmJson.Str(p, "provisioningState")),
                ArmJson.Row("Location", ArmJson.Str(gw, "location")),
                ArmJson.Row("Zone", string.Join(", ", ArmJson.Items(gw, "zones").Select(z => z.ToString()).Order()) is { Length: > 0 } zones ? zones : T("None")),
                ArmJson.Row("SKU", $"{ArmJson.Str(sku, "name")} ({ArmJson.Str(sku, "family")})"),
                ArmJson.Row(T("Capacity"), capacity),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(gw, "identity"), "type")),
            }),
            new(T("Security"), new[]
            {
                ArmJson.Row("Web Application Firewall", wafText),
                ArmJson.Row(T("SSL policy"), ArmJson.Str(sslPolicy, "policyName") != "-" ? ArmJson.Str(sslPolicy, "policyName") : ArmJson.Str(sslPolicy, "policyType")),
                ArmJson.Row(T("Minimum TLS version"), ArmJson.Str(sslPolicy, "minProtocolVersion")),
                ArmJson.Row("HTTP/2", ArmJson.Str(p, "enableHttp2")),
                ArmJson.Row("FIPS", ArmJson.Str(p, "enableFips")),
                ArmJson.Row(T("SSL certificates"), ArmJson.Items(p, "sslCertificates").Count().ToString()),
                ArmJson.Row(T("Private endpoint connections"), ArmJson.Items(p, "privateEndpointConnections").Count().ToString()),
            }),
            new(T("Configuration"), new[]
            {
                ArmJson.Row("Listener", ArmJson.Items(p, "httpListeners").Count().ToString()),
                ArmJson.Row(T("Routing rules"), ArmJson.Items(p, "requestRoutingRules").Count().ToString()),
                ArmJson.Row(T("Backend pools"), ArmJson.Items(p, "backendAddressPools").Count().ToString()),
                ArmJson.Row(T("Backend HTTP settings"), ArmJson.Items(p, "backendHttpSettingsCollection").Count().ToString()),
                ArmJson.Row(T("Health probes"), ArmJson.Items(p, "probes").Count().ToString()),
                ArmJson.Row(T("Rewrite rule sets"), ArmJson.Items(p, "rewriteRuleSets").Count().ToString()),
                ArmJson.Row(T("Redirect configurations"), ArmJson.Items(p, "redirectConfigurations").Count().ToString()),
            }),
        };

        var ports = ArmJson.Items(p, "frontendPorts").ToDictionary(x => ArmJson.Str(x, "id"), x => Props(x, "port"), StringComparer.OrdinalIgnoreCase);

        var listeners = ArmJson.Items(p, "httpListeners").Select(l =>
        {
            var lp = ArmJson.Obj(l, "properties");
            var host = ArmJson.Str(lp, "hostName") != "-" ? ArmJson.Str(lp, "hostName")
                : string.Join(", ", ArmJson.Items(lp, "hostNames").Select(h => h.GetString())) is { Length: > 0 } hosts ? hosts : "-";
            var portId = ArmJson.Str(ArmJson.Obj(lp, "frontendPort"), "id");
            return (IReadOnlyList<string>)new[]
            {
                Name(l), ArmJson.Str(lp, "protocol"), ports.TryGetValue(portId, out var port) ? port : ArmJson.LastSegment(portId),
                host, Ref(l, "sslCertificate"), ArmJson.Str(lp, "requireServerNameIndication"),
            };
        }).ToList();

        var rules = ArmJson.Items(p, "requestRoutingRules").Select(r =>
        {
            var target = Ref(r, "backendAddressPool") != "-" ? $"Pool {Ref(r, "backendAddressPool")}"
                : Ref(r, "redirectConfiguration") != "-" ? $"Redirect {Ref(r, "redirectConfiguration")}"
                : Ref(r, "urlPathMap") != "-" ? $"Path map {Ref(r, "urlPathMap")}" : "-";
            return (IReadOnlyList<string>)new[]
            {
                Name(r), Props(r, "ruleType"), Props(r, "priority"), Ref(r, "httpListener"), target, Ref(r, "backendHttpSettings"),
            };
        }).ToList();

        var pools = ArmJson.Items(p, "backendAddressPools").Select(b =>
        {
            var addresses = ArmJson.Items(ArmJson.Obj(b, "properties"), "backendAddresses")
                .Select(a => ArmJson.Str(a, "fqdn") != "-" ? ArmJson.Str(a, "fqdn") : ArmJson.Str(a, "ipAddress"));
            return (IReadOnlyList<string>)new[] { Name(b), string.Join(", ", addresses) is { Length: > 0 } list ? list : T("No addresses") };
        }).ToList();

        var settings = ArmJson.Items(p, "backendHttpSettingsCollection").Select(s => (IReadOnlyList<string>)new[]
        {
            Name(s), Props(s, "port"), Props(s, "protocol"), Props(s, "cookieBasedAffinity"),
            $"{Props(s, "requestTimeout")} s", Props(s, "hostName") != "-" ? Props(s, "hostName") : (Props(s, "pickHostNameFromBackendAddress") == T("Yes") ? T("from backend") : "-"),
            Ref(s, "probe"),
        }).ToList();

        var probes = ArmJson.Items(p, "probes").Select(pr => (IReadOnlyList<string>)new[]
        {
            Name(pr), Props(pr, "protocol"), Props(pr, "host"), Props(pr, "path"),
            $"{Props(pr, "interval")} s", $"{Props(pr, "timeout")} s", Props(pr, "unhealthyThreshold"),
        }).ToList();

        var certificates = ArmJson.Items(p, "sslCertificates")
            .Select(c => (IReadOnlyList<string>)new[] { Name(c), Props(c, "keyVaultSecretId") != "-" ? "Key Vault" : T("Uploaded") }).ToList();

        var tables = new List<InfoTable>
        {
            new("Frontend IP", new[] { T("Name"), T("Type"), T("Public IP"), T("Private IP") }, frontends),
            new("Listener", new[] { T("Name"), T("Protocol"), T("Port"), "Host", T("Certificate"), T("SNI required") }, listeners),
            new(T("Routing rules"), new[] { T("Name"), T("Type"), T("Priority"), "Listener", T("Destination"), T("HTTP settings") }, rules),
            new(T("Backend pools"), new[] { T("Name"), "Target" }, pools),
            new(T("Backend HTTP settings"), new[] { T("Name"), T("Port"), T("Protocol"), T("Cookie affinity"), "Timeout", "Host header", "Probe" }, settings),
            new(T("Health probes"), new[] { T("Name"), T("Protocol"), "Host", T("Path"), T("Interval"), "Timeout", T("Unhealthy threshold") }, probes),
            new(T("SSL certificates"), new[] { T("Name"), T("Source") }, certificates),
        };

        var tags = ArmJson.Obj(gw, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(state, sections, tables, tags);
    }
}
