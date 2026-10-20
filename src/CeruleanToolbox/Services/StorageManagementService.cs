using System.Text.Json;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.Storage.Blobs;

namespace CeruleanToolbox.Services;

public sealed record StorageAccountInfo(
    string Name, string Location, string ResourceGroup, string? BlobEndpoint, string Id,
    string PublicNetworkAccess, string NetworkDefaultAction, bool HasNetworkRestrictions,
    string Kind, string Replication);
public sealed record BlobContainerInfo(string Name, bool PublicAccess);
public sealed record BlobItemInfo(string Name, long? SizeBytes, DateTimeOffset? LastModified, bool LooksLikeTerraformState);
public sealed record TerraformStateCandidate(string ContainerName, string BlobName, long? SizeBytes, DateTimeOffset? LastModified);
public sealed record TerraformStateCheckResult(bool IsTerraformState, string? TerraformVersion, int? ResourceCount, string? Error);

public sealed class StorageManagementService
{
    private const string StorageApiVersion = "2023-05-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public StorageManagementService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetOverviewAsync(string accountResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(accountResourceId, StorageApiVersion, ct: ct);
        var account = doc.RootElement;
        var p = ArmJson.Obj(account, "properties");
        var sku = ArmJson.Obj(account, "sku");
        var acls = ArmJson.Obj(p, "networkAcls");
        var endpoints = ArmJson.Obj(p, "primaryEndpoints");
        var secondary = ArmJson.Obj(p, "secondaryEndpoints");

        // Blob service settings and the container list come from the management plane, so they
        // still work when the account's firewall blocks data-plane calls from this machine.
        JsonElement blobService = default;
        using var blobDoc = await TryGetAsync($"{accountResourceId}/blobServices/default", ct);
        if (blobDoc is not null) blobService = ArmJson.Obj(blobDoc.RootElement, "properties");

        var containers = new List<IReadOnlyList<string>>();
        using var containerDoc = await TryGetAsync($"{accountResourceId}/blobServices/default/containers", ct);
        if (containerDoc is not null)
        {
            foreach (var c in ArmJson.Items(containerDoc.RootElement, "value"))
            {
                var cp = ArmJson.Obj(c, "properties");
                containers.Add(new[]
                {
                    ArmJson.Str(c, "name"), ArmJson.Str(cp, "publicAccess"), ArmJson.Str(cp, "leaseState"),
                    ArmJson.Date(ArmJson.Str(cp, "lastModifiedTime")),
                    ArmJson.Str(cp, "hasImmutabilityPolicy") == T("Yes") || ArmJson.Str(cp, "hasLegalHold") == T("Yes") ? T("Yes") : T("No"),
                });
            }
        }

        var deleteRetention = ArmJson.Obj(blobService, "deleteRetentionPolicy");
        var softDelete = ArmJson.Str(deleteRetention, "enabled") == T("Yes") ? T("Enabled (days: {0})", ArmJson.Str(deleteRetention, "days")) : T("Disabled");
        var containerRetention = ArmJson.Obj(blobService, "containerDeleteRetentionPolicy");
        var containerSoftDelete = ArmJson.Str(containerRetention, "enabled") == T("Yes") ? T("Enabled (days: {0})", ArmJson.Str(containerRetention, "days")) : T("Disabled");

        var provisioning = ArmJson.Str(p, "provisioningState");
        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row(T("Account type"), ArmJson.Str(account, "kind")),
                ArmJson.Row(T("Performance"), ArmJson.Str(sku, "tier")),
                ArmJson.Row(T("Replication"), ArmJson.Str(sku, "name")),
                ArmJson.Row(T("Access tier"), ArmJson.Str(p, "accessTier")),
                ArmJson.Row(T("Primary location"), $"{ArmJson.Str(p, "primaryLocation")} ({ArmJson.Str(p, "statusOfPrimary")})"),
                ArmJson.Row(T("Secondary location"), ArmJson.Str(p, "secondaryLocation") == "-" ? "-" : $"{ArmJson.Str(p, "secondaryLocation")} ({ArmJson.Str(p, "statusOfSecondary")})"),
                ArmJson.Row(T("Creation date"), ArmJson.Date(ArmJson.Str(p, "creationTime"))),
                ArmJson.Row(T("Hierarchical namespace (Data Lake)"), ArmJson.Str(p, "isHnsEnabled")),
                ArmJson.Row("SFTP", ArmJson.Str(p, "isSftpEnabled")),
                ArmJson.Row("NFS v3", ArmJson.Str(p, "isNfsV3Enabled")),
            }),
            new(T("Network"), new[]
            {
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess") is var access && access != "-" ? access : T("Enabled (default)")),
                ArmJson.Row(T("Default network rule"), ArmJson.Str(acls, "defaultAction")),
                ArmJson.Row(T("Exceptions (bypass)"), ArmJson.Str(acls, "bypass")),
                ArmJson.Row(T("IP rules"), ArmJson.Items(acls, "ipRules").Count().ToString()),
                ArmJson.Row(T("Virtual network rules"), ArmJson.Items(acls, "virtualNetworkRules").Count().ToString()),
                ArmJson.Row(T("Private endpoint connections"), ArmJson.Items(p, "privateEndpointConnections").Count().ToString()),
            }),
            new(T("Security"), new[]
            {
                ArmJson.Row(T("Secure transfer (HTTPS only)"), ArmJson.Str(p, "supportsHttpsTrafficOnly")),
                ArmJson.Row(T("Minimum TLS version"), ArmJson.Str(p, "minimumTlsVersion")),
                ArmJson.Row(T("Blob public access allowed"), ArmJson.Str(p, "allowBlobPublicAccess")),
                ArmJson.Row(T("Shared key access"), ArmJson.Str(p, "allowSharedKeyAccess")),
                ArmJson.Row(T("Cross-tenant replication"), ArmJson.Str(p, "allowCrossTenantReplication")),
                ArmJson.Row(T("Default Entra authentication in the portal"), ArmJson.Str(p, "defaultToOAuthAuthentication")),
                ArmJson.Row(T("Encryption (key source)"), ArmJson.Str(ArmJson.Obj(p, "encryption"), "keySource")),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(account, "identity"), "type")),
                ArmJson.Row(T("Key 1 created on"), ArmJson.Date(ArmJson.Str(ArmJson.Obj(p, "keyCreationTime"), "key1"))),
                ArmJson.Row(T("Key 2 created on"), ArmJson.Date(ArmJson.Str(ArmJson.Obj(p, "keyCreationTime"), "key2"))),
            }),
            new(T("Data protection (blob service)"), new[]
            {
                ArmJson.Row(T("Blob soft delete"), softDelete),
                ArmJson.Row(T("Container soft delete"), containerSoftDelete),
                ArmJson.Row("Versioning", ArmJson.Str(blobService, "isVersioningEnabled")),
                ArmJson.Row("Change feed", ArmJson.Str(ArmJson.Obj(blobService, "changeFeed"), "enabled")),
                ArmJson.Row(T("Point-in-time restore"), ArmJson.Str(ArmJson.Obj(blobService, "restorePolicy"), "enabled")),
            }),
            new("Endpoint", new[]
            {
                ArmJson.Row("Blob", ArmJson.Str(endpoints, "blob")),
                ArmJson.Row("File", ArmJson.Str(endpoints, "file")),
                ArmJson.Row("Queue", ArmJson.Str(endpoints, "queue")),
                ArmJson.Row("Table", ArmJson.Str(endpoints, "table")),
                ArmJson.Row("Data Lake (dfs)", ArmJson.Str(endpoints, "dfs")),
                ArmJson.Row(T("Static website"), ArmJson.Str(endpoints, "web")),
                ArmJson.Row(T("Secondary blob"), ArmJson.Str(secondary, "blob")),
            }),
        };

        var ipRules = ArmJson.Items(acls, "ipRules")
            .Select(r => (IReadOnlyList<string>)new[] { ArmJson.Str(r, "value"), ArmJson.Str(r, "action") }).ToList();
        var vnetRules = ArmJson.Items(acls, "virtualNetworkRules")
            .Select(r => (IReadOnlyList<string>)new[] { ArmJson.LastSegment(ArmJson.Str(r, "id")), ArmJson.Str(r, "state") }).ToList();
        var privateEndpoints = ArmJson.Items(p, "privateEndpointConnections").Select(c =>
        {
            var cp = ArmJson.Obj(c, "properties");
            return (IReadOnlyList<string>)new[]
            {
                ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(cp, "privateEndpoint"), "id")),
                ArmJson.Str(ArmJson.Obj(cp, "privateLinkServiceConnectionState"), "status"),
            };
        }).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Containers (from the management plane)"), new[] { T("Name"), T("Public access"), "Lease", T("Last modified"), T("Immutability / legal hold") }, containers),
            new(T("Private endpoints"), new[] { "Endpoint", T("Status") }, privateEndpoints),
            new(T("IP rules"), new[] { T("Value"), T("Action") }, ipRules),
            new(T("Virtual network rules"), new[] { "Subnet", T("Status") }, vnetRules),
        };

        var tags = ArmJson.Obj(account, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(provisioning, sections, tables, tags);
    }

    private async Task<JsonDocument?> TryGetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _arm.GetAsync(path, StorageApiVersion, ct: ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<List<StorageAccountInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<StorageAccountInfo>();
        await foreach (StorageAccountResource account in subscription.GetStorageAccountsAsync(ct))
        {
            var defaultAction = account.Data.NetworkRuleSet?.DefaultAction.ToString() ?? "Allow";
            var hasRestrictions =
                account.Data.PublicNetworkAccess?.ToString() == "Disabled" ||
                string.Equals(defaultAction, "Deny", StringComparison.OrdinalIgnoreCase);

            result.Add(new StorageAccountInfo(
                account.Data.Name,
                account.Data.Location.Name,
                account.Id!.ResourceGroupName ?? "-",
                account.Data.PrimaryEndpoints?.BlobUri?.ToString(),
                account.Id!.ToString(),
                account.Data.PublicNetworkAccess?.ToString() ?? "Enabled",
                defaultAction,
                hasRestrictions,
                account.Data.Kind?.ToString() ?? "-",
                account.Data.Sku?.Name.ToString() ?? "-"));
        }

        return result;
    }

    public async Task<List<BlobContainerInfo>> ListContainersAsync(string blobEndpoint, CancellationToken ct = default)
    {
        var client = CreateBlobServiceClient(blobEndpoint);

        var result = new List<BlobContainerInfo>();
        await foreach (var container in client.GetBlobContainersAsync(cancellationToken: ct))
        {
            result.Add(new BlobContainerInfo(container.Name, container.Properties.PublicAccess is not null));
        }

        return result;
    }

    public async Task<List<BlobItemInfo>> ListBlobsAsync(string blobEndpoint, string containerName, CancellationToken ct = default)
    {
        var containerClient = CreateBlobServiceClient(blobEndpoint).GetBlobContainerClient(containerName);

        var result = new List<BlobItemInfo>();
        await foreach (var blob in containerClient.GetBlobsAsync(cancellationToken: ct))
        {
            result.Add(new BlobItemInfo(
                blob.Name, blob.Properties.ContentLength, blob.Properties.LastModified, LooksLikeTerraformState(blob.Name)));
        }

        return result;
    }

    /// <summary>
    /// Scans every container in the storage account for blobs whose name looks like Terraform
    /// state (ends in ".tfstate" — this covers the default `key` and Terraform workspace paths
    /// like "env:/prod/terraform.tfstate"). This is a filename heuristic only; use
    /// <see cref="VerifyTerraformStateAsync"/> on a candidate to confirm it by content.
    /// </summary>
    public async Task<List<TerraformStateCandidate>> ScanForTerraformStateAsync(string blobEndpoint, CancellationToken ct = default)
    {
        var serviceClient = CreateBlobServiceClient(blobEndpoint);
        var result = new List<TerraformStateCandidate>();

        await foreach (var container in serviceClient.GetBlobContainersAsync(cancellationToken: ct))
        {
            var containerClient = serviceClient.GetBlobContainerClient(container.Name);
            await foreach (var blob in containerClient.GetBlobsAsync(cancellationToken: ct))
            {
                if (LooksLikeTerraformState(blob.Name))
                {
                    result.Add(new TerraformStateCandidate(
                        container.Name, blob.Name, blob.Properties.ContentLength, blob.Properties.LastModified));
                }
            }
        }

        return result;
    }

    /// <summary>Downloads the blob and confirms it actually parses as Terraform state JSON.</summary>
    public async Task<TerraformStateCheckResult> VerifyTerraformStateAsync(
        string blobEndpoint, string containerName, string blobName, CancellationToken ct = default)
    {
        try
        {
            var blobClient = CreateBlobServiceClient(blobEndpoint).GetBlobContainerClient(containerName).GetBlobClient(blobName);
            var download = await blobClient.DownloadContentAsync(ct);

            using var json = JsonDocument.Parse(download.Value.Content.ToStream());
            var root = json.RootElement;

            if (root.TryGetProperty("terraform_version", out var versionElement) &&
                root.TryGetProperty("resources", out var resourcesElement) &&
                resourcesElement.ValueKind == JsonValueKind.Array)
            {
                return new TerraformStateCheckResult(true, versionElement.GetString(), resourcesElement.GetArrayLength(), null);
            }

            return new TerraformStateCheckResult(false, null, null, T("The blob does not have the expected schema of a Terraform state file ('terraform_version'/'resources' are missing)."));
        }
        catch (Exception ex)
        {
            return new TerraformStateCheckResult(false, null, null, ex.Message);
        }
    }

    private BlobServiceClient CreateBlobServiceClient(string blobEndpoint) =>
        new(new Uri(blobEndpoint), _clientFactory.GetCredential());

    private static bool LooksLikeTerraformState(string blobName) =>
        blobName.EndsWith(".tfstate", StringComparison.OrdinalIgnoreCase);
}
