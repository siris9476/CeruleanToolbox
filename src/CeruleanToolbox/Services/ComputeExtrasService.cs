using System.Text.Json;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.DevTestLabs;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record VmssInfo(
    string Name, string Location, string ResourceGroup, int Capacity,
    string? OrchestrationMode, string Id);

public sealed record ManagedDiskInfo(
    string Name, string Location, string ResourceGroup, int? SizeGb,
    string? DiskState, bool IsAttached, string Sku, string Id);

public sealed record SnapshotInfo(
    string Name, string Location, string ResourceGroup, int? SizeGb,
    DateTimeOffset? CreatedOn, string Id);

public sealed record AppServicePlanInfo(
    string Name, string Location, string ResourceGroup, string Sku,
    int NumberOfSites, int? NumberOfWorkers, string Id);

public sealed record DevTestLabScheduleInfo(
    string Name, string ResourceGroup, string? Status, string? TaskType,
    string? TargetResourceId, string Id);

public sealed class ComputeExtrasService
{
    private const string ComputeApiVersion = "2024-07-01";
    private const string DiskApiVersion = "2023-10-02";
    private const string WebApiVersion = "2023-12-01";
    private const string DevTestApiVersion = "2018-09-15";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public ComputeExtrasService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    private static List<KeyValuePair<string, string>> Tags(JsonElement resource) =>
        ArmJson.Obj(resource, "tags") is { ValueKind: JsonValueKind.Object } tags
            ? tags.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

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


    private SubscriptionResource GetSubscription(string subscriptionId) =>
        _clientFactory.GetClient().GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

    public async Task<ResourceOverview> GetVmssOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, ComputeApiVersion, ct: ct);
        var vmss = doc.RootElement;
        var p = ArmJson.Obj(vmss, "properties");
        var sku = ArmJson.Obj(vmss, "sku");
        var profile = ArmJson.Obj(p, "virtualMachineProfile");
        var storage = ArmJson.Obj(profile, "storageProfile");
        var image = ArmJson.Obj(storage, "imageReference");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(vmss, "location")),
                ArmJson.Row(T("Orchestration mode"), ArmJson.Str(p, "orchestrationMode")),
                ArmJson.Row(T("VM size"), ArmJson.Str(sku, "name")),
                ArmJson.Row(T("Instances"), ArmJson.Str(sku, "capacity")),
                ArmJson.Row(T("Zones"), string.Join(", ", ArmJson.Items(vmss, "zones").Select(z => z.ToString()).Order()) is { Length: > 0 } zones ? zones : T("None")),
                ArmJson.Row(T("Fault domains"), ArmJson.Str(p, "platformFaultDomainCount")),
                ArmJson.Row(T("Created on"), ArmJson.Date(ArmJson.Str(p, "timeCreated"))),
            }),
            new(T("Image and operating system"), new[]
            {
                ArmJson.Row(T("Image"), ArmJson.Str(image, "publisher") != "-"
                    ? $"{ArmJson.Str(image, "publisher")} / {ArmJson.Str(image, "offer")} / {ArmJson.Str(image, "sku")} ({ArmJson.Str(image, "version")})"
                    : ArmJson.LastSegment(ArmJson.Str(image, "id"))),
                ArmJson.Row(T("Computer name prefix"), ArmJson.Str(ArmJson.Obj(profile, "osProfile"), "computerNamePrefix")),
                ArmJson.Row(T("OS disk type"), ArmJson.Str(ArmJson.Obj(ArmJson.Obj(storage, "osDisk"), "managedDisk"), "storageAccountType")),
                ArmJson.Row(T("Priority"), ArmJson.Str(profile, "priority")),
            }),
            new(T("Scaling and policies"), new[]
            {
                ArmJson.Row(T("Upgrade policy"), ArmJson.Str(ArmJson.Obj(p, "upgradePolicy"), "mode")),
                ArmJson.Row(T("Automatic repairs"), ArmJson.Str(ArmJson.Obj(p, "automaticRepairsPolicy"), "enabled")),
                ArmJson.Row("Overprovisioning", ArmJson.Str(p, "overprovision")),
                ArmJson.Row("Single placement group", ArmJson.Str(p, "singlePlacementGroup")),
                ArmJson.Row(T("Zone balancing"), ArmJson.Str(p, "zoneBalance")),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(vmss, "identity"), "type")),
            }),
        };

        var instances = new List<IReadOnlyList<string>>();
        using var instanceDoc = await TryGetAsync($"{resourceId}/virtualMachines", ComputeApiVersion, ct, "$expand=instanceView");
        if (instanceDoc is not null)
        {
            foreach (var vm in ArmJson.Items(instanceDoc.RootElement, "value"))
            {
                var vp = ArmJson.Obj(vm, "properties");
                var power = ArmJson.Items(ArmJson.Obj(vp, "instanceView"), "statuses")
                    .Select(x => ArmJson.Str(x, "code")).FirstOrDefault(c => c.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))?["PowerState/".Length..] ?? "-";
                instances.Add(new[]
                {
                    ArmJson.Str(vm, "name"), ArmJson.Str(vm, "instanceId"), ArmJson.Str(vp, "provisioningState"), power,
                    ArmJson.Str(vp, "latestModelApplied"),
                });
            }
        }

        var extensions = ArmJson.Items(ArmJson.Obj(profile, "extensionProfile"), "extensions").Select(e =>
        {
            var ep = ArmJson.Obj(e, "properties");
            return (IReadOnlyList<string>)new[]
            {
                ArmJson.Str(e, "name"), $"{ArmJson.Str(ep, "publisher")}.{ArmJson.Str(ep, "type")}", ArmJson.Str(ep, "typeHandlerVersion"),
            };
        }).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Instances"), new[] { T("Name"), T("Instance ID"), "Provisioning", "Power state", T("Latest model applied") }, instances),
            new(T("Extensions"), new[] { T("Name"), T("Type"), T("Version") }, extensions),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(vmss));
    }

    public async Task<ResourceOverview> GetDiskOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, DiskApiVersion, ct: ct);
        var disk = doc.RootElement;
        var p = ArmJson.Obj(disk, "properties");
        var creation = ArmJson.Obj(p, "creationData");
        var state = ArmJson.Str(p, "diskState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Disk state"), state),
                ArmJson.Row("Provisioning", ArmJson.Str(p, "provisioningState")),
                ArmJson.Row("Location", ArmJson.Str(disk, "location")),
                ArmJson.Row(T("Attached to"), ArmJson.LastSegment(ArmJson.Str(disk, "managedBy"))),
                ArmJson.Row(T("Operating system"), ArmJson.Str(p, "osType")),
                ArmJson.Row(T("Size"), $"{ArmJson.Str(p, "diskSizeGB")} GB"),
                ArmJson.Row("SKU", ArmJson.Str(ArmJson.Obj(disk, "sku"), "name")),
                ArmJson.Row(T("Performance tier"), ArmJson.Str(p, "tier")),
                ArmJson.Row(T("Zones"), string.Join(", ", ArmJson.Items(disk, "zones").Select(z => z.ToString()).Order()) is { Length: > 0 } zones ? zones : T("None")),
                ArmJson.Row(T("Created on"), ArmJson.Date(ArmJson.Str(p, "timeCreated"))),
            }),
            new(T("Performance and source"), new[]
            {
                ArmJson.Row(T("Max IOPS"), ArmJson.Str(p, "diskIOPSReadWrite")),
                ArmJson.Row(T("Max throughput (MB/s)"), ArmJson.Str(p, "diskMBpsReadWrite")),
                ArmJson.Row("Bursting", ArmJson.Str(p, "burstingEnabled")),
                ArmJson.Row(T("Maximum sharing (max shares)"), ArmJson.Str(p, "maxShares")),
                ArmJson.Row(T("Creation method"), ArmJson.Str(creation, "createOption")),
                ArmJson.Row(T("Source resource"), ArmJson.LastSegment(ArmJson.Str(creation, "sourceResourceId"))),
                ArmJson.Row(T("Hyper-V generation"), ArmJson.Str(p, "hyperVGeneration")),
            }),
            new(T("Security and networking"), new[]
            {
                ArmJson.Row(T("Encryption"), ArmJson.Str(ArmJson.Obj(p, "encryption"), "type")),
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess")),
                ArmJson.Row(T("Network access policy"), ArmJson.Str(p, "networkAccessPolicy")),
                ArmJson.Row(T("Hibernation supported"), ArmJson.Str(p, "supportsHibernation")),
            }),
        };

        return new ResourceOverview(state, sections, Array.Empty<InfoTable>(), Tags(disk));
    }

    public async Task<ResourceOverview> GetSnapshotOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, DiskApiVersion, ct: ct);
        var snapshot = doc.RootElement;
        var p = ArmJson.Obj(snapshot, "properties");
        var creation = ArmJson.Obj(p, "creationData");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row(T("Status"), ArmJson.Str(p, "diskState")),
                ArmJson.Row("Location", ArmJson.Str(snapshot, "location")),
                ArmJson.Row(T("Size"), $"{ArmJson.Str(p, "diskSizeGB")} GB"),
                ArmJson.Row("SKU", ArmJson.Str(ArmJson.Obj(snapshot, "sku"), "name")),
                ArmJson.Row(T("Type"), ArmJson.Str(p, "incremental") == T("Yes") ? T("Incremental") : T("Full")),
                ArmJson.Row(T("Operating system"), ArmJson.Str(p, "osType")),
                ArmJson.Row(T("Created on"), ArmJson.Date(ArmJson.Str(p, "timeCreated"))),
                ArmJson.Row(T("Copy completion"), ArmJson.Str(p, "completionPercent")),
            }),
            new(T("Source and security"), new[]
            {
                ArmJson.Row(T("Creation method"), ArmJson.Str(creation, "createOption")),
                ArmJson.Row(T("Origin disk"), ArmJson.LastSegment(ArmJson.Str(creation, "sourceResourceId"))),
                ArmJson.Row(T("Encryption"), ArmJson.Str(ArmJson.Obj(p, "encryption"), "type")),
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess")),
                ArmJson.Row(T("Network access policy"), ArmJson.Str(p, "networkAccessPolicy")),
                ArmJson.Row(T("Hyper-V generation"), ArmJson.Str(p, "hyperVGeneration")),
            }),
        };

        return new ResourceOverview(provisioning, sections, Array.Empty<InfoTable>(), Tags(snapshot));
    }

    public async Task<ResourceOverview> GetAppServicePlanOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, WebApiVersion, ct: ct);
        var plan = doc.RootElement;
        var p = ArmJson.Obj(plan, "properties");
        var sku = ArmJson.Obj(plan, "sku");
        var status = ArmJson.Str(p, "status");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Status"), status),
                ArmJson.Row("Provisioning", ArmJson.Str(p, "provisioningState")),
                ArmJson.Row("Location", ArmJson.Str(plan, "location")),
                ArmJson.Row(T("Operating system"), ArmJson.Str(p, "reserved") == T("Yes") ? "Linux" : "Windows"),
                ArmJson.Row(T("Pricing plan"), $"{ArmJson.Str(sku, "name")} ({ArmJson.Str(sku, "tier")})"),
                ArmJson.Row(T("Instances (workers)"), ArmJson.Str(p, "numberOfWorkers")),
                ArmJson.Row(T("SKU capacity"), ArmJson.Str(sku, "capacity")),
                ArmJson.Row(T("Hosted sites"), ArmJson.Str(p, "numberOfSites")),
            }),
            new(T("Scaling"), new[]
            {
                ArmJson.Row(T("Zone-redundant"), ArmJson.Str(p, "zoneRedundant")),
                ArmJson.Row(T("Per-site scaling"), ArmJson.Str(p, "perSiteScaling")),
                ArmJson.Row(T("Elastic scale"), ArmJson.Str(p, "elasticScaleEnabled")),
                ArmJson.Row(T("Maximum elastic workers"), ArmJson.Str(p, "maximumElasticWorkerCount")),
                ArmJson.Row(T("Hosting environment (ASE)"), ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(p, "hostingEnvironmentProfile"), "id"))),
            }),
        };

        var sites = new List<IReadOnlyList<string>>();
        using var siteDoc = await TryGetAsync($"{resourceId}/sites", WebApiVersion, ct);
        if (siteDoc is not null)
        {
            foreach (var site in ArmJson.Items(siteDoc.RootElement, "value"))
            {
                var sp = ArmJson.Obj(site, "properties");
                sites.Add(new[] { ArmJson.Str(site, "name"), ArmJson.Str(site, "kind"), ArmJson.Str(sp, "state"), ArmJson.Str(sp, "defaultHostName") });
            }
        }

        var tables = new List<InfoTable>
        {
            new(T("Hosted sites"), new[] { T("Name"), T("Type"), T("Status"), T("Default host") }, sites),
        };

        return new ResourceOverview(status, sections, tables, Tags(plan));
    }

    public async Task<ResourceOverview> GetDevTestScheduleOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, DevTestApiVersion, ct: ct);
        var schedule = doc.RootElement;
        var p = ArmJson.Obj(schedule, "properties");
        var status = ArmJson.Str(p, "status");

        // DevTest Labs stores times as "2300"; show them as "23:00".
        static string Clock(string time) => time.Length == 4 && time.All(char.IsDigit) ? $"{time[..2]}:{time[2..]}" : time;

        string Recurrence()
        {
            var daily = ArmJson.Obj(p, "dailyRecurrence");
            if (daily.ValueKind == JsonValueKind.Object) return T("Every day at {0}", Clock(ArmJson.Str(daily, "time")));

            var weekly = ArmJson.Obj(p, "weeklyRecurrence");
            if (weekly.ValueKind == JsonValueKind.Object)
            {
                return T("Weekly ({0}) at {1}", string.Join(", ", ArmJson.Items(weekly, "weekdays").Select(w => w.GetString())), Clock(ArmJson.Str(weekly, "time")));
            }

            var hourly = ArmJson.Obj(p, "hourlyRecurrence");
            return hourly.ValueKind == JsonValueKind.Object ? T("Every hour at minute {0}", ArmJson.Str(hourly, "minute")) : "-";
        }

        var notification = ArmJson.Obj(p, "notificationSettings");
        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Status"), status),
                ArmJson.Row(T("Task type"), ArmJson.Str(p, "taskType")),
                ArmJson.Row(T("Recurrence"), Recurrence()),
                ArmJson.Row(T("Time zone"), ArmJson.Str(p, "timeZoneId")),
                ArmJson.Row(T("Target resource"), ArmJson.LastSegment(ArmJson.Str(p, "targetResourceId"))),
                ArmJson.Row(T("Notifications"), ArmJson.Str(notification, "status")),
                ArmJson.Row(T("Created at"), ArmJson.Date(ArmJson.Str(p, "createdDate"))),
                ArmJson.Row("Location", ArmJson.Str(schedule, "location")),
            }),
        };

        return new ResourceOverview(status, sections, Array.Empty<InfoTable>(), Tags(schedule));
    }

    public async Task<List<VmssInfo>> ListVmssAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<VmssInfo>();
        await foreach (VirtualMachineScaleSetResource vmss in GetSubscription(subscriptionId).GetVirtualMachineScaleSetsAsync(ct))
        {
            result.Add(new VmssInfo(
                vmss.Data.Name,
                vmss.Data.Location.Name,
                vmss.Id.ResourceGroupName ?? "-",
                (int)(vmss.Data.Sku?.Capacity ?? 0),
                vmss.Data.OrchestrationMode?.ToString(),
                vmss.Id.ToString()));
        }

        return result;
    }

    public async Task<List<ManagedDiskInfo>> ListManagedDisksAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<ManagedDiskInfo>();
        await foreach (ManagedDiskResource disk in GetSubscription(subscriptionId).GetManagedDisksAsync(ct))
        {
            result.Add(new ManagedDiskInfo(
                disk.Data.Name,
                disk.Data.Location.Name,
                disk.Id.ResourceGroupName ?? "-",
                disk.Data.DiskSizeGB,
                disk.Data.DiskState?.ToString(),
                !string.IsNullOrEmpty(disk.Data.ManagedBy),
                disk.Data.Sku?.Name?.ToString() ?? "-",
                disk.Id.ToString()));
        }

        return result;
    }

    public async Task<List<SnapshotInfo>> ListSnapshotsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<SnapshotInfo>();
        await foreach (SnapshotResource snapshot in GetSubscription(subscriptionId).GetSnapshotsAsync(ct))
        {
            result.Add(new SnapshotInfo(
                snapshot.Data.Name,
                snapshot.Data.Location.Name,
                snapshot.Id.ResourceGroupName ?? "-",
                snapshot.Data.DiskSizeGB,
                snapshot.Data.TimeCreated,
                snapshot.Id.ToString()));
        }

        return result;
    }

    public async Task DeleteManagedDiskAsync(string diskResourceId, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the two lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteManagedDiskAsync() called for {diskResourceId} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        var disk = _clientFactory.GetClient().GetManagedDiskResource(new Azure.Core.ResourceIdentifier(diskResourceId));
        await disk.DeleteAsync(Azure.WaitUntil.Completed, ct);
    }

    /// <summary>
    /// Creates a snapshot as a full copy of the source disk (<c>DiskCreateOption.Copy</c>), in the
    /// same resource group/location as the disk itself.
    /// </summary>
    public async Task<SnapshotInfo> CreateSnapshotAsync(string diskResourceId, string snapshotName, CancellationToken ct = default)
    {
        var diskId = new Azure.Core.ResourceIdentifier(diskResourceId);
        var disk = _clientFactory.GetClient().GetManagedDiskResource(diskId);
        var diskData = (await disk.GetAsync(ct)).Value.Data;

        var resourceGroup = _clientFactory.GetClient().GetResourceGroupResource(
            ResourceGroupResource.CreateResourceIdentifier(diskId.SubscriptionId!, diskId.ResourceGroupName!));

        var snapshotData = new SnapshotData(diskData.Location)
        {
            CreationData = new DiskCreationData(DiskCreateOption.Copy) { SourceResourceId = diskId },
        };

        var operation = await resourceGroup.GetSnapshots().CreateOrUpdateAsync(Azure.WaitUntil.Completed, snapshotName, snapshotData, ct);
        var snapshot = operation.Value;

        return new SnapshotInfo(
            snapshot.Data.Name, snapshot.Data.Location.Name, diskId.ResourceGroupName ?? "-",
            snapshot.Data.DiskSizeGB, snapshot.Data.TimeCreated, snapshot.Id.ToString());
    }

    public async Task DeleteSnapshotAsync(string snapshotResourceId, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the two lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteSnapshotAsync() called for {snapshotResourceId} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        var snapshot = _clientFactory.GetClient().GetSnapshotResource(new Azure.Core.ResourceIdentifier(snapshotResourceId));
        await snapshot.DeleteAsync(Azure.WaitUntil.Completed, ct);
    }

    public async Task<List<AppServicePlanInfo>> ListAppServicePlansAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<AppServicePlanInfo>();
        await foreach (AppServicePlanResource plan in GetSubscription(subscriptionId).GetAppServicePlansAsync(detailed: null, ct))
        {
            result.Add(new AppServicePlanInfo(
                plan.Data.Name,
                plan.Data.Location.Name,
                plan.Id.ResourceGroupName ?? "-",
                plan.Data.Sku?.Name ?? "-",
                plan.Data.NumberOfSites ?? 0,
                plan.Data.NumberOfWorkers,
                plan.Id.ToString()));
        }

        return result;
    }

    public async Task<List<DevTestLabScheduleInfo>> ListDevTestLabSchedulesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<DevTestLabScheduleInfo>();
        await foreach (DevTestLabGlobalScheduleResource schedule in GetSubscription(subscriptionId)
            .GetDevTestLabGlobalSchedulesAsync(expand: null, filter: null, top: null, orderby: null, ct))
        {
            result.Add(new DevTestLabScheduleInfo(
                schedule.Data.Name,
                schedule.Id.ResourceGroupName ?? "-",
                schedule.Data.Status?.ToString(),
                schedule.Data.TaskType,
                schedule.Data.TargetResourceId,
                schedule.Id.ToString()));
        }

        return result;
    }
}
