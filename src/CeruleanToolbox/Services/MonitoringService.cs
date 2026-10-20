using System.Text.Json;
using Azure.ResourceManager.ApplicationInsights;
using Azure.ResourceManager.Monitor;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record ActionGroupInfo(string Name, string ResourceGroup, bool IsEnabled, int ReceiverCount, string Id);

public sealed record MetricAlertInfo(
    string Name, string ResourceGroup, bool IsEnabled, int Severity, List<string> Scopes, string Id);

public sealed record DataCollectionRuleInfo(string Name, string Location, string ResourceGroup, string Id);

public sealed record WebTestInfo(string Name, string Location, string ResourceGroup, bool IsEnabled, string? Kind, string Id);

/// <summary>
/// Generic ARM properties view for resource types with no ergonomic typed SDK (workbooks, smart
/// detector alert rules, Security Center automations) — a raw properties readout is a pragmatic
/// stand-in for building a bespoke client against each one.
/// </summary>
public sealed record RawResourceInfo(string Name, string Location, string ResourceGroup, string Id);

public sealed class MonitoringService
{
    private const string ActionGroupApiVersion = "2023-01-01";
    private const string MetricAlertApiVersion = "2018-03-01";
    private const string DcrApiVersion = "2023-03-11";
    private const string WebTestApiVersion = "2022-06-15";
    private const string WorkbookApiVersion = "2023-06-01";
    private const string SmartDetectorApiVersion = "2021-04-01";
    private const string SecurityAutomationApiVersion = "2023-12-01-preview";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public MonitoringService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    private static List<KeyValuePair<string, string>> Tags(JsonElement resource) =>
        ArmJson.Obj(resource, "tags") is { ValueKind: JsonValueKind.Object } tags
            ? tags.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

    private static string Join(IEnumerable<string> values) =>
        string.Join(", ", values.Where(v => v != "-")) is { Length: > 0 } text ? text : "-";

    private static string Names(JsonElement e, string key) =>
        Join(ArmJson.Items(e, key).Select(x => ArmJson.LastSegment(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "-" : ArmJson.Str(x, "id"))));

    // Webhook URLs often embed tokens; only the host is shown.
    private static string HostOnly(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : "-";

    private async Task<JsonDocument?> TryGetAsync(string path, string apiVersion, CancellationToken ct)
    {
        try
        {
            return await _arm.GetAsync(path, apiVersion, ct: ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<ResourceOverview> GetActionGroupOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, ActionGroupApiVersion, ct: ct);
        var group = doc.RootElement;
        var p = ArmJson.Obj(group, "properties");
        var enabled = ArmJson.Str(p, "enabled") == T("Yes");

        var receivers = new List<IReadOnlyList<string>>();
        void Add(string key, string type, Func<JsonElement, string> target, Func<JsonElement, string> status)
        {
            foreach (var r in ArmJson.Items(p, key)) receivers.Add(new[] { ArmJson.Str(r, "name"), type, target(r), status(r) });
        }

        Add("emailReceivers", "Email", r => ArmJson.Str(r, "emailAddress"), r => ArmJson.Str(r, "status"));
        Add("smsReceivers", "SMS", r => $"+{ArmJson.Str(r, "countryCode")} {ArmJson.Str(r, "phoneNumber")}", r => ArmJson.Str(r, "status"));
        Add("voiceReceivers", T("Voice"), r => $"+{ArmJson.Str(r, "countryCode")} {ArmJson.Str(r, "phoneNumber")}", _ => "-");
        Add("webhookReceivers", "Webhook", r => HostOnly(ArmJson.Str(r, "serviceUri")), _ => "-");
        Add("azureAppPushReceivers", T("Azure app push"), r => ArmJson.Str(r, "emailAddress"), _ => "-");
        Add("itsmReceivers", "ITSM", r => ArmJson.Str(r, "workspaceId"), _ => "-");
        Add("armRoleReceivers", T("Azure Resource Manager role"), r => ArmJson.Str(r, "roleId"), _ => "-");
        Add("logicAppReceivers", "Logic App", r => ArmJson.LastSegment(ArmJson.Str(r, "resourceId")), _ => "-");
        Add("azureFunctionReceivers", "Azure Function", r => ArmJson.Str(r, "functionName"), _ => "-");
        Add("automationRunbookReceivers", T("Automation runbook"), r => ArmJson.Str(r, "runbookName"), _ => "-");
        Add("eventHubReceivers", "Event Hub", r => ArmJson.Str(r, "eventHubName"), _ => "-");

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row(T("Enabled"), enabled ? T("Yes") : T("No")),
                ArmJson.Row(T("Short name"), ArmJson.Str(p, "groupShortName")),
                ArmJson.Row("Location", ArmJson.Str(group, "location")),
                ArmJson.Row(T("Receivers"), receivers.Count.ToString()),
            }),
        };

        var tables = new List<InfoTable> { new(T("Receivers"), new[] { T("Name"), T("Type"), T("Destination"), T("Status") }, receivers) };
        return new ResourceOverview(enabled ? T("Enabled") : T("Disabled"), sections, tables, Tags(group));
    }

    public async Task<ResourceOverview> GetMetricAlertOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, MetricAlertApiVersion, ct: ct);
        var alert = doc.RootElement;
        var p = ArmJson.Obj(alert, "properties");
        var enabled = ArmJson.Str(p, "enabled") == T("Yes");
        var criteria = ArmJson.Obj(p, "criteria");

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row(T("Enabled"), enabled ? T("Yes") : T("No")),
                ArmJson.Row(T("Severity"), ArmJson.Str(p, "severity")),
                ArmJson.Row(T("Description"), ArmJson.Str(p, "description")),
                ArmJson.Row(T("Evaluation frequency"), ArmJson.Str(p, "evaluationFrequency")),
                ArmJson.Row(T("Aggregation window"), ArmJson.Str(p, "windowSize")),
                ArmJson.Row(T("Auto-resolve"), ArmJson.Str(p, "autoMitigate")),
                ArmJson.Row(T("Target resource type"), ArmJson.Str(p, "targetResourceType")),
                ArmJson.Row(T("Target region"), ArmJson.Str(p, "targetResourceRegion")),
                ArmJson.Row(T("Last updated"), ArmJson.Date(ArmJson.Str(p, "lastUpdatedTime"))),
                ArmJson.Row(T("Criteria type"), ArmJson.Str(criteria, "odata.type").Split('.').LastOrDefault() ?? "-"),
                ArmJson.Row(T("Availability test"), ArmJson.LastSegment(ArmJson.Str(criteria, "webTestId"))),
                ArmJson.Row(T("Failed locations to trigger the alert"), ArmJson.Str(criteria, "failedLocationCount")),
            }),
        };

        var conditions = ArmJson.Items(criteria, "allOf").Select(c => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(c, "name"), ArmJson.Str(c, "metricNamespace"), ArmJson.Str(c, "metricName"),
            $"{ArmJson.Str(c, "timeAggregation")} {ArmJson.Str(c, "operator")} {ArmJson.Str(c, "threshold")}",
            ArmJson.Str(c, "criterionType"), ArmJson.Items(c, "dimensions").Count().ToString(),
        }).ToList();

        var scopes = ArmJson.Items(p, "scopes").Select(x => (IReadOnlyList<string>)new[] { ArmJson.LastSegment(x.GetString() ?? "-"), x.GetString() ?? "-" }).ToList();
        var actions = ArmJson.Items(p, "actions").Select(a => (IReadOnlyList<string>)new[] { ArmJson.LastSegment(ArmJson.Str(a, "actionGroupId")) }).ToList();

        var tables = new List<InfoTable>
        {
            new(T("Conditions"), new[] { T("Name"), "Namespace", T("Metric"), T("Criteria"), T("Type"), T("Dimensions") }, conditions),
            new("Scope", new[] { T("Resource"), "ID" }, scopes),
            new("Action group", new[] { T("Name") }, actions),
        };

        return new ResourceOverview(enabled ? T("Enabled") : T("Disabled"), sections, tables, Tags(alert));
    }

    public async Task<ResourceOverview> GetDataCollectionRuleOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, DcrApiVersion, ct: ct);
        var rule = doc.RootElement;
        var p = ArmJson.Obj(rule, "properties");
        var sources = ArmJson.Obj(p, "dataSources");
        var destinations = ArmJson.Obj(p, "destinations");
        var provisioning = ArmJson.Str(p, "provisioningState");

        var sourceRows = new List<IReadOnlyList<string>>();
        foreach (var kind in new[] { "performanceCounters", "windowsEventLogs", "syslog", "extensions", "logFiles", "iisLogs", "prometheusForwarder", "platformTelemetry", "dataImports" })
        {
            foreach (var src in ArmJson.Items(sources, kind))
            {
                sourceRows.Add(new[] { ArmJson.Str(src, "name"), kind, Join(ArmJson.Items(src, "streams").Select(x => x.GetString() ?? "-")) });
            }
        }

        var destinationRows = new List<IReadOnlyList<string>>();
        foreach (var kind in new[] { "logAnalytics", "azureMonitorMetrics", "eventHubs", "storageBlobs", "storageAccounts", "monitoringAccounts" })
        {
            var value = ArmJson.Obj(destinations, kind);
            var entries = value.ValueKind == JsonValueKind.Array ? ArmJson.Items(destinations, kind) : (value.ValueKind == JsonValueKind.Object ? new[] { value } : Array.Empty<JsonElement>());
            foreach (var d in entries)
            {
                var target = ArmJson.LastSegment(ArmJson.Str(d, "workspaceResourceId") is var w && w != "-" ? w : ArmJson.Str(d, "eventHubResourceId"));
                destinationRows.Add(new[] { ArmJson.Str(d, "name"), kind, target });
            }
        }

        var flows = ArmJson.Items(p, "dataFlows").Select(f => (IReadOnlyList<string>)new[]
        {
            Join(ArmJson.Items(f, "streams").Select(x => x.GetString() ?? "-")),
            Join(ArmJson.Items(f, "destinations").Select(x => x.GetString() ?? "-")),
            ArmJson.Str(f, "outputStream"), ArmJson.Str(f, "transformKql") == "-" ? T("No") : T("Yes"),
        }).ToList();

        // Associations point from monitored resources (VMs, clusters) to the rule.
        var associations = new List<IReadOnlyList<string>>();
        using var assocDoc = await TryGetAsync($"{resourceId}/associations", "2022-06-01", ct);
        if (assocDoc is not null)
        {
            foreach (var a in ArmJson.Items(assocDoc.RootElement, "value"))
            {
                var id = ArmJson.Str(a, "id");
                var marker = id.IndexOf("/providers/Microsoft.Insights/dataCollectionRuleAssociations", StringComparison.OrdinalIgnoreCase);
                var target = marker > 0 ? id[..marker] : id;
                associations.Add(new[] { ArmJson.LastSegment(target), ArmJson.Str(a, "name") });
            }
        }

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row("Provisioning", provisioning),
                ArmJson.Row(T("Type"), ArmJson.Str(rule, "kind")),
                ArmJson.Row("Location", ArmJson.Str(rule, "location")),
                ArmJson.Row(T("Description"), ArmJson.Str(p, "description")),
                ArmJson.Row("Data collection endpoint", ArmJson.LastSegment(ArmJson.Str(p, "dataCollectionEndpointId"))),
                ArmJson.Row("Immutable ID", ArmJson.Str(p, "immutableId")),
                ArmJson.Row(T("Associated resources"), associations.Count.ToString()),
            }),
        };

        var tables = new List<InfoTable>
        {
            new(T("Data sources"), new[] { T("Name"), T("Type"), "Stream" }, sourceRows),
            new(T("Destinations"), new[] { T("Name"), T("Type"), T("Resource") }, destinationRows),
            new(T("Data flows"), new[] { "Stream", T("Destinations"), T("Output stream"), T("KQL transformation") }, flows),
            new(T("Associated resources"), new[] { T("Resource"), T("Association") }, associations),
        };

        return new ResourceOverview(provisioning, sections, tables, Tags(rule));
    }

    public async Task<ResourceOverview> GetWebTestOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, WebTestApiVersion, ct: ct);
        var test = doc.RootElement;
        var p = ArmJson.Obj(test, "properties");
        var enabled = ArmJson.Str(p, "Enabled") == T("Yes");
        var request = ArmJson.Obj(p, "Request");
        var rules = ArmJson.Obj(p, "ValidationRules");

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row(T("Enabled"), enabled ? T("Yes") : T("No")),
                ArmJson.Row(T("Type"), ArmJson.Str(p, "Kind") != "-" ? ArmJson.Str(p, "Kind") : ArmJson.Str(test, "kind")),
                ArmJson.Row(T("Test name"), ArmJson.Str(p, "Name")),
                ArmJson.Row(T("Description"), ArmJson.Str(p, "Description")),
                ArmJson.Row(T("Frequency (seconds)"), ArmJson.Str(p, "Frequency")),
                ArmJson.Row(T("Timeout (seconds)"), ArmJson.Str(p, "Timeout")),
                ArmJson.Row(T("Retry on failure"), ArmJson.Str(p, "RetryEnabled")),
                ArmJson.Row("Location", ArmJson.Str(test, "location")),
            }),
            new(T("Request and validation"), new[]
            {
                ArmJson.Row("URL", ArmJson.Str(request, "RequestUrl")),
                ArmJson.Row(T("HTTP method"), ArmJson.Str(request, "HttpVerb")),
                ArmJson.Row(T("Follow redirects"), ArmJson.Str(request, "FollowRedirects")),
                ArmJson.Row(T("Expected HTTP status code"), ArmJson.Str(rules, "ExpectedHttpStatusCode")),
                ArmJson.Row(T("SSL check"), ArmJson.Str(rules, "SSLCheck")),
                ArmJson.Row(T("Minimum certificate validity (days)"), ArmJson.Str(rules, "SSLCertRemainingLifetimeCheck")),
            }),
        };

        var locations = ArmJson.Items(p, "Locations")
            .Select(l => (IReadOnlyList<string>)new[] { ArmJson.Str(l, "Id") }).ToList();

        var tables = new List<InfoTable> { new(T("Test locations"), new[] { "Location" }, locations) };
        return new ResourceOverview(enabled ? T("Enabled") : T("Disabled"), sections, tables, Tags(test));
    }

    public async Task<ResourceOverview> GetWorkbookOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, WorkbookApiVersion, ct: ct);
        var workbook = doc.RootElement;
        var p = ArmJson.Obj(workbook, "properties");

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row(T("Display name"), ArmJson.Str(p, "displayName")),
                ArmJson.Row(T("Category"), ArmJson.Str(p, "category")),
                ArmJson.Row(T("Type"), ArmJson.Str(workbook, "kind")),
                ArmJson.Row("Location", ArmJson.Str(workbook, "location")),
                ArmJson.Row(T("Linked to"), ArmJson.LastSegment(ArmJson.Str(p, "sourceId"))),
                ArmJson.Row(T("Version"), ArmJson.Str(p, "version")),
                ArmJson.Row(T("Last modified"), ArmJson.Date(ArmJson.Str(p, "timeModified"))),
            }),
        };

        return new ResourceOverview(T("Present"), sections, Array.Empty<InfoTable>(), Tags(workbook));
    }

    public async Task<ResourceOverview> GetSmartDetectorOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, SmartDetectorApiVersion, ct: ct);
        var rule = doc.RootElement;
        var p = ArmJson.Obj(rule, "properties");
        var state = ArmJson.Str(p, "state");

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row(T("Status"), state),
                ArmJson.Row(T("Severity"), ArmJson.Str(p, "severity")),
                ArmJson.Row(T("Frequency"), ArmJson.Str(p, "frequency")),
                ArmJson.Row("Detector", ArmJson.Str(ArmJson.Obj(p, "detector"), "id")),
                ArmJson.Row(T("Description"), ArmJson.Str(p, "description")),
                ArmJson.Row("Scope", ArmJson.Obj(p, "scope") is { ValueKind: JsonValueKind.Array } scopeList
                    ? Join(scopeList.EnumerateArray().Select(x => ArmJson.LastSegment(x.GetString() ?? "-")))
                    : Names(ArmJson.Obj(p, "scope"), "resourceIds")),
                ArmJson.Row("Action group", Names(ArmJson.Obj(p, "actionGroups"), "groupIds")),
            }),
        };

        return new ResourceOverview(state, sections, Array.Empty<InfoTable>(), Tags(rule));
    }

    public async Task<ResourceOverview> GetSecurityAutomationOverviewAsync(string resourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(resourceId, SecurityAutomationApiVersion, ct: ct);
        var automation = doc.RootElement;
        var p = ArmJson.Obj(automation, "properties");
        var enabled = ArmJson.Str(p, "isEnabled") == T("Yes");

        var sections = new List<InfoSection>
        {
            new(T("Basics"), new[]
            {
                ArmJson.Row(T("Enabled (automation)"), enabled ? T("Yes") : T("No")),
                ArmJson.Row(T("Description"), ArmJson.Str(p, "description")),
                ArmJson.Row("Location", ArmJson.Str(automation, "location")),
                ArmJson.Row(T("Sources"), ArmJson.Items(p, "sources").Count().ToString()),
                ArmJson.Row(T("Actions"), ArmJson.Items(p, "actions").Count().ToString()),
            }),
        };

        var scopes = ArmJson.Items(p, "scopes").Select(x => (IReadOnlyList<string>)new[] { ArmJson.Str(x, "description"), ArmJson.Str(x, "scopePath") }).ToList();
        var sources = ArmJson.Items(p, "sources").Select(x => (IReadOnlyList<string>)new[] { ArmJson.Str(x, "eventSource"), ArmJson.Items(x, "ruleSets").Count().ToString() }).ToList();
        var actions = ArmJson.Items(p, "actions").Select(x => (IReadOnlyList<string>)new[] { ArmJson.Str(x, "actionType"), ArmJson.LastSegment(ArmJson.Str(x, "logicAppResourceId") != "-" ? ArmJson.Str(x, "logicAppResourceId") : ArmJson.Str(x, "eventHubResourceId")) }).ToList();

        var tables = new List<InfoTable>
        {
            new("Scope", new[] { T("Description"), T("Path") }, scopes),
            new(T("Sources"), new[] { T("Event source"), T("Rule sets") }, sources),
            new(T("Actions"), new[] { T("Type"), T("Destination") }, actions),
        };

        return new ResourceOverview(enabled ? T("Automation enabled") : T("Automation disabled"), sections, tables, Tags(automation));
    }


    private SubscriptionResource GetSubscription(string subscriptionId) =>
        _clientFactory.GetClient().GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

    public async Task<List<ActionGroupInfo>> ListActionGroupsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<ActionGroupInfo>();
        await foreach (ActionGroupResource ag in GetSubscription(subscriptionId).GetActionGroupsAsync(ct))
        {
            var receiverCount = ag.Data.EmailReceivers.Count + ag.Data.SmsReceivers.Count + ag.Data.WebhookReceivers.Count
                + ag.Data.ItsmReceivers.Count + ag.Data.AzureAppPushReceivers.Count + ag.Data.AutomationRunbookReceivers.Count
                + ag.Data.VoiceReceivers.Count + ag.Data.LogicAppReceivers.Count + ag.Data.AzureFunctionReceivers.Count
                + ag.Data.ArmRoleReceivers.Count + ag.Data.EventHubReceivers.Count;

            result.Add(new ActionGroupInfo(
                ag.Data.Name, ag.Id.ResourceGroupName ?? "-", ag.Data.IsEnabled ?? false, receiverCount, ag.Id.ToString()));
        }

        return result;
    }

    public async Task<List<MetricAlertInfo>> ListMetricAlertsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<MetricAlertInfo>();
        await foreach (MetricAlertResource alert in GetSubscription(subscriptionId).GetMetricAlertsAsync(ct))
        {
            result.Add(new MetricAlertInfo(
                alert.Data.Name,
                alert.Id.ResourceGroupName ?? "-",
                alert.Data.IsEnabled,
                alert.Data.Severity,
                alert.Data.Scopes.Select(s => s.ToString()).ToList(),
                alert.Id.ToString()));
        }

        return result;
    }

    public async Task<List<DataCollectionRuleInfo>> ListDataCollectionRulesAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<DataCollectionRuleInfo>();
        await foreach (DataCollectionRuleResource dcr in GetSubscription(subscriptionId).GetDataCollectionRulesAsync(ct))
        {
            result.Add(new DataCollectionRuleInfo(
                dcr.Data.Name, dcr.Data.Location.Name, dcr.Id.ResourceGroupName ?? "-", dcr.Id.ToString()));
        }

        return result;
    }

    public async Task<List<WebTestInfo>> ListWebTestsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var result = new List<WebTestInfo>();
        await foreach (ApplicationInsightsWebTestResource test in GetSubscription(subscriptionId).GetApplicationInsightsWebTestsAsync(ct))
        {
            result.Add(new WebTestInfo(
                test.Data.Name,
                test.Data.Location.Name,
                test.Id.ResourceGroupName ?? "-",
                string.Equals(test.Data.IsEnabled?.ToString(), "true", StringComparison.OrdinalIgnoreCase),
                test.Data.WebTestKind?.ToString(),
                test.Id.ToString()));
        }

        return result;
    }

    public Task<List<RawResourceInfo>> ListWorkbooksAsync(string subscriptionId, CancellationToken ct = default) =>
        ListGenericAsync(subscriptionId, "Microsoft.Insights/workbooks", ct);

    public Task<List<RawResourceInfo>> ListSmartDetectorAlertRulesAsync(string subscriptionId, CancellationToken ct = default) =>
        ListGenericAsync(subscriptionId, "microsoft.alertsmanagement/smartDetectorAlertRules", ct);

    public Task<List<RawResourceInfo>> ListSecurityAutomationsAsync(string subscriptionId, CancellationToken ct = default) =>
        ListGenericAsync(subscriptionId, "Microsoft.Security/automations", ct);

    private async Task<List<RawResourceInfo>> ListGenericAsync(string subscriptionId, string resourceType, CancellationToken ct)
    {
        var result = new List<RawResourceInfo>();
        var filter = $"resourceType eq '{resourceType}'";
        await foreach (var resource in GetSubscription(subscriptionId).GetGenericResourcesAsync(filter, expand: null, top: null, ct))
        {
            result.Add(new RawResourceInfo(
                resource.Data.Name, resource.Data.Location.Name, resource.Id.ResourceGroupName ?? "-", resource.Id.ToString()));
        }

        return result;
    }
}
