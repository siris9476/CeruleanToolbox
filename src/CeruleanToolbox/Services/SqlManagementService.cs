using System.Text.Json;
using Azure;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;

namespace CeruleanToolbox.Services;

public sealed record SqlServerInfo(string Name, string Location, string ResourceGroup, string FullyQualifiedDomainName, string Id);
public sealed record SqlDatabaseInfo(string Name, string SkuName, string SkuTier, string Status, string Id);
public sealed record SqlFirewallRuleInfo(string Name, string StartIPAddress, string EndIPAddress);

public sealed class SqlManagementService
{
    private const string SqlApiVersion = "2021-11-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public SqlManagementService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetServerOverviewAsync(string serverResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(serverResourceId, SqlApiVersion, ct: ct);
        var server = doc.RootElement;
        var p = ArmJson.Obj(server, "properties");

        // Entra admin and "Entra-only auth" are sub-resources; a missing permission must not hide the rest.
        var admins = new List<IReadOnlyList<string>>();
        try
        {
            using var adminDoc = await _arm.GetAsync($"{serverResourceId}/administrators", SqlApiVersion, ct: ct);
            foreach (var admin in ArmJson.Items(adminDoc.RootElement, "value"))
            {
                var ap = ArmJson.Obj(admin, "properties");
                admins.Add(new[] { ArmJson.Str(ap, "login"), ArmJson.Str(ap, "administratorType"), ArmJson.Str(ap, "sid") });
            }
        }
        catch (InvalidOperationException)
        {
        }

        var entraOnly = "-";
        try
        {
            using var onlyDoc = await _arm.GetAsync($"{serverResourceId}/azureADOnlyAuthentications", SqlApiVersion, ct: ct);
            var first = ArmJson.Items(onlyDoc.RootElement, "value").FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                entraOnly = ArmJson.Str(ArmJson.Obj(first, "properties"), "azureADOnlyAuthentication");
            }
        }
        catch (InvalidOperationException)
        {
        }

        var state = ArmJson.Str(p, "state");
        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Status"), state),
                ArmJson.Row("Location", ArmJson.Str(server, "location")),
                ArmJson.Row(T("Server name (FQDN)"), ArmJson.Str(p, "fullyQualifiedDomainName")),
                ArmJson.Row(T("Server version"), ArmJson.Str(p, "version")),
                ArmJson.Row(T("SQL administrator"), ArmJson.Str(p, "administratorLogin")),
                ArmJson.Row(T("Microsoft Entra administrator"), admins.Count > 0 ? admins[0][0] : T("Not set")),
                ArmJson.Row(T("Entra authentication only"), entraOnly),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(server, "identity"), "type")),
            }),
            new(T("Networking and security"), new[]
            {
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess")),
                ArmJson.Row(T("Minimum TLS version"), ArmJson.Str(p, "minimalTlsVersion")),
                ArmJson.Row(T("Restrict outbound access"), ArmJson.Str(p, "restrictOutboundNetworkAccess")),
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
                ArmJson.Str(ArmJson.Obj(cp, "privateLinkServiceConnectionState"), "description"),
            };
        }).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Private endpoints"), new[] { "Endpoint", T("Status"), T("Description") }, endpoints),
            new(T("Microsoft Entra administrators"), new[] { "Login", T("Type"), "SID" }, admins),
        };

        return new ResourceOverview(state, sections, tables, Tags(server));
    }

    public async Task<ResourceOverview> GetDatabaseOverviewAsync(string databaseResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(databaseResourceId, SqlApiVersion, ct: ct);
        var db = doc.RootElement;
        var p = ArmJson.Obj(db, "properties");
        var sku = ArmJson.Obj(db, "sku");

        var maxBytes = double.TryParse(ArmJson.Str(p, "maxSizeBytes"), out var bytes) ? bytes : 0;
        var status = ArmJson.Str(p, "status");
        var capacity = ArmJson.Str(sku, "capacity");

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Status"), status),
                ArmJson.Row("Location", ArmJson.Str(db, "location")),
                ArmJson.Row("Server", ArmJson.LastSegment(DatabaseServerId(databaseResourceId))),
                ArmJson.Row("SKU", ArmJson.Str(sku, "name")),
                ArmJson.Row("Tier", ArmJson.Str(sku, "tier")),
                ArmJson.Row(T("Capacity (DTU or vCore)"), capacity),
                ArmJson.Row(T("Maximum size"), maxBytes > 0 ? $"{maxBytes / 1024 / 1024 / 1024:0.##} GB" : "-"),
                ArmJson.Row("Collation", ArmJson.Str(p, "collation")),
                ArmJson.Row(T("Creation date"), ArmJson.Date(ArmJson.Str(p, "creationDate"))),
                ArmJson.Row(T("Earliest restore point"), ArmJson.Date(ArmJson.Str(p, "earliestRestoreDate"))),
            }),
            new(T("Availability and backup"), new[]
            {
                ArmJson.Row("Zone redundant", ArmJson.Str(p, "zoneRedundant")),
                ArmJson.Row(T("Backup storage redundancy"), ArmJson.Str(p, "currentBackupStorageRedundancy")),
                ArmJson.Row("Read scale", ArmJson.Str(p, "readScale")),
                ArmJson.Row(T("Default secondary location"), ArmJson.Str(p, "defaultSecondaryLocation")),
                ArmJson.Row("Elastic pool", ArmJson.LastSegment(ArmJson.Str(p, "elasticPoolId"))),
                ArmJson.Row(T("License"), ArmJson.Str(p, "licenseType")),
                ArmJson.Row(T("Auto-pause (min, serverless)"), ArmJson.Str(p, "autoPauseDelay")),
                ArmJson.Row(T("Maintenance window"), ArmJson.LastSegment(ArmJson.Str(p, "maintenanceConfigurationId"))),
                ArmJson.Row("Ledger", ArmJson.Str(p, "isLedgerOn")),
                ArmJson.Row(T("Infrastructure encryption"), ArmJson.Str(p, "isInfraEncryptionEnabled")),
            }),
        };

        return new ResourceOverview(status, sections, Array.Empty<InfoTable>(), Tags(db));
    }

    private static string DatabaseServerId(string databaseResourceId)
    {
        var index = databaseResourceId.IndexOf("/databases/", StringComparison.OrdinalIgnoreCase);
        return index > 0 ? databaseResourceId[..index] : "-";
    }

    private static List<KeyValuePair<string, string>> Tags(JsonElement resource) =>
        ArmJson.Obj(resource, "tags") is { ValueKind: JsonValueKind.Object } tags
            ? tags.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

    public async Task<List<SqlServerInfo>> ListServersAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(
            SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<SqlServerInfo>();
        await foreach (SqlServerResource server in subscription.GetSqlServersAsync(cancellationToken: ct))
        {
            result.Add(new SqlServerInfo(
                server.Data.Name,
                server.Data.Location.Name,
                server.Id!.ResourceGroupName ?? "-",
                server.Data.FullyQualifiedDomainName ?? "-",
                server.Id!.ToString()));
        }

        return result;
    }

    public async Task<List<SqlDatabaseInfo>> ListDatabasesAsync(string serverResourceId, CancellationToken ct = default)
    {
        var server = _clientFactory.GetClient().GetSqlServerResource(new Azure.Core.ResourceIdentifier(serverResourceId));

        var result = new List<SqlDatabaseInfo>();
        await foreach (SqlDatabaseResource db in server.GetSqlDatabases().GetAllAsync(cancellationToken: ct))
        {
            // The service-level "master" system database is not a user database, skip it.
            if (string.Equals(db.Data.Name, "master", StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new SqlDatabaseInfo(
                db.Data.Name,
                db.Data.Sku?.Name ?? "-",
                db.Data.Sku?.Tier ?? "-",
                db.Data.Status?.ToString() ?? "Unknown",
                db.Id!.ToString()));
        }

        return result;
    }

    public async Task UpdateDatabaseSkuAsync(string databaseResourceId, string skuName, string skuTier, CancellationToken ct = default)
    {
        var db = _clientFactory.GetClient().GetSqlDatabaseResource(new Azure.Core.ResourceIdentifier(databaseResourceId));
        var patch = new SqlDatabasePatch { Sku = new SqlSku(skuName) { Tier = skuTier } };
        await db.UpdateAsync(WaitUntil.Completed, patch, ct);
    }

    public async Task<List<SqlFirewallRuleInfo>> ListFirewallRulesAsync(string serverResourceId, CancellationToken ct = default)
    {
        var server = _clientFactory.GetClient().GetSqlServerResource(new Azure.Core.ResourceIdentifier(serverResourceId));

        var result = new List<SqlFirewallRuleInfo>();
        await foreach (var rule in server.GetSqlFirewallRules().GetAllAsync(ct))
        {
            result.Add(new SqlFirewallRuleInfo(rule.Data.Name, rule.Data.StartIPAddress, rule.Data.EndIPAddress));
        }

        return result;
    }

    public async Task AddFirewallRuleAsync(
        string serverResourceId, string ruleName, string startIp, string endIp, CancellationToken ct = default)
    {
        var server = _clientFactory.GetClient().GetSqlServerResource(new Azure.Core.ResourceIdentifier(serverResourceId));
        var data = new SqlFirewallRuleData { StartIPAddress = startIp, EndIPAddress = endIp };
        await server.GetSqlFirewallRules().CreateOrUpdateAsync(WaitUntil.Completed, ruleName, data, ct);
    }

    public async Task DeleteFirewallRuleAsync(string serverResourceId, string ruleName, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the three lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteFirewallRuleAsync() called for rule={ruleName} on {serverResourceId} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        var server = _clientFactory.GetClient().GetSqlServerResource(new Azure.Core.ResourceIdentifier(serverResourceId));
        var rule = await server.GetSqlFirewallRules().GetAsync(ruleName, ct);
        await rule.Value.DeleteAsync(WaitUntil.Completed, ct);
    }

    /// <summary>
    /// A standard ADO.NET template. Credentials are left as placeholders on purpose: this app
    /// authenticates to Azure Resource Manager, not to the SQL data plane, so it never holds a
    /// SQL login. Swap in Azure AD auth (Authentication=Active Directory Default) to reuse this
    /// same service principal instead, provided it has been granted access inside the database.
    /// </summary>
    public static string BuildConnectionStringTemplate(string serverFqdn, string databaseName) =>
        $"Server=tcp:{serverFqdn},1433;Initial Catalog={databaseName};User ID=<username>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";
}
