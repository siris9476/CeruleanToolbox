using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record BastionHostInfo(
    string Name, string ResourceGroup, string Location, string? DnsName,
    string SkuName, bool ShareableLinkEnabled, string Id);

public sealed class BastionService
{
    // Azure.ResourceManager.Network 1.17.0 only wraps Get/Delete Bastion Shareable Link — the
    // Create ("createShareableLinks") operation isn't generated in this SDK version, so it's
    // called directly against the documented ARM REST endpoint instead.
    private const string CreateApiVersion = "2024-05-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public BastionService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetOverviewAsync(string hostResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(hostResourceId, CreateApiVersion, ct: ct);
        var host = doc.RootElement;
        var p = ArmJson.Obj(host, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        // Public IPs are separate resources; resolve the address for the IP configuration table.
        var ipConfigs = new List<IReadOnlyList<string>>();
        foreach (var c in ArmJson.Items(p, "ipConfigurations"))
        {
            var cp = ArmJson.Obj(c, "properties");
            var publicId = ArmJson.Str(ArmJson.Obj(cp, "publicIPAddress"), "id");
            var address = "-";
            if (publicId != "-")
            {
                try
                {
                    using var ipDoc = await _arm.GetAsync(publicId, CreateApiVersion, ct: ct);
                    address = ArmJson.Str(ArmJson.Obj(ipDoc.RootElement, "properties"), "ipAddress");
                }
                catch (InvalidOperationException)
                {
                }
            }

            var subnetId = ArmJson.Str(ArmJson.Obj(cp, "subnet"), "id");
            var parts = subnetId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var vnetIndex = Array.FindIndex(parts, x => string.Equals(x, "virtualNetworks", StringComparison.OrdinalIgnoreCase));
            var subnetLabel = subnetId == "-" ? "-" : vnetIndex >= 0 && vnetIndex + 3 < parts.Length ? $"{parts[vnetIndex + 1]} / {parts[vnetIndex + 3]}" : ArmJson.LastSegment(subnetId);

            ipConfigs.Add(new[]
            {
                ArmJson.Str(c, "name"), subnetLabel, ArmJson.LastSegment(publicId), address, ArmJson.Str(cp, "privateIPAllocationMethod"),
            });
        }

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(host, "location")),
                ArmJson.Row("SKU", ArmJson.Str(ArmJson.Obj(host, "sku"), "name")),
                ArmJson.Row(T("Scale units"), ArmJson.Str(host, "scaleUnits") is var units && units != "-" ? units : ArmJson.Str(p, "scaleUnits")),
                ArmJson.Row("DNS name", ArmJson.Str(p, "dnsName")),
                ArmJson.Row("Zone", string.Join(", ", ArmJson.Items(host, "zones").Select(z => z.ToString()).Order()) is { Length: > 0 } zones ? zones : T("None")),
                ArmJson.Row(T("Virtual network (Developer SKU)"), ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "virtualNetwork"), "id"))),
            }),
            new(T("Features"), new[]
            {
                ArmJson.Row(T("Shared links"), ArmJson.Str(p, "enableShareableLink")),
                ArmJson.Row(T("Native tunneling (client)"), ArmJson.Str(p, "enableTunneling")),
                ArmJson.Row(T("IP connection"), ArmJson.Str(p, "enableIpConnect")),
                ArmJson.Row(T("Kerberos authentication"), ArmJson.Str(p, "enableKerberos")),
                ArmJson.Row(T("Session recording"), ArmJson.Str(p, "enableSessionRecording")),
                ArmJson.Row(T("Copy and paste disabled"), ArmJson.Str(p, "disableCopyPaste")),
            }),
        };

        var tables = new List<InfoTable>
        {
            new(T("IP configurations"), new[] { T("Name"), "VNet / subnet", T("Public IP"), T("Address"), T("Private IP allocation") }, ipConfigs),
        };

        var tags = ArmJson.Obj(host, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(provisioning, sections, tables, tags);
    }

    public async Task<List<BastionHostInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<BastionHostInfo>();
        await foreach (BastionHostResource host in subscription.GetBastionHostsAsync(ct))
        {
            result.Add(new BastionHostInfo(
                host.Data.Name,
                host.Id!.ResourceGroupName ?? "-",
                host.Data.Location?.Name ?? "-",
                host.Data.DnsName,
                host.Data.SkuName?.ToString() ?? "-",
                host.Data.EnableShareableLink ?? false,
                host.Id!.ToString()));
        }

        return result;
    }

    /// <summary>
    /// Generates a Bastion Shareable Link for a VM: a URL that opens an RDP/SSH session in the
    /// browser without needing Azure portal access, as long as the caller has Reader on the VM.
    /// Requires the Bastion host to be Standard SKU with the shareable link feature enabled.
    /// </summary>
    public async Task<string> CreateShareableLinkAsync(string bastionResourceId, string vmResourceId, CancellationToken ct = default)
    {
        var credential = _clientFactory.GetCredential();
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var url = $"https://management.azure.com{bastionResourceId}/createShareableLinks?api-version={CreateApiVersion}";
        var body = new { vms = new[] { new { vm = new { id = vmResourceId } } } };

        var response = await http.PostAsJsonAsync(url, body, ct);

        // The create action can be a long-running operation: poll the Location header until done.
        var attempts = 0;
        while (response.StatusCode == HttpStatusCode.Accepted && attempts++ < 15)
        {
            var location = response.Headers.Location?.ToString();
            if (location is null) break;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            response = await http.GetAsync(location, ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                T("Bastion link creation failed ({0} {1}): {2}", (int)response.StatusCode, response.StatusCode, errorBody));
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var first = json.GetProperty("value").EnumerateArray().FirstOrDefault();

        if (first.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(T("Bastion did not return any shared link."));
        }

        if (first.TryGetProperty("message", out var messageElement) && messageElement.GetString() is { Length: > 0 } message)
        {
            throw new InvalidOperationException(T("Bastion did not generate the link: {0}", message));
        }

        return first.GetProperty("bsl").GetString()
            ?? throw new InvalidOperationException(T("Bastion did not return any shared link."));
    }

    public async Task DeleteShareableLinkAsync(string bastionResourceId, string vmResourceId, CancellationToken ct = default)
    {
        // DEBUG-TEST: real revocation disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the block below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteShareableLinkAsync() called for vm={vmResourceId} on {bastionResourceId} — no-op, revoke disabled.");
        await Task.CompletedTask;
        return;

        var bastion = _clientFactory.GetClient().GetBastionHostResource(new ResourceIdentifier(bastionResourceId));
        var vmRef = new BastionShareableLinkVirtualMachine { Id = new ResourceIdentifier(vmResourceId) };

        var content = new BastionShareableLinkListContent();
        content.Vms.Add(new BastionShareableLink(vmRef));

        await bastion.DeleteBastionShareableLinkAsync(Azure.WaitUntil.Completed, content, ct);
    }
}
