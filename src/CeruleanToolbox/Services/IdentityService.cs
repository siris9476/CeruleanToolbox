using System.Text.Json;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record ManagedIdentityInfo(
    string Name, string Location, string ResourceGroup, string? PrincipalId, string? ClientId, string Id);

public sealed class IdentityService
{
    private const string IdentityApiVersion = "2023-01-31";
    private const string B2CApiVersion = "2023-05-17-preview";
    private const string AuthorizationApiVersion = "2022-04-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public IdentityService(AzureClientFactory clientFactory, ArmRestClient arm)
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

    public async Task<ResourceOverview> GetManagedIdentityOverviewAsync(string identityResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(identityResourceId, IdentityApiVersion, ct: ct);
        var identity = doc.RootElement;
        var p = ArmJson.Obj(identity, "properties");
        var principalId = ArmJson.Str(p, "principalId");
        var subscriptionId = new Azure.Core.ResourceIdentifier(identityResourceId).SubscriptionId;

        // Federated credentials (workload identity) and role assignments are separate calls; a
        // missing permission on either must not hide the rest of the overview.
        var federated = new List<IReadOnlyList<string>>();
        using (var fedDoc = await TryGetAsync($"{identityResourceId}/federatedIdentityCredentials", IdentityApiVersion, ct))
        {
            foreach (var f in fedDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(fedDoc.RootElement, "value"))
            {
                var fp = ArmJson.Obj(f, "properties");
                federated.Add(new[]
                {
                    ArmJson.Str(f, "name"), ArmJson.Str(fp, "issuer"), ArmJson.Str(fp, "subject"),
                    string.Join(", ", ArmJson.Items(fp, "audiences").Select(a => a.GetString())),
                });
            }
        }

        var roleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var roleDoc = await TryGetAsync($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions", AuthorizationApiVersion, ct))
        {
            foreach (var r in roleDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(roleDoc.RootElement, "value"))
            {
                roleNames[ArmJson.LastSegment(ArmJson.Str(r, "id"))] = ArmJson.Str(ArmJson.Obj(r, "properties"), "roleName");
            }
        }

        var assignments = new List<IReadOnlyList<string>>();
        if (principalId != "-")
        {
            using var assignDoc = await TryGetAsync(
                $"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleAssignments", AuthorizationApiVersion, ct,
                $"$filter=principalId eq '{principalId}'");
            foreach (var a in assignDoc is null ? Enumerable.Empty<JsonElement>() : ArmJson.Items(assignDoc.RootElement, "value"))
            {
                var ap = ArmJson.Obj(a, "properties");
                var roleId = ArmJson.LastSegment(ArmJson.Str(ap, "roleDefinitionId"));
                var scope = ArmJson.Str(ap, "scope");
                assignments.Add(new[]
                {
                    roleNames.TryGetValue(roleId, out var roleName) ? roleName : roleId,
                    scope.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase) || scope.Split('/').Length > 3 ? ArmJson.LastSegment(scope) : "Subscription",
                    scope,
                });
            }
        }

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Location", ArmJson.Str(identity, "location")),
                ArmJson.Row("Principal ID (Object ID)", principalId),
                ArmJson.Row("Client ID", ArmJson.Str(p, "clientId")),
                ArmJson.Row("Tenant ID", ArmJson.Str(p, "tenantId")),
                ArmJson.Row("Isolation scope", ArmJson.Str(p, "isolationScope")),
                ArmJson.Row(T("Federated credentials"), federated.Count.ToString()),
                ArmJson.Row(T("Assigned roles (in the subscription)"), assignments.Count.ToString()),
            }),
        };

        var tables = new List<InfoTable>
        {
            new(T("Role assignments"), new[] { T("Role"), T("Scope"), T("Full scope") }, assignments),
            new(T("Federated credentials"), new[] { T("Name"), "Issuer", "Subject", "Audience" }, federated),
        };

        return new ResourceOverview(T("Present"), sections, tables, Tags(identity));
    }

    public async Task<ResourceOverview> GetB2CDirectoryOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, B2CApiVersion, ct: ct);
        var directory = doc.RootElement;
        var p = ArmJson.Obj(directory, "properties");
        var sku = ArmJson.Obj(directory, "sku");
        var billing = ArmJson.Obj(p, "billingConfig");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Location", ArmJson.Str(directory, "location")),
                ArmJson.Row("Tenant ID", ArmJson.Str(p, "tenantId")),
                ArmJson.Row("SKU", $"{ArmJson.Str(sku, "name")} ({ArmJson.Str(sku, "tier")})"),
                ArmJson.Row(T("Billing type"), ArmJson.Str(billing, "billingType")),
                ArmJson.Row(T("Billing active since"), ArmJson.Date(ArmJson.Str(billing, "effectiveStartDateUtc"))),
            }),
        };

        return new ResourceOverview(T("Present"), sections, Array.Empty<InfoTable>(), Tags(directory));
    }

    private SubscriptionResource GetSubscription(string subscriptionId) =>
        _clientFactory.GetClient().GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

    public async Task<List<ManagedIdentityInfo>> ListManagedIdentitiesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<ManagedIdentityInfo>();
        await foreach (UserAssignedIdentityResource identity in GetSubscription(subscriptionId).GetUserAssignedIdentitiesAsync(ct))
        {
            result.Add(new ManagedIdentityInfo(
                identity.Data.Name,
                identity.Data.Location.Name,
                identity.Id.ResourceGroupName ?? "-",
                identity.Data.PrincipalId?.ToString(),
                identity.Data.ClientId?.ToString(),
                identity.Id.ToString()));
        }

        return result;
    }

    /// <summary>
    /// AAD B2C Directories have no dedicated ARM SDK package — a generic ARM properties readout is
    /// the pragmatic stand-in, same approach used for the other legacy/niche types in Monitoring.
    /// </summary>
    public async Task<List<RawResourceInfo>> ListB2CDirectoriesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<RawResourceInfo>();
        var filter = "resourceType eq 'Microsoft.AzureActiveDirectory/b2cDirectories'";
        await foreach (var resource in GetSubscription(subscriptionId).GetGenericResourcesAsync(filter, expand: null, top: null, ct))
        {
            result.Add(new RawResourceInfo(
                resource.Data.Name, resource.Data.Location.Name, resource.Id.ResourceGroupName ?? "-", resource.Id.ToString()));
        }

        return result;
    }
}
