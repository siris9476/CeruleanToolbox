using System.Text.Json;
using Azure;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record VaultInfo(string Name, string Location, string? VaultUri, string ResourceGroup, string Id);

public sealed class KeyVaultManagementService
{
    private const string VaultApiVersion = "2023-07-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public KeyVaultManagementService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetOverviewAsync(string vaultResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(vaultResourceId, VaultApiVersion, ct: ct);
        var vault = doc.RootElement;
        var p = ArmJson.Obj(vault, "properties");
        var acls = ArmJson.Obj(p, "networkAcls");
        var sku = ArmJson.Obj(p, "sku");
        var rbac = ArmJson.Str(p, "enableRbacAuthorization") == T("Yes");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var softDelete = ArmJson.Str(p, "enableSoftDelete") == T("No")
            ? T("Off")
            : T("On ({0} days)", ArmJson.Str(p, "softDeleteRetentionInDays"));

        var sections = new List<InfoSection>
        {
            new(T("Essentials"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(vault, "location")),
                ArmJson.Row(T("Vault URI"), ArmJson.Str(p, "vaultUri")),
                ArmJson.Row("SKU", ArmJson.Str(sku, "name")),
                ArmJson.Row("Tenant ID", ArmJson.Str(p, "tenantId")),
                ArmJson.Row(T("Created on"), ArmJson.Date(ArmJson.Str(ArmJson.Obj(vault, "systemData"), "createdAt"))),
                ArmJson.Row(T("Last modified"), ArmJson.Date(ArmJson.Str(ArmJson.Obj(vault, "systemData"), "lastModifiedAt"))),
            }),
            new(T("Authorization and protection"), new[]
            {
                ArmJson.Row(T("Permission model"), rbac ? "Azure RBAC" : T("Access policy")),
                ArmJson.Row(T("Soft delete"), softDelete),
                ArmJson.Row(T("Purge protection"), ArmJson.Str(p, "enablePurgeProtection") == T("Yes") ? T("On") : T("Off")),
                ArmJson.Row(T("Access for Virtual Machines (deployment)"), ArmJson.Str(p, "enabledForDeployment")),
                ArmJson.Row(T("Access for disk encryption"), ArmJson.Str(p, "enabledForDiskEncryption")),
                ArmJson.Row(T("Access for ARM templates"), ArmJson.Str(p, "enabledForTemplateDeployment")),
            }),
            new(T("Networking"), new[]
            {
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess")),
                ArmJson.Row(T("Default network rule"),
                    acls.ValueKind == JsonValueKind.Object ? ArmJson.Str(acls, "defaultAction") : T("Allow (no firewall configured)")),
                ArmJson.Row(T("Exceptions (bypass)"), acls.ValueKind == JsonValueKind.Object ? ArmJson.Str(acls, "bypass") : T("None")),
                ArmJson.Row(T("IP rules"), ArmJson.Items(acls, "ipRules").Count().ToString()),
                ArmJson.Row(T("Virtual network rules"), ArmJson.Items(acls, "virtualNetworkRules").Count().ToString()),
                ArmJson.Row(T("Private endpoint connections"), ArmJson.Items(p, "privateEndpointConnections").Count().ToString()),
            }),
        };

        var policies = ArmJson.Items(p, "accessPolicies").Select(a =>
        {
            var perms = ArmJson.Obj(a, "permissions");
            string Join(string key) => string.Join(", ", ArmJson.Items(perms, key).Select(x => x.GetString() ?? ""));
            return (IReadOnlyList<string>)new[] { ArmJson.Str(a, "objectId"), Join("secrets"), Join("keys"), Join("certificates") };
        }).ToList();

        var endpoints = ArmJson.Items(p, "privateEndpointConnections").Select(c =>
        {
            var cp = ArmJson.Obj(c, "properties");
            return (IReadOnlyList<string>)new[]
            {
                ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(cp, "privateEndpoint"), "id")),
                ArmJson.Str(ArmJson.Obj(cp, "privateLinkServiceConnectionState"), "status"),
            };
        }).ToList();

        var ipRules = ArmJson.Items(acls, "ipRules")
            .Select(r => (IReadOnlyList<string>)new[] { ArmJson.Str(r, "value") }).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Private endpoints"), new[] { "Endpoint", T("Status") }, endpoints),
            new(T("IP rules"), new[] { T("Value") }, ipRules),
        };
        if (!rbac)
        {
            tables.Insert(0, new InfoTable(T("Access policies"), new[] { "Object ID", "Secret", T("Keys"), T("Certificates") }, policies));
        }

        var tags = ArmJson.Obj(vault, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(provisioning, sections, tables, tags);
    }

    public async Task<List<VaultInfo>> ListVaultsAsync(
        string subscriptionId, string resourceGroupName, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var rgId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        var rg = client.GetResourceGroupResource(rgId);

        return await CollectAsync(rg.GetKeyVaults().GetAllAsync(cancellationToken: ct));
    }

    /// <summary>Lists every vault across every resource group in the subscription, in one call.</summary>
    public async Task<List<VaultInfo>> ListAllVaultsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        return await CollectAsync(subscription.GetKeyVaultsAsync(cancellationToken: ct));
    }

    private static async Task<List<VaultInfo>> CollectAsync(IAsyncEnumerable<KeyVaultResource> vaults)
    {
        var result = new List<VaultInfo>();
        await foreach (var vault in vaults)
        {
            result.Add(new VaultInfo(
                vault.Data.Name,
                vault.Data.Location.Name,
                vault.Data.Properties?.VaultUri?.ToString(),
                vault.Id!.ResourceGroupName ?? "-",
                vault.Id!.ToString()));
        }

        return result;
    }

    public async Task<string> GetVaultUriAsync(
        string subscriptionId, string resourceGroupName, string vaultName, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var vaultId = KeyVaultResource.CreateResourceIdentifier(subscriptionId, resourceGroupName, vaultName);
        var vault = await client.GetKeyVaultResource(vaultId).GetAsync(ct);

        return vault.Value.Data.Properties?.VaultUri?.ToString()
            ?? throw new InvalidOperationException(T("Vault '{0}' has no VaultUri.", vaultName));
    }

    public async Task<VaultInfo> CreateVaultAsync(
        string subscriptionId, string resourceGroupName, string vaultName, string location, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var rgId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        var rg = client.GetResourceGroupResource(rgId);

        var properties = new KeyVaultProperties(
            _clientFactory.TenantId,
            new KeyVaultSku(KeyVaultSkuFamily.A, KeyVaultSkuName.Standard))
        {
            EnableRbacAuthorization = true,
            EnableSoftDelete = true,
        };

        var content = new KeyVaultCreateOrUpdateContent(location, properties);

        var operation = await rg.GetKeyVaults().CreateOrUpdateAsync(WaitUntil.Completed, vaultName, content, ct);
        var vault = operation.Value;

        return new VaultInfo(
            vault.Data.Name,
            vault.Data.Location.Name,
            vault.Data.Properties?.VaultUri?.ToString(),
            vault.Id!.ResourceGroupName ?? "-",
            vault.Id!.ToString());
    }
}
