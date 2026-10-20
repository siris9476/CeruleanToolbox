using System.Text.Json;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager.OperationalInsights;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record LogAnalyticsSolutionInfo(string Name, string Location, string ResourceGroup, string Id);

public sealed record LogAnalyticsWorkspaceInfo(
    string Name, string Location, string ResourceGroup, int? RetentionInDays,
    string? WorkspaceId, string Sku, string Id);

public sealed record LogAnalyticsQueryResult(List<string> Columns, List<List<string>> Rows, int TotalRowCount, bool Truncated);

public sealed class LogAnalyticsService
{
    // Same cap used for App Insights: an unbounded result set renders into an HTML table large
    // enough to freeze the browser tab, so results are capped here regardless of the query itself.
    private const int MaxRenderedRows = 200;

    private const string WorkspaceApiVersion = "2023-09-01";
    private const string TableApiVersion = "2022-10-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public LogAnalyticsService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetOverviewAsync(string workspaceResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(workspaceResourceId, WorkspaceApiVersion, ct: ct);
        var workspace = doc.RootElement;
        var p = ArmJson.Obj(workspace, "properties");
        var sku = ArmJson.Obj(p, "sku");
        var features = ArmJson.Obj(p, "features");
        var capping = ArmJson.Obj(p, "workspaceCapping");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var dailyCap = ArmJson.Str(capping, "dailyQuotaGb");
        var dailyCapText = !double.TryParse(dailyCap, System.Globalization.CultureInfo.InvariantCulture, out var capGb) || capGb <= 0
            ? T("No limit")
            : T("{0} GB per day", dailyCap);
        var reservation = ArmJson.Str(sku, "capacityReservationLevel");

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(workspace, "location")),
                ArmJson.Row("Workspace ID", ArmJson.Str(p, "customerId")),
                ArmJson.Row(T("Pricing tier"), reservation != "-" ? T("{0} (reservation {1} GB/day)", ArmJson.Str(sku, "name"), reservation) : ArmJson.Str(sku, "name")),
                ArmJson.Row(T("Default retention (days)"), ArmJson.Str(p, "retentionInDays")),
                ArmJson.Row(T("Creation date"), ArmJson.Date(ArmJson.Str(p, "createdDate"))),
                ArmJson.Row(T("Last modified"), ArmJson.Date(ArmJson.Str(p, "modifiedDate"))),
            }),
            new(T("Network and access"), new[]
            {
                ArmJson.Row(T("Ingestion from public network"), ArmJson.Str(p, "publicNetworkAccessForIngestion")),
                ArmJson.Row(T("Query from public network"), ArmJson.Str(p, "publicNetworkAccessForQuery")),
                ArmJson.Row(T("Log access using only resource permissions"), ArmJson.Str(features, "enableLogAccessUsingOnlyResourcePermissions")),
                ArmJson.Row(T("Local authentication disabled"), ArmJson.Str(features, "disableLocalAuth")),
                ArmJson.Row(T("Linked Private Link scopes"), ArmJson.Items(p, "privateLinkScopedResources").Count().ToString()),
            }),
            new(T("Limits and collection"), new[]
            {
                ArmJson.Row(T("Daily ingestion cap"), dailyCapText),
                ArmJson.Row(T("Ingestion status"), ArmJson.Str(capping, "dataIngestionStatus")),
                ArmJson.Row(T("Next quota reset"), ArmJson.Date(ArmJson.Str(capping, "quotaNextResetTime"))),
                ArmJson.Row(T("Data purge within 30 days"), ArmJson.Str(features, "immediatePurgeDataOn30Days")),
                ArmJson.Row(T("Customer-managed key for queries"), ArmJson.Str(p, "forceCmkForQuery")),
                ArmJson.Row(T("Default data collection rule"), ArmJson.LastSegment(ArmJson.Str(p, "defaultDataCollectionRuleResourceId"))),
            }),
        };

        // The table list is a separate (and long) call; a missing permission must not hide the rest.
        var tables = new List<IReadOnlyList<string>>();
        try
        {
            using var tableDoc = await _arm.GetAsync($"{workspaceResourceId}/tables", TableApiVersion, ct: ct);
            // Custom and search-result tables first: the ~700 built-in "Microsoft" tables would bury them.
            foreach (var t in ArmJson.Items(tableDoc.RootElement, "value")
                         .OrderBy(x => ArmJson.Str(ArmJson.Obj(ArmJson.Obj(x, "properties"), "schema"), "tableType") == "Microsoft" ? 1 : 0)
                         .ThenBy(x => ArmJson.Str(x, "name"), StringComparer.OrdinalIgnoreCase))
            {
                var tp = ArmJson.Obj(t, "properties");
                tables.Add(new[]
                {
                    ArmJson.Str(t, "name"), ArmJson.Str(ArmJson.Obj(tp, "schema"), "tableType"), ArmJson.Str(tp, "plan"),
                    ArmJson.Str(tp, "retentionInDays"), ArmJson.Str(tp, "totalRetentionInDays"),
                });
            }
        }
        catch (InvalidOperationException)
        {
        }

        var shown = tables.Take(150).ToList();
        var tableTitle = tables.Count > shown.Count ? T("Tables (first {0} of {1})", shown.Count, tables.Count) : T("Tables");
        var infoTables = new List<InfoTable>
        {
            new(tableTitle, new[] { T("Name"), T("Type"), T("Plan"), T("Interactive retention (days)"), T("Total retention (days)") }, shown),
        };

        var tags = ArmJson.Obj(workspace, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(provisioning, sections, infoTables, tags);
    }

    public async Task<List<LogAnalyticsWorkspaceInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<LogAnalyticsWorkspaceInfo>();
        await foreach (OperationalInsightsWorkspaceResource workspace in subscription.GetOperationalInsightsWorkspacesAsync(cancellationToken: ct))
        {
            result.Add(new LogAnalyticsWorkspaceInfo(
                workspace.Data.Name,
                workspace.Data.Location.Name,
                workspace.Id!.ResourceGroupName ?? "-",
                workspace.Data.RetentionInDays,
                workspace.Data.CustomerId?.ToString(),
                workspace.Data.Sku?.Name.ToString() ?? "-",
                workspace.Id!.ToString()));
        }

        return result;
    }

    public async Task<LogAnalyticsQueryResult> RunQueryAsync(
        string workspaceResourceId, string kqlQuery, TimeSpan lookback, CancellationToken ct = default)
    {
        // A network block on the Azure Monitor Query endpoint tends to hang silently (dropped
        // packets, not a rejection) rather than fail fast, so this call gets its own timeout
        // instead of waiting on the caller forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        Azure.Response<LogsQueryResult> response;
        try
        {
            var logsClient = new LogsQueryClient(_clientFactory.GetCredential());
            response = await logsClient.QueryResourceAsync(
                new Azure.Core.ResourceIdentifier(workspaceResourceId),
                kqlQuery,
                new QueryTimeRange(lookback),
                cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(T(
                "The query did not respond within 60 seconds. This is typical of a network block towards the " +
                "Azure Monitor endpoints (packets dropped rather than rejected) — check the outbound firewall/" +
                "proxy from this machine to *.ods.opinsights.azure.com and *.oms.opinsights.azure.com."));
        }

        var table = response.Value.Table;
        var columns = table.Columns.Select(c => c.Name).ToList();

        var totalRowCount = table.Rows.Count;
        var rows = new List<List<string>>();
        foreach (var row in table.Rows.Take(MaxRenderedRows))
        {
            var values = new List<string>();
            for (var i = 0; i < columns.Count; i++)
            {
                values.Add(row[i]?.ToString() ?? "");
            }

            rows.Add(values);
        }

        return new LogAnalyticsQueryResult(columns, rows, totalRowCount, totalRowCount > MaxRenderedRows);
    }

    /// <summary>
    /// "Solutions" (Microsoft.OperationsManagement/solutions) are the legacy per-workspace add-ons
    /// (Update Management, Security & Audit, etc.) predating Azure Monitor's unified experience —
    /// no ergonomic typed SDK exists for them, so a generic ARM properties readout stands in.
    /// </summary>
    public async Task<List<LogAnalyticsSolutionInfo>> ListSolutionsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var subscription = _clientFactory.GetClient().GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<LogAnalyticsSolutionInfo>();
        var filter = "resourceType eq 'Microsoft.OperationsManagement/solutions'";
        await foreach (var resource in subscription.GetGenericResourcesAsync(filter, expand: null, top: null, ct))
        {
            result.Add(new LogAnalyticsSolutionInfo(
                resource.Data.Name, resource.Data.Location.Name, resource.Id.ResourceGroupName ?? "-", resource.Id.ToString()));
        }

        return result;
    }
}
