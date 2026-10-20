using System.Text.Json;
using Azure.Data.AppConfiguration;
using Azure.ResourceManager.AppConfiguration;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record AppConfigStoreInfo(
    string Name, string Location, string ResourceGroup, string Endpoint, string Sku, string Id);

public sealed record AppConfigSettingInfo(
    string Key, string? Label, string Value, bool IsReadOnly, DateTimeOffset? LastModified);

public sealed class AppConfigurationService
{
    private const string StoreApiVersion = "2023-03-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public AppConfigurationService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetOverviewAsync(string storeResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(storeResourceId, StoreApiVersion, ct: ct);
        var store = doc.RootElement;
        var p = ArmJson.Obj(store, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(store, "location")),
                ArmJson.Row("Endpoint", ArmJson.Str(p, "endpoint")),
                ArmJson.Row("SKU", ArmJson.Str(ArmJson.Obj(store, "sku"), "name")),
                ArmJson.Row(T("Creation date"), ArmJson.Date(ArmJson.Str(p, "creationDate"))),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(store, "identity"), "type")),
            }),
            new(T("Security and protection"), new[]
            {
                ArmJson.Row(T("Local authentication (access keys)"), ArmJson.Str(p, "disableLocalAuth") == T("Yes") ? T("Off") : T("On")),
                ArmJson.Row(T("Soft delete (days)"), ArmJson.Str(p, "softDeleteRetentionInDays")),
                ArmJson.Row(T("Purge protection"), ArmJson.Str(p, "enablePurgeProtection")),
                ArmJson.Row(T("Customer-managed key encryption"),
                    ArmJson.Str(ArmJson.Obj(ArmJson.Obj(p, "encryption"), "keyVaultProperties"), "keyIdentifier") == "-" ? T("No") : T("Yes")),
            }),
            new(T("Networking"), new[]
            {
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess") is var access && access != "-" ? access : T("Enabled (default)")),
                ArmJson.Row(T("Private endpoint connections"), ArmJson.Items(p, "privateEndpointConnections").Count().ToString()),
            }),
        };

        var endpoints = ArmJson.Items(p, "privateEndpointConnections").Select(c =>
        {
            var cp = ArmJson.Obj(c, "properties");
            return (IReadOnlyList<string>)new[]
            {
                ArmJson.LastSegment(ArmJson.Str(ArmJson.Obj(cp, "privateEndpoint"), "id")),
                ArmJson.Str(ArmJson.Obj(cp, "privateLinkServiceConnectionState"), "status"),
            };
        }).ToList();

        var tables = new List<InfoTable> { new(T("Private endpoints"), new[] { "Endpoint", T("Status") }, endpoints) };

        var tags = ArmJson.Obj(store, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(provisioning, sections, tables, tags);
    }

    public async Task<List<AppConfigStoreInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<AppConfigStoreInfo>();
        await foreach (AppConfigurationStoreResource store in subscription.GetAppConfigurationStoresAsync(skipToken: null, ct))
        {
            result.Add(new AppConfigStoreInfo(
                store.Data.Name,
                store.Data.Location.Name,
                store.Id.ResourceGroupName ?? "-",
                store.Data.Endpoint,
                store.Data.SkuName,
                store.Id.ToString()));
        }

        return result;
    }

    public async Task<List<AppConfigSettingInfo>> ListSettingsAsync(string endpoint, CancellationToken ct = default)
    {
        var configClient = new ConfigurationClient(new Uri(endpoint), _clientFactory.GetCredential());

        var result = new List<AppConfigSettingInfo>();
        await foreach (var setting in configClient.GetConfigurationSettingsAsync(new SettingSelector(), ct))
        {
            result.Add(new AppConfigSettingInfo(
                setting.Key, setting.Label, setting.Value, setting.IsReadOnly ?? false, setting.LastModified));
        }

        return result.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SetSettingAsync(string endpoint, string key, string value, string? label, CancellationToken ct = default)
    {
        var configClient = new ConfigurationClient(new Uri(endpoint), _clientFactory.GetCredential());
        await configClient.SetConfigurationSettingAsync(key, value, label, ct);
    }

    public async Task DeleteSettingAsync(string endpoint, string key, string? label, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the two lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteSettingAsync() called for key={key} label={label} on {endpoint} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        var configClient = new ConfigurationClient(new Uri(endpoint), _clientFactory.GetCredential());
        await configClient.DeleteConfigurationSettingAsync(key, label, ct);
    }
}
