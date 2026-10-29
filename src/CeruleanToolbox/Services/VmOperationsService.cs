using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record VmInfo(string Name, string Location, string PowerState, string ResourceGroup, string Id);
public sealed class VmOperationsService
{
    private const string ComputeApiVersion = "2024-07-01";
    private const string NetworkApiVersion = "2024-05-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;
    private readonly ExtensionCatalogService _catalog;

    public VmOperationsService(AzureClientFactory clientFactory, ArmRestClient arm, ExtensionCatalogService catalog)
    {
        _clientFactory = clientFactory;
        _arm = arm;
        _catalog = catalog;
    }

    /// <param name="refreshCatalog">Read the extension catalog again instead of using the cached copy (the Refresh button).</param>
    public async Task<ResourceOverview> GetOverviewAsync(string resourceId, CancellationToken ct = default, bool refreshCatalog = false)
    {
        using var doc = await _arm.GetAsync(resourceId, ComputeApiVersion, "$expand=instanceView", ct);
        var root = doc.RootElement;
        var p = ArmJson.Obj(root, "properties");
        var storage = ArmJson.Obj(p, "storageProfile");
        var image = ArmJson.Obj(storage, "imageReference");
        var osDisk = ArmJson.Obj(storage, "osDisk");
        var view = ArmJson.Obj(p, "instanceView");
        var location = ArmJson.Str(root, "location");
        var size = ArmJson.Str(ArmJson.Obj(p, "hardwareProfile"), "vmSize");

        var powerState = ArmJson.Items(view, "statuses")
            .Where(s => ArmJson.Str(s, "code").StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))
            .Select(s => ArmJson.Str(s, "displayStatus"))
            .FirstOrDefault() ?? "-";

        var nics = await ReadNicsAsync(p, ct);
        var first = nics.FirstOrDefault();

        var os = ArmJson.Str(view, "osName") != "-"
            ? $"{ArmJson.Str(view, "osName")} {ArmJson.Str(view, "osVersion")}".Trim()
            : ArmJson.Str(osDisk, "osType");

        var zones = string.Join(", ", ArmJson.Items(root, "zones").Select(z => z.ToString()));
        var agentStatus = ArmJson.Items(ArmJson.Obj(view, "vmAgent"), "statuses")
            .Select(s => ArmJson.Str(s, "displayStatus")).FirstOrDefault() ?? "-";
        var agent = ArmJson.Str(ArmJson.Obj(view, "vmAgent"), "vmAgentVersion") is var version && version != "-"
            ? $"{agentStatus} (v{version})" : agentStatus;

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Status"), powerState),
                ArmJson.Row("Provisioning", ArmJson.Str(p, "provisioningState")),
                ArmJson.Row("Location", location),
                ArmJson.Row(T("Availability zone"), zones.Length > 0 ? zones : T("None")),
                ArmJson.Row("Computer name", ArmJson.Str(view, "computerName")),
                ArmJson.Row(T("Operating system"), os),
                ArmJson.Row(T("Size"), await DescribeSizeAsync(resourceId, location, size, ct)),
                ArmJson.Row(T("Public IP"), first?.PublicIp ?? "-"),
                ArmJson.Row(T("Private IP"), first?.PrivateIp ?? "-"),
                ArmJson.Row(T("Virtual network / subnet"), first?.Subnet ?? "-"),
                ArmJson.Row(T("Created on"), ArmJson.Date(ArmJson.Str(p, "timeCreated"))),
                ArmJson.Row("VM ID", ArmJson.Str(p, "vmId")),
            }),
            new(T("Image and configuration"), new[]
            {
                ArmJson.Row("Publisher", ArmJson.Str(image, "publisher")),
                ArmJson.Row("Offer", ArmJson.Str(image, "offer")),
                ArmJson.Row("SKU", ArmJson.Str(image, "sku")),
                ArmJson.Row(T("Image version"), ArmJson.Str(image, "exactVersion") is var exact && exact != "-" ? exact : ArmJson.Str(image, "version")),
                ArmJson.Row(T("VM generation"), ArmJson.Str(view, "hyperVGeneration")),
                ArmJson.Row(T("Priority"), ArmJson.Str(p, "priority")),
                ArmJson.Row(T("VM agent"), agent),
                ArmJson.Row("Boot diagnostics", ArmJson.Str(ArmJson.Obj(ArmJson.Obj(p, "diagnosticsProfile"), "bootDiagnostics"), "enabled")),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(root, "identity"), "type")),
                ArmJson.Row("Availability set", ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "availabilitySet"), "id"))),
                ArmJson.Row("Scale set (VMSS)", ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "virtualMachineScaleSet"), "id"))),
            }),
        };

        var disks = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "OS", ArmJson.Str(osDisk, "name"), $"{ArmJson.Str(osDisk, "diskSizeGB")} GB", ArmJson.Str(osDisk, "caching"),
                ArmJson.Str(ArmJson.Obj(osDisk, "managedDisk"), "storageAccountType"), ArmJson.Str(osDisk, "deleteOption"),
                await DiskCreatedAsync(ArmJson.Str(ArmJson.Obj(osDisk, "managedDisk"), "id"), ct),
            },
        };
        foreach (var d in ArmJson.Items(storage, "dataDisks"))
        {
            disks.Add(new[]
            {
                T("Data (LUN {0})", ArmJson.Str(d, "lun")), ArmJson.Str(d, "name"), $"{ArmJson.Str(d, "diskSizeGB")} GB", ArmJson.Str(d, "caching"),
                ArmJson.Str(ArmJson.Obj(d, "managedDisk"), "storageAccountType"), ArmJson.Str(d, "deleteOption"),
                await DiskCreatedAsync(ArmJson.Str(ArmJson.Obj(d, "managedDisk"), "id"), ct),
            });
        }

        // Provisioning state comes from the ARM resource; what the extension actually did on the VM (and why it failed)
        // comes from the instance view.
        var extensionRuns = ArmJson.Items(view, "extensions").ToDictionary(
            e => ArmJson.Str(e, "name"), e => e, StringComparer.OrdinalIgnoreCase);
        // The "latest available" column comes from the extension catalog of the VM's region: one lookup per extension,
        // started together and cached, so a slow or failing catalog never holds the page back (the cell shows "-").
        var subscriptionId = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } idParts ? idParts[1] : "-";
        var extensionRows = new List<string[]>();
        var latestLookups = new List<Task<CatalogAnswer?>>();
        foreach (var extension in ArmJson.Items(root, "resources")
                     .Where(x => ArmJson.Str(x, "type").EndsWith("/extensions", StringComparison.OrdinalIgnoreCase)))
        {
            var props = ArmJson.Obj(extension, "properties");
            var name = ArmJson.Str(extension, "name").Split('/')[^1];
            var status = extensionRuns.TryGetValue(name, out var run) ? ArmJson.Items(run, "statuses").FirstOrDefault() : default;
            var level = ArmJson.Str(status, "level");
            var publisher = ArmJson.Str(props, "publisher");
            var extensionType = ArmJson.Str(props, "type");
            var runningVersion = ArmJson.Str(run, "typeHandlerVersion");
            var configuredVersion = ArmJson.Str(props, "typeHandlerVersion");

            var versionForComparison = runningVersion != "-" ? runningVersion : configuredVersion;
            latestLookups.Add(_catalog.LookupAsync(subscriptionId, location, publisher, extensionType, versionForComparison, ct, refreshCatalog));
            extensionRows.Add(new[]
            {
                name, $"{publisher}.{extensionType}",
                ArmJson.ExtensionVersion(runningVersion, configuredVersion),
                "-", // latest available, filled in below
                ArmJson.Str(props, "enableAutomaticUpgrade"),
                ArmJson.Str(props, "provisioningState"),
                ArmJson.Str(status, "displayStatus"),
                // Only the headline message of a non-successful status: the sub-statuses carry the script's own output.
                level is "Error" or "Warning" ? ExtensionMessage(ArmJson.Str(status, "message")) : "-",
            });
        }

        var latestAnswers = await Task.WhenAll(latestLookups);
        for (var i = 0; i < extensionRows.Count; i++)
        {
            extensionRows[i][3] = latestAnswers[i]?.Display ?? "-";
        }

        var extensions = extensionRows.Select(row => (IReadOnlyList<string>)row).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Disks"), new[] { T("Type"), T("Name"), T("Size"), "Caching", T("Disk SKU"), T("On VM deletion"), T("Created at") }, disks),
            new(T("Network interfaces"), new[] { "NIC", T("Private IP"), T("Public IP"), "Subnet", "NSG", "Accelerated networking" },
                nics.Select(n => (IReadOnlyList<string>)new[] { n.Name, n.PrivateIp, n.PublicIp, n.Subnet, n.Nsg, n.Accelerated }).ToList()),
            new(T("Extensions"), new[] { T("Name"), T("Type"), T("Version"), T("Latest available"), T("Automatic upgrade"), "Provisioning", T("Execution"), T("Error detail") }, extensions),
        };

        var tags = ArmJson.Obj(root, "tags") is { ValueKind: System.Text.Json.JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(powerState, sections, tables, tags);
    }

    // Creation time of a managed disk ("-" if it cannot be read: this is an extra, it must not fail the overview).
    private async Task<string> DiskCreatedAsync(string diskId, CancellationToken ct)
    {
        if (diskId == "-") return "-";

        try
        {
            using var disk = await _arm.GetAsync(diskId, "2023-10-02", ct: ct);
            return ArmJson.Date(ArmJson.Str(ArmJson.Obj(disk.RootElement, "properties"), "timeCreated"));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return "-";
        }
    }

    // Extension errors can quote command lines or settings: never show something that looks like a secret.
    private static string ExtensionMessage(string message)
    {
        if (message == "-") return "-";

        var text = System.Text.RegularExpressions.Regex.Replace(
            message, @"(?i)(token|pat|password|secret|sas|key)(\s*[=:]\s*|\s+)\S+", "$1=" + T("[redacted]"));
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > 260 ? text[..260] + "..." : text;
    }

    private sealed record NicRow(string Name, string PrivateIp, string PublicIp, string Subnet, string Nsg, string Accelerated);

    private async Task<List<NicRow>> ReadNicsAsync(System.Text.Json.JsonElement vmProperties, CancellationToken ct)
    {
        var rows = new List<NicRow>();
        foreach (var reference in ArmJson.Items(ArmJson.Obj(vmProperties, "networkProfile"), "networkInterfaces"))
        {
            var nicId = ArmJson.Str(reference, "id");
            if (nicId == "-") continue;

            using var nic = await _arm.GetAsync(nicId, NetworkApiVersion, ct: ct);
            var np = ArmJson.Obj(nic.RootElement, "properties");
            var nsg = ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(np, "networkSecurityGroup"), "id"));
            var accelerated = ArmJson.Str(np, "enableAcceleratedNetworking");

            foreach (var config in ArmJson.Items(np, "ipConfigurations"))
            {
                var cp = ArmJson.Obj(config, "properties");
                var subnetId = ArmJson.Str(ArmJson.Obj(cp, "subnet"), "id");
                var subnet = subnetId == "-" ? "-" : $"{VnetName(subnetId)} / {ArmJson.LastSegment(subnetId)}";

                var publicIp = "-";
                var publicId = ArmJson.Str(ArmJson.Obj(cp, "publicIPAddress"), "id");
                if (publicId != "-")
                {
                    using var ip = await _arm.GetAsync(publicId, NetworkApiVersion, ct: ct);
                    publicIp = ArmJson.Str(ArmJson.Obj(ip.RootElement, "properties"), "ipAddress");
                }

                rows.Add(new NicRow(ArmJson.Str(nic.RootElement, "name"), ArmJson.Str(cp, "privateIPAddress"), publicIp, subnet, nsg, accelerated));
            }
        }

        return rows;
    }

    private static string VnetName(string subnetId)
    {
        var parts = subnetId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, x => string.Equals(x, "virtualNetworks", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "-";
    }

    private async Task<string> DescribeSizeAsync(string resourceId, string location, string size, CancellationToken ct)
    {
        try
        {
            var subscriptionId = new Azure.Core.ResourceIdentifier(resourceId).SubscriptionId;
            using var sizes = await _arm.GetAsync(
                $"/subscriptions/{subscriptionId}/providers/Microsoft.Compute/locations/{location}/vmSizes", ComputeApiVersion, ct: ct);
            var match = ArmJson.Items(sizes.RootElement, "value")
                .FirstOrDefault(s => string.Equals(ArmJson.Str(s, "name"), size, StringComparison.OrdinalIgnoreCase));
            if (match.ValueKind != System.Text.Json.JsonValueKind.Object) return size;

            var memoryMb = double.TryParse(ArmJson.Str(match, "memoryInMB"), out var mb) ? mb : 0;
            return $"{size} · {ArmJson.Str(match, "numberOfCores")} vCPU · {memoryMb / 1024:0.#} GB RAM";
        }
        catch (InvalidOperationException)
        {
            return size;
        }
    }

    /// <summary>Lists every VM across every resource group in the subscription, in one call.</summary>
    public async Task<List<VmInfo>> ListAllVirtualMachinesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        return await CollectAsync(subscription.GetVirtualMachinesAsync(cancellationToken: ct), ct);
    }

    private static async Task<List<VmInfo>> CollectAsync(
        IAsyncEnumerable<VirtualMachineResource> vms, CancellationToken ct)
    {
        var result = new List<VmInfo>();
        await foreach (var vm in vms)
        {
            var instanceView = await vm.InstanceViewAsync(ct);
            var powerState = instanceView.Value.Statuses
                .FirstOrDefault(s => s.Code?.StartsWith("PowerState/") == true)
                ?.DisplayStatus ?? "Unknown";

            result.Add(new VmInfo(vm.Data.Name, vm.Data.Location.Name, powerState, vm.Id!.ResourceGroupName ?? "-", vm.Id!.ToString()));
        }

        return result;
    }

    public async Task StartAsync(string resourceId, CancellationToken ct = default)
    {
        var vm = _clientFactory.GetClient().GetVirtualMachineResource(new Azure.Core.ResourceIdentifier(resourceId));
        await vm.PowerOnAsync(Azure.WaitUntil.Started, ct);
    }

    public async Task StopAsync(string resourceId, CancellationToken ct = default)
    {
        // Deallocate releases compute cost, unlike PowerOff which keeps the VM allocated.
        var vm = _clientFactory.GetClient().GetVirtualMachineResource(new Azure.Core.ResourceIdentifier(resourceId));
        await vm.DeallocateAsync(Azure.WaitUntil.Started, cancellationToken: ct);
    }

    public async Task RestartAsync(string resourceId, CancellationToken ct = default)
    {
        var vm = _clientFactory.GetClient().GetVirtualMachineResource(new Azure.Core.ResourceIdentifier(resourceId));
        await vm.RestartAsync(Azure.WaitUntil.Started, ct);
    }

    public async Task<List<string>> ListAvailableSizesAsync(string resourceId, CancellationToken ct = default)
    {
        var vm = _clientFactory.GetClient().GetVirtualMachineResource(new Azure.Core.ResourceIdentifier(resourceId));

        var result = new List<string>();
        await foreach (var size in vm.GetAvailableSizesAsync(ct))
        {
            if (size.Name is not null) result.Add(size.Name);
        }

        return result.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task ResizeAsync(string resourceId, string newSize, CancellationToken ct = default)
    {
        var vm = _clientFactory.GetClient().GetVirtualMachineResource(new Azure.Core.ResourceIdentifier(resourceId));
        var patch = new Azure.ResourceManager.Compute.Models.VirtualMachinePatch
        {
            HardwareProfile = new Azure.ResourceManager.Compute.Models.VirtualMachineHardwareProfile
            {
                VmSize = newSize,
            },
        };

        await vm.UpdateAsync(Azure.WaitUntil.Completed, patch, ct);
    }
}
