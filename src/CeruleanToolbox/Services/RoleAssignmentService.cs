using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record RoleAssignmentInfo(string Scope, string RoleName, string AssignmentId);

public sealed record RoleAssignmentDetail(
    string Scope, string RoleName, string PrincipalId, string PrincipalType, string AssignmentId, string? PrincipalName);

public sealed record RoleDefinitionSummary(string Name, string Id);

/// <summary>
/// Answers "who has which role on which scope in this subscription" and lets the active
/// connection's service principal (typically Owner, which includes
/// Microsoft.Authorization/roleAssignments/write) grant or revoke access for any principal —
/// user, group, or service principal — across the subscription.
/// </summary>
public sealed class RoleAssignmentService
{
    private readonly AzureClientFactory _clientFactory;

    public RoleAssignmentService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    /// <summary>
    /// The service principal's own Entra object ID (the "oid" claim), read directly from its
    /// access token — this avoids needing Microsoft Graph permissions just to look itself up.
    /// </summary>
    public async Task<string> GetOwnPrincipalIdAsync(CancellationToken ct = default)
    {
        var credential = _clientFactory.GetCredential();
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct);

        return ExtractOidClaim(token.Token);
    }

    public async Task<List<RoleAssignmentInfo>> ListForPrincipalAsync(
        string subscriptionId, string principalId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var scope = SubscriptionResource.CreateResourceIdentifier(subscriptionId);
        var roleAssignments = client.GetRoleAssignments(scope);

        var result = new List<RoleAssignmentInfo>();
        await foreach (RoleAssignmentResource assignment in roleAssignments.GetAllAsync(
            filter: $"principalId eq '{principalId}'", tenantId: null, skipToken: null, cancellationToken: ct))
        {
            var roleDefinition = await client
                .GetAuthorizationRoleDefinitionResource(assignment.Data.RoleDefinitionId)
                .GetAsync(ct);

            result.Add(new RoleAssignmentInfo(
                assignment.Data.Scope.ToString(),
                roleDefinition.Value.Data.RoleName ?? assignment.Data.RoleDefinitionId.ToString(),
                assignment.Id!.ToString()));
        }

        return result;
    }

    /// <summary>Every role assignment at the subscription scope and everywhere below it (resource groups, resources).</summary>
    public async Task<List<RoleAssignmentDetail>> ListAllInSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var scope = SubscriptionResource.CreateResourceIdentifier(subscriptionId);
        var roleAssignments = client.GetRoleAssignments(scope);

        var assignments = new List<RoleAssignmentResource>();
        await foreach (RoleAssignmentResource assignment in roleAssignments.GetAllAsync(cancellationToken: ct))
        {
            assignments.Add(assignment);
        }

        var roleNameCache = new Dictionary<string, string>();
        var principalNames = await TryResolvePrincipalNamesAsync(
            assignments.Select(a => a.Data.PrincipalId?.ToString()).Where(id => id is not null)!, ct);

        var result = new List<RoleAssignmentDetail>();
        foreach (var assignment in assignments)
        {
            var roleDefId = assignment.Data.RoleDefinitionId.ToString();
            if (!roleNameCache.TryGetValue(roleDefId, out var roleName))
            {
                var roleDefinition = await client.GetAuthorizationRoleDefinitionResource(assignment.Data.RoleDefinitionId).GetAsync(ct);
                roleName = roleDefinition.Value.Data.RoleName ?? roleDefId;
                roleNameCache[roleDefId] = roleName;
            }

            var principalId = assignment.Data.PrincipalId?.ToString() ?? "-";
            principalNames.TryGetValue(principalId, out var principalName);

            result.Add(new RoleAssignmentDetail(
                assignment.Data.Scope.ToString(),
                roleName,
                principalId,
                assignment.Data.PrincipalType?.ToString() ?? "Unknown",
                assignment.Id!.ToString(),
                principalName));
        }

        return result;
    }

    /// <summary>Built-in role definitions available at (or above) this subscription, for the "assign a role" picker.</summary>
    public async Task<List<RoleDefinitionSummary>> ListBuiltInRoleDefinitionsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var scope = SubscriptionResource.CreateResourceIdentifier(subscriptionId);

        var result = new List<RoleDefinitionSummary>();
        await foreach (var def in client.GetAuthorizationRoleDefinitions(scope).GetAllAsync(filter: null, cancellationToken: ct))
        {
            if (string.Equals(def.Data.RoleType?.ToString(), "BuiltInRole", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new RoleDefinitionSummary(def.Data.RoleName ?? def.Data.Name, def.Id!.ToString()));
            }
        }

        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task CreateAssignmentAsync(
        string scopeResourceId, string principalId, string roleDefinitionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var scope = new ResourceIdentifier(scopeResourceId);
        var content = new RoleAssignmentCreateOrUpdateContent(new ResourceIdentifier(roleDefinitionId), Guid.Parse(principalId));

        await client.GetRoleAssignments(scope).CreateOrUpdateAsync(WaitUntil.Completed, Guid.NewGuid().ToString(), content, ct);
    }

    public async Task DeleteAssignmentAsync(string assignmentResourceId, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the two lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteAssignmentAsync() called for {assignmentResourceId} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        var assignment = _clientFactory.GetClient().GetRoleAssignmentResource(new ResourceIdentifier(assignmentResourceId));
        await assignment.DeleteAsync(WaitUntil.Completed, cancellationToken: ct);
    }

    /// <summary>
    /// Best-effort display name lookup via Microsoft Graph. Azure RBAC roles (even Owner) grant
    /// nothing on Microsoft Graph — a separate permission system — so this silently returns an
    /// empty map (falling back to showing raw object IDs) whenever Graph access isn't authorized,
    /// rather than failing the whole role list over an optional cosmetic lookup.
    /// </summary>
    private async Task<Dictionary<string, string>> TryResolvePrincipalNamesAsync(
        IEnumerable<string> principalIds, CancellationToken ct)
    {
        var ids = principalIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var credential = _clientFactory.GetCredential();
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), ct);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await http.PostAsJsonAsync(
                "https://graph.microsoft.com/v1.0/directoryObjects/getByIds", new { ids }, ct);

            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, string>();
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var result = new Dictionary<string, string>();
            foreach (var item in json.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var name = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                if (id is not null && name is not null)
                {
                    result[id] = name;
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static string ExtractOidClaim(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var bytes = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));

        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.GetProperty("oid").GetString()
            ?? throw new InvalidOperationException(T("The token does not contain the 'oid' claim."));
    }
}
