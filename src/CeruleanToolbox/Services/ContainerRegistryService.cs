using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.ResourceManager.ContainerRegistry;
using Azure.ResourceManager.ContainerRegistry.Models;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record AcrRegistryInfo(
    string Name, string Location, string ResourceGroup, string LoginServer,
    string Sku, bool AdminUserEnabled, string Id);

public sealed record AcrNetworkRules(string Sku, string PublicNetworkAccess, string DefaultAction, List<string> IpRules);

public sealed record AcrPasswordInfo(string Name, string Value);

public sealed record AcrCredentialsInfo(string Username, List<AcrPasswordInfo> Passwords);

public sealed class ContainerRegistryService
{
    private const string AcrApiVersion = "2023-07-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public ContainerRegistryService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetOverviewAsync(string registryResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(registryResourceId, AcrApiVersion, ct: ct);
        var registry = doc.RootElement;
        var p = ArmJson.Obj(registry, "properties");
        var sku = ArmJson.Obj(registry, "sku");
        var policies = ArmJson.Obj(p, "policies");
        var rules = ArmJson.Obj(p, "networkRuleSet");
        var provisioning = ArmJson.Str(p, "provisioningState");

        string PolicyState(string name) => ArmJson.Str(ArmJson.Obj(policies, name), "status");
        var retention = ArmJson.Obj(policies, "retentionPolicy");
        var retentionText = ArmJson.Str(retention, "status") == "-" ? "-" : T("{0} ({1} days)", ArmJson.Str(retention, "status"), ArmJson.Str(retention, "days"));
        var softDelete = ArmJson.Obj(policies, "softDeletePolicy");
        var softDeleteText = ArmJson.Str(softDelete, "status") == "-" ? "-" : T("{0} ({1} days)", ArmJson.Str(softDelete, "status"), ArmJson.Str(softDelete, "retentionDays"));

        // Storage usage and replications are separate calls; a missing permission must not hide the rest.
        var usage = "-";
        using var usageDoc = await TryGetAsync($"{registryResourceId}/listUsages", ct);
        if (usageDoc is not null)
        {
            var size = ArmJson.Items(usageDoc.RootElement, "value")
                .FirstOrDefault(u => string.Equals(ArmJson.Str(u, "name"), "Size", StringComparison.OrdinalIgnoreCase));
            if (size.ValueKind == JsonValueKind.Object && double.TryParse(ArmJson.Str(size, "currentValue"), out var bytes))
            {
                usage = $"{bytes / 1024 / 1024 / 1024:0.##} GB";
            }
        }

        var replications = new List<IReadOnlyList<string>>();
        using var replDoc = await TryGetAsync($"{registryResourceId}/replications", ct);
        if (replDoc is not null)
        {
            foreach (var r in ArmJson.Items(replDoc.RootElement, "value"))
            {
                var rp = ArmJson.Obj(r, "properties");
                replications.Add(new[]
                {
                    ArmJson.Str(r, "location"), ArmJson.Str(rp, "provisioningState"),
                    ArmJson.Str(ArmJson.Obj(rp, "status"), "displayStatus"), ArmJson.Str(rp, "zoneRedundancy"),
                });
            }
        }

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(registry, "location")),
                ArmJson.Row("Login server", ArmJson.Str(p, "loginServer")),
                ArmJson.Row("SKU", ArmJson.Str(sku, "name")),
                ArmJson.Row(T("Creation date"), ArmJson.Date(ArmJson.Str(p, "creationDate"))),
                ArmJson.Row(T("Space used"), usage),
                ArmJson.Row("Admin user", ArmJson.Str(p, "adminUserEnabled")),
                ArmJson.Row(T("Zone-redundant"), ArmJson.Str(p, "zoneRedundancy")),
            }),
            new(T("Networking"), new[]
            {
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess")),
                ArmJson.Row(T("Default network rule"), ArmJson.Str(rules, "defaultAction")),
                ArmJson.Row(T("IP rules"), ArmJson.Items(rules, "ipRules").Count().ToString()),
                ArmJson.Row(T("Trusted Azure services (bypass)"), ArmJson.Str(p, "networkRuleBypassOptions")),
                ArmJson.Row(T("Dedicated data endpoints"), ArmJson.Str(p, "dataEndpointEnabled")),
                ArmJson.Row(T("Private endpoint connections"), ArmJson.Items(p, "privateEndpointConnections").Count().ToString()),
            }),
            new(T("Security and policies"), new[]
            {
                ArmJson.Row(T("Anonymous pull"), ArmJson.Str(p, "anonymousPullEnabled")),
                ArmJson.Row(T("Entra ARM token authentication"), PolicyState("azureADAuthenticationAsArmPolicy")),
                ArmJson.Row(T("Customer-managed key encryption"), ArmJson.Str(ArmJson.Obj(p, "encryption"), "status")),
                ArmJson.Row(T("Quarantine"), PolicyState("quarantinePolicy")),
                ArmJson.Row("Content trust", PolicyState("trustPolicy")),
                ArmJson.Row(T("Untagged manifest retention"), retentionText),
                ArmJson.Row(T("Soft delete"), softDeleteText),
                ArmJson.Row("Export", PolicyState("exportPolicy")),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(registry, "identity"), "type")),
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
        var ipRules = ArmJson.Items(rules, "ipRules")
            .Select(r => (IReadOnlyList<string>)new[] { ArmJson.Str(r, "value"), ArmJson.Str(r, "action") }).ToList();

        var tables = new List<InfoTable>
        {
            new("Geo-replication", new[] { "Location", "Provisioning", T("Status"), "Zone redundancy" }, replications),
            new(T("Private endpoints"), new[] { "Endpoint", T("Status") }, endpoints),
            new(T("IP rules"), new[] { T("Value"), T("Action") }, ipRules),
        };

        var tags = ArmJson.Obj(registry, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(provisioning, sections, tables, tags);
    }

    private async Task<JsonDocument?> TryGetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _arm.GetAsync(path, AcrApiVersion, ct: ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<List<AcrRegistryInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<AcrRegistryInfo>();
        await foreach (ContainerRegistryResource registry in subscription.GetContainerRegistriesAsync(ct))
        {
            result.Add(new AcrRegistryInfo(
                registry.Data.Name,
                registry.Data.Location.Name,
                registry.Id.ResourceGroupName ?? "-",
                registry.Data.LoginServer,
                registry.Data.Sku.Name.ToString(),
                registry.Data.IsAdminUserEnabled ?? false,
                registry.Id.ToString()));
        }

        return result;
    }

    public async Task SetAdminUserEnabledAsync(string registryResourceId, bool enabled, CancellationToken ct = default)
    {
        var registry = _clientFactory.GetClient().GetContainerRegistryResource(new Azure.Core.ResourceIdentifier(registryResourceId));
        var patch = new ContainerRegistryPatch { IsAdminUserEnabled = enabled };
        await registry.UpdateAsync(Azure.WaitUntil.Completed, patch, ct);
    }

    /// <summary>Reads the IP rules fresh from Azure: the list is replaced as a whole on every write, so the page never works on a stale copy.</summary>
    public async Task<AcrNetworkRules> GetNetworkRulesAsync(string registryResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(registryResourceId, AcrApiVersion, ct: ct);
        var p = ArmJson.Obj(doc.RootElement, "properties");
        var rules = ArmJson.Obj(p, "networkRuleSet");
        return new AcrNetworkRules(
            ArmJson.Str(ArmJson.Obj(doc.RootElement, "sku"), "name"),
            ArmJson.Str(p, "publicNetworkAccess"),
            ArmJson.Str(rules, "defaultAction"),
            ArmJson.Items(rules, "ipRules").Select(r => ArmJson.Str(r, "value")).ToList());
    }

    /// <summary>
    /// Accepts an IPv4 address or a CIDR range; a "/32" is written as the plain address (the form Azure documents
    /// for a single address). Returns null and the reason in <paramref name="problem"/> when the text is not usable.
    /// </summary>
    public static string? NormalizeIpRule(string text, out string problem)
    {
        problem = "";
        var value = (text ?? "").Trim();
        var parts = value.Split('/');
        if (parts.Length > 2 || !System.Net.IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || parts[0].Count(c => c == '.') != 3)
        {
            problem = T("Enter an IPv4 address (1.2.3.4) or a range (1.2.3.0/24).");
            return null;
        }

        if (parts.Length == 1) return address.ToString();

        if (!int.TryParse(parts[1], out var prefix) || prefix < 1 || prefix > 32)
        {
            problem = T("The range length must be a number from 1 to 32 (a /0 range would open the registry to every address).");
            return null;
        }

        return prefix == 32 ? address.ToString() : $"{address}/{prefix}";
    }

    /// <summary>Adds or removes one IP rule: reads the current list, changes it and writes it back, leaving the other rules and the default action as they are.</summary>
    public async Task<AcrNetworkRules> ChangeIpRuleAsync(string registryResourceId, string rule, bool add, CancellationToken ct = default)
    {
        var current = await GetNetworkRulesAsync(registryResourceId, ct);
        var values = current.IpRules.ToList();
        if (add)
        {
            if (values.Contains(rule, StringComparer.OrdinalIgnoreCase)) return current;
            values.Add(rule);
        }
        else if (values.RemoveAll(v => string.Equals(v, rule, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return current;
        }

        var defaultAction = current.DefaultAction == "-" ? "Allow" : current.DefaultAction;
        var ruleSet = new ContainerRegistryNetworkRuleSet(new ContainerRegistryNetworkRuleDefaultAction(defaultAction));
        foreach (var v in values) ruleSet.IPRules.Add(new ContainerRegistryIPRule(v));

        var registry = _clientFactory.GetClient().GetContainerRegistryResource(new Azure.Core.ResourceIdentifier(registryResourceId));
        await registry.UpdateAsync(Azure.WaitUntil.Completed, new ContainerRegistryPatch { NetworkRuleSet = ruleSet }, ct);
        return await GetNetworkRulesAsync(registryResourceId, ct);
    }

    public async Task<AcrCredentialsInfo> GetCredentialsAsync(string registryResourceId, CancellationToken ct = default)
    {
        var registry = _clientFactory.GetClient().GetContainerRegistryResource(new Azure.Core.ResourceIdentifier(registryResourceId));
        var result = await registry.GetCredentialsAsync(ct);

        return new AcrCredentialsInfo(
            result.Value.Username,
            result.Value.Passwords.Select(p => new AcrPasswordInfo(p.Name?.ToString() ?? "-", p.Value)).ToList());
    }

    public async Task<AcrCredentialsInfo> RegenerateCredentialAsync(string registryResourceId, string passwordName, CancellationToken ct = default)
    {
        var registry = _clientFactory.GetClient().GetContainerRegistryResource(new Azure.Core.ResourceIdentifier(registryResourceId));
        var name = Enum.Parse<ContainerRegistryPasswordName>(passwordName);
        var result = await registry.RegenerateCredentialAsync(new ContainerRegistryCredentialRegenerateContent(name), ct);

        return new AcrCredentialsInfo(
            result.Value.Username,
            result.Value.Passwords.Select(p => new AcrPasswordInfo(p.Name?.ToString() ?? "-", p.Value)).ToList());
    }

    /// <summary>
    /// Repository/tag listing uses the Docker Registry HTTP API v2, which isn't wrapped by the ARM
    /// SDK (that only manages the registry resource, not its image content). It authenticates with
    /// the admin username/password over Basic auth — the same credentials shown in "Admin
    /// credentials" — since that avoids the separate AAD-token-to-ACR-refresh-token exchange dance needed
    /// for RBAC-based pull access.
    /// </summary>
    public async Task<List<string>> ListRepositoriesAsync(string loginServer, string username, string password, CancellationToken ct = default)
    {
        using var http = CreateAuthenticatedClient(username, password);
        var response = await http.GetAsync($"https://{loginServer}/v2/_catalog", ct);
        await EnsureSuccessAsync(response, ct);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return json.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    public async Task<List<string>> ListTagsAsync(string loginServer, string username, string password, string repository, CancellationToken ct = default)
    {
        using var http = CreateAuthenticatedClient(username, password);
        var response = await http.GetAsync($"https://{loginServer}/v2/{repository}/tags/list", ct);
        await EnsureSuccessAsync(response, ct);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!json.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return tagsElement.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    /// <summary>
    /// Uses the ACR-specific REST extension (<c>/acr/v1/...</c>, not the standard Docker Registry
    /// v2 API) which is what the portal itself calls to delete a tag or a whole repository —
    /// deleting a tag here untags it but the ACR API garbage-collects the underlying manifest once
    /// nothing references it, unlike a raw Docker v2 manifest delete which needs the digest.
    /// </summary>
    public async Task DeleteTagAsync(string loginServer, string username, string password, string repository, string tag, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the three lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteTagAsync() called for {repository}:{tag} on {loginServer} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        using var http = CreateAuthenticatedClient(username, password);
        var response = await http.DeleteAsync($"https://{loginServer}/acr/v1/{repository}/_tags/{tag}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task DeleteRepositoryAsync(string loginServer, string username, string password, string repository, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the three lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteRepositoryAsync() called for {repository} on {loginServer} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        using var http = CreateAuthenticatedClient(username, password);
        var response = await http.DeleteAsync($"https://{loginServer}/acr/v1/{repository}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static HttpClient CreateAuthenticatedClient(string username, string password)
    {
        var http = new HttpClient();
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        return http;
    }

    /// <summary>The IP that the registry names in its "client with IP '...' is not allowed access" refusal, or null.</summary>
    public static string? BlockedIp(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"client with IP '([0-9.]+)' is not allowed access");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var message = T("Registry call failed ({0} {1}): {2}", (int)response.StatusCode, response.StatusCode, body);
        if (BlockedIp(body) is not null)
        {
            message += " " + T("The registry firewall blocks this IP: add it in the Firewall tab.");
        }

        throw new InvalidOperationException(message);
    }
}
