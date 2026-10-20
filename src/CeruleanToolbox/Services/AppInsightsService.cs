using System.Text.Json;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager.ApplicationInsights;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record AppInsightsComponentInfo(
    string Name, string Location, string ResourceGroup, int? RetentionInDays,
    string? ConnectionString, string? InstrumentationKey, string Id);

public sealed record AppInsightsQueryResult(List<string> Columns, List<List<string>> Rows, int TotalRowCount, bool Truncated);

public sealed class AppInsightsService
{
    // A query without its own "take" can return thousands of rows; rendering all of them as one
    // HTML table froze the browser tab entirely (Chrome's "Page Unresponsive" prompt), so results
    // are capped here regardless of what the query itself returned.
    private const int MaxRenderedRows = 200;

    private const string ComponentApiVersion = "2020-02-02";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public AppInsightsService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    public async Task<ResourceOverview> GetOverviewAsync(string componentResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(componentResourceId, ComponentApiVersion, ct: ct);
        var component = doc.RootElement;
        var p = ArmJson.Obj(component, "properties");
        var provisioning = ArmJson.Str(p, "provisioningState");

        string Access(string key) => ArmJson.Str(p, key) switch { "-" => T("Enabled (default)"), var v => v };

        var sampling = ArmJson.Str(p, "SamplingPercentage");
        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row("Location", ArmJson.Str(component, "location")),
                ArmJson.Row(T("Application type"), ArmJson.Str(p, "Application_Type")),
                ArmJson.Row(T("Type"), ArmJson.Str(component, "kind")),
                ArmJson.Row("Application ID", ArmJson.Str(p, "AppId")),
                ArmJson.Row(T("Retention (days)"), ArmJson.Str(p, "RetentionInDays")),
                ArmJson.Row(T("Ingestion mode"), ArmJson.Str(p, "IngestionMode")),
                ArmJson.Row(T("Creation date"), ArmJson.Date(ArmJson.Str(p, "CreationDate"))),
                ArmJson.Row(T("Request source"), ArmJson.Str(p, "Request_Source")),
            }),
            new(T("Linked workspace"), new[]
            {
                ArmJson.Row("Workspace Log Analytics", ArmJson.LastSegment(ArmJson.Str(p, "WorkspaceResourceId"))),
                ArmJson.Row(T("Workspace resource group"), ArmJson.Str(p, "WorkspaceResourceId") is var w && w != "-" ? new Azure.Core.ResourceIdentifier(w).ResourceGroupName ?? "-" : "-"),
            }),
            new(T("Security and network"), new[]
            {
                ArmJson.Row(T("Ingestion from public network"), Access("publicNetworkAccessForIngestion")),
                ArmJson.Row(T("Query from public network"), Access("publicNetworkAccessForQuery")),
                ArmJson.Row(T("Local (key) authentication disabled"), ArmJson.Str(p, "DisableLocalAuth")),
                ArmJson.Row(T("IP address masking disabled"), ArmJson.Str(p, "DisableIpMasking")),
                ArmJson.Row(T("Sampling (%)"), sampling),
                ArmJson.Row("Tenant ID", ArmJson.Str(p, "TenantId")),
            }),
        };

        var tags = ArmJson.Obj(component, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(provisioning, sections, Array.Empty<InfoTable>(), tags);
    }

    public async Task<List<AppInsightsComponentInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<AppInsightsComponentInfo>();
        await foreach (ApplicationInsightsComponentResource component in subscription.GetApplicationInsightsComponentsAsync(ct))
        {
            result.Add(new AppInsightsComponentInfo(
                component.Data.Name,
                component.Data.Location.Name,
                component.Id!.ResourceGroupName ?? "-",
                component.Data.RetentionInDays,
                component.Data.ConnectionString,
                component.Data.InstrumentationKey?.ToString(),
                component.Id!.ToString()));
        }

        return result;
    }

    public async Task<AppInsightsQueryResult> RunQueryAsync(
        string componentResourceId, string kqlQuery, TimeSpan lookback, CancellationToken ct = default)
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
                new Azure.Core.ResourceIdentifier(componentResourceId),
                kqlQuery,
                new QueryTimeRange(lookback),
                cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(T(
                "The query did not respond within 60 seconds. This is typical of a network block towards the " +
                "Azure Monitor endpoints (packets dropped rather than rejected) — check the outbound firewall/" +
                "proxy from this machine to *.applicationinsights.azure.com and *.ods.opinsights.azure.com."));
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

        return new AppInsightsQueryResult(columns, rows, totalRowCount, totalRowCount > MaxRenderedRows);
    }
}
