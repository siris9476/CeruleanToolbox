using System.Text.Json;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;

namespace CeruleanToolbox.Services;

public sealed record ResourceGraphQueryResult(long TotalRecords, string JsonRows);

public sealed class ResourceGraphQueryService
{
    private readonly AzureClientFactory _clientFactory;

    public ResourceGraphQueryService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<ResourceGraphQueryResult> RunQueryAsync(
        IReadOnlyCollection<string> subscriptionIds, string query, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        Azure.ResourceManager.Resources.TenantResource? tenant = null;
        await foreach (var candidate in client.GetTenants().GetAllAsync(ct))
        {
            tenant = candidate;
            break;
        }

        if (tenant is null)
        {
            throw new InvalidOperationException(T("No tenant accessible with these credentials."));
        }

        var content = new ResourceQueryContent(query);
        foreach (var subscriptionId in subscriptionIds)
        {
            content.Subscriptions.Add(subscriptionId);
        }

        var response = await tenant.GetResourcesAsync(content, ct);
        var result = response.Value;

        var prettyJson = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(result.Data.ToStream()),
            new JsonSerializerOptions { WriteIndented = true });

        return new ResourceGraphQueryResult(result.TotalRecords, prettyJson);
    }
}
