using System.Text.Json;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record AutomationAccountInfo(string Name, string Location, string ResourceGroup, string Id);

public sealed record AutomationRunbookInfo(string Name, string RunbookType, string State, int? JobCount, string Id);

public sealed record AutomationJobInfo(
    string JobId, string RunbookName, string Status,
    DateTimeOffset? CreatedOn, DateTimeOffset? StartOn, DateTimeOffset? EndOn);

public sealed class AutomationService
{
    private const string AutomationApiVersion = "2023-11-01";

    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public AutomationService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    private async Task<JsonDocument?> TryGetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _arm.GetAsync(path, AutomationApiVersion, ct: ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<ResourceOverview> GetOverviewAsync(string accountResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(accountResourceId, AutomationApiVersion, ct: ct);
        var account = doc.RootElement;
        var p = ArmJson.Obj(account, "properties");
        var state = ArmJson.Str(p, "state");

        // Child resources are separate calls; a missing permission on one must not hide the rest.
        async Task<List<JsonElement>> ListAsync(string segment)
        {
            var items = new List<JsonElement>();
            using var childDoc = await TryGetAsync($"{accountResourceId}/{segment}", ct);
            if (childDoc is not null)
            {
                items.AddRange(ArmJson.Items(childDoc.RootElement, "value").Select(x => x.Clone()));
            }

            return items;
        }

        var schedules = await ListAsync("schedules");
        var variables = await ListAsync("variables");
        var modules = await ListAsync("modules");
        var webhooks = await ListAsync("webhooks");
        var credentials = await ListAsync("credentials");
        var certificates = await ListAsync("certificates");
        var connections = await ListAsync("connections");
        var workerGroups = await ListAsync("hybridRunbookWorkerGroups");

        string Prop(JsonElement e, string key) => ArmJson.Str(ArmJson.Obj(e, "properties"), key);

        var sections = new List<InfoSection>
        {
            new(T("Basic information"), new[]
            {
                ArmJson.Row(T("Status"), state),
                ArmJson.Row("Location", ArmJson.Str(account, "location")),
                ArmJson.Row("SKU", ArmJson.Str(ArmJson.Obj(p, "sku"), "name")),
                ArmJson.Row(T("Creation date"), ArmJson.Date(ArmJson.Str(p, "creationTime"))),
                ArmJson.Row(T("Last modified"), ArmJson.Date(ArmJson.Str(p, "lastModifiedTime"))),
                ArmJson.Row(T("Managed identity"), ArmJson.Str(ArmJson.Obj(account, "identity"), "type")),
            }),
            new(T("Security and networking"), new[]
            {
                ArmJson.Row(T("Public network access"), ArmJson.Str(p, "publicNetworkAccess") switch
                {
                    var yes when yes == T("Yes") => T("Enabled"),
                    var no when no == T("No") => T("Disabled"),
                    "-" => T("Enabled (default)"),
                    var other => other,
                }),
                ArmJson.Row(T("Local authentication disabled"), ArmJson.Str(p, "disableLocalAuth")),
                ArmJson.Row(T("Encryption (key source)"), ArmJson.Str(ArmJson.Obj(p, "encryption"), "keySource")),
                ArmJson.Row(T("Private endpoint connections"), ArmJson.Items(p, "privateEndpointConnections").Count().ToString()),
            }),
            new(T("Account resources"), new[]
            {
                ArmJson.Row("Schedule", schedules.Count.ToString()),
                ArmJson.Row(T("Variables"), variables.Count.ToString()),
                ArmJson.Row(T("Modules"), modules.Count.ToString()),
                ArmJson.Row("Webhook", webhooks.Count.ToString()),
                ArmJson.Row(T("Credentials"), credentials.Count.ToString()),
                ArmJson.Row(T("Certificates"), certificates.Count.ToString()),
                ArmJson.Row(T("Connections"), connections.Count.ToString()),
                ArmJson.Row(T("Hybrid worker groups"), workerGroups.Count.ToString()),
            }),
        };

        var scheduleRows = schedules.Select(x => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(x, "name"), Prop(x, "frequency"), Prop(x, "interval"), ArmJson.Date(Prop(x, "nextRun")),
            Prop(x, "isEnabled"), Prop(x, "timeZone"),
        }).ToList();

        // Variable values are never read: only the name, type and whether they are encrypted.
        var variableRows = variables.Select(x => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(x, "name"), Prop(x, "isEncrypted"), ArmJson.Date(Prop(x, "lastModifiedTime")),
        }).ToList();

        var webhookRows = webhooks.Select(x => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(x, "name"), Prop(x, "isEnabled"), ArmJson.Date(Prop(x, "expiryTime")), ArmJson.LastSegment(Prop(x, "runbook") != "-" ? Prop(x, "runbook") : ArmJson.Str(ArmJson.Obj(ArmJson.Obj(x, "properties"), "runbook"), "name")),
        }).ToList();

        var moduleRows = modules.Select(x => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(x, "name"), Prop(x, "version"), Prop(x, "provisioningState"), Prop(x, "isGlobal"),
        }).Where(r => r[3] != T("Yes")).ToList();

        var workerRows = workerGroups.Select(x => (IReadOnlyList<string>)new[]
        {
            ArmJson.Str(x, "name"), Prop(x, "groupType"),
        }).ToList();

        var tables = new List<InfoTable>
        {
            new("Schedule", new[] { T("Name"), T("Frequency"), T("Interval"), T("Next run"), T("Is enabled"), T("Time zone") }, scheduleRows),
            new(T("Variables (names only)"), new[] { T("Name"), T("Encrypted"), T("Last modified") }, variableRows),
            new(T("Webhooks (without URL)"), new[] { T("Name"), T("Enabled"), T("Expiry"), "Runbook" }, webhookRows),
            new(T("Imported modules (global ones excluded)"), new[] { T("Name"), T("Version"), "Provisioning", T("Global") }, moduleRows),
            new(T("Hybrid worker groups"), new[] { T("Name"), T("Type") }, workerRows),
        };

        var tags = ArmJson.Obj(account, "tags") is { ValueKind: JsonValueKind.Object } tagObject
            ? tagObject.EnumerateObject().Select(t => ArmJson.Row(t.Name, t.Value.ToString())).ToList()
            : new List<KeyValuePair<string, string>>();

        return new ResourceOverview(state, sections, tables, tags);
    }

    public async Task<List<AutomationAccountInfo>> ListAllAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        var result = new List<AutomationAccountInfo>();
        await foreach (AutomationAccountResource account in subscription.GetAutomationAccountsAsync(ct))
        {
            result.Add(new AutomationAccountInfo(
                account.Data.Name,
                account.Data.Location.Name,
                account.Id.ResourceGroupName ?? "-",
                account.Id.ToString()));
        }

        return result;
    }

    public async Task<List<AutomationRunbookInfo>> ListRunbooksAsync(string accountResourceId, CancellationToken ct = default)
    {
        var account = _clientFactory.GetClient().GetAutomationAccountResource(new Azure.Core.ResourceIdentifier(accountResourceId));

        var result = new List<AutomationRunbookInfo>();
        await foreach (var runbook in account.GetAutomationRunbooks().GetAllAsync(ct))
        {
            result.Add(new AutomationRunbookInfo(
                runbook.Data.Name,
                runbook.Data.RunbookType?.ToString() ?? "-",
                runbook.Data.State?.ToString() ?? "-",
                runbook.Data.JobCount,
                runbook.Id.ToString()));
        }

        return result;
    }

    public async Task<List<AutomationJobInfo>> ListRecentJobsAsync(string accountResourceId, CancellationToken ct = default)
    {
        var account = _clientFactory.GetClient().GetAutomationAccountResource(new Azure.Core.ResourceIdentifier(accountResourceId));

        var result = new List<AutomationJobInfo>();
        await foreach (var job in account.GetAutomationJobs().GetAllAsync(filter: null, clientRequestId: null, ct))
        {
            result.Add(new AutomationJobInfo(
                job.JobId?.ToString() ?? "-",
                job.RunbookName ?? "-",
                job.Status?.ToString() ?? "-",
                job.CreatedOn,
                job.StartOn,
                job.EndOn));
        }

        return result.OrderByDescending(j => j.CreatedOn).Take(50).ToList();
    }

    /// <summary>
    /// Starts a runbook job. Parameters are passed as a flat key/value dictionary — the same shape
    /// the runbook's own "Parameters" declares, sent as strings regardless of the runbook's PowerShell type.
    /// </summary>
    public async Task StartRunbookAsync(
        string accountResourceId, string runbookName, Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        var account = _clientFactory.GetClient().GetAutomationAccountResource(new Azure.Core.ResourceIdentifier(accountResourceId));

        var content = new AutomationJobCreateOrUpdateContent { RunbookName = runbookName };
        foreach (var (key, value) in parameters)
        {
            content.Parameters[key] = value;
        }

        var jobName = Guid.NewGuid().ToString();
        await account.GetAutomationJobs().CreateOrUpdateAsync(Azure.WaitUntil.Completed, jobName, content, clientRequestId: null, ct);
    }
}
