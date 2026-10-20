using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record SubscriptionInfo(string SubscriptionId, string DisplayName);
public sealed record ResourceGroupInfo(string Name, string Location);
public sealed record GenericResourceInfo(string Name, string ResourceType, string Location, string ResourceGroup, string Id);

public sealed class ResourceInventoryService
{
    private readonly AzureClientFactory _clientFactory;

    public ResourceInventoryService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<List<SubscriptionInfo>> ListSubscriptionsAsync(CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var result = new List<SubscriptionInfo>();

        await foreach (SubscriptionResource sub in client.GetSubscriptions().GetAllAsync(ct))
        {
            result.Add(new SubscriptionInfo(sub.Data.SubscriptionId, sub.Data.DisplayName));
        }

        return result;
    }

    public async Task<List<ResourceGroupInfo>> ListResourceGroupsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<ResourceGroupInfo>();
        await foreach (ResourceGroupResource rg in subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct))
        {
            result.Add(new ResourceGroupInfo(rg.Data.Name, rg.Data.Location.Name));
        }

        return result;
    }

    public async Task<List<GenericResourceInfo>> ListResourcesAsync(
        string subscriptionId, string resourceGroupName, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var rgId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        var rg = client.GetResourceGroupResource(rgId);

        return await CollectAsync(rg.GetGenericResourcesAsync(cancellationToken: ct));
    }

    /// <summary>Lists every resource across every resource group in the subscription, in one call.</summary>
    public async Task<List<GenericResourceInfo>> ListAllResourcesAsync(
        string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        return await CollectAsync(subscription.GetGenericResourcesAsync(cancellationToken: ct));
    }

    private static async Task<List<GenericResourceInfo>> CollectAsync(
        IAsyncEnumerable<Azure.ResourceManager.Resources.GenericResource> resources)
    {
        var result = new List<GenericResourceInfo>();
        await foreach (var resource in resources)
        {
            result.Add(new GenericResourceInfo(
                resource.Data.Name,
                resource.Data.ResourceType.ToString(),
                resource.Data.Location.Name,
                resource.Id.ResourceGroupName ?? "-",
                resource.Data.Id!.ToString()));
        }

        return result;
    }
}
