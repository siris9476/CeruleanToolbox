using Azure.Monitor.Query;
using Azure.ResourceManager.ApplicationInsights;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;

namespace CeruleanToolbox.Services;

public sealed record FunctionAppInfo(string Name, string Location, string State, string ResourceGroup, string Id);
public sealed record FunctionInfo(string Name, bool IsDisabled, string? Language);
public sealed record FunctionInvocationLog(DateTimeOffset Timestamp, string OperationName, bool Success, string ResultCode, double DurationMs);
public sealed record KeyVaultReferenceInfo(
    string Setting, string Vault, string Secret, string Version, string Identity, string Status, string Details);
public sealed record FunctionInvocationQuery(int Hours, int MaxRows, bool OnlyFailures);
public sealed record FunctionFailureSummary(string Function, long Total, long Failures, DateTimeOffset? LastFailure, string? LastErrorType, string? LastError);

public sealed record FunctionInvocationReport(
    bool HasAppInsights, IReadOnlyList<FunctionFailureSummary> Summary, IReadOnlyList<FunctionInvocationLog> Invocations)
{
    public static FunctionInvocationReport NoAppInsights { get; } = new(false, [], []);
}

public sealed class FunctionAppService
{
    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public FunctionAppService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    /// <summary>
    /// The app settings of the function app that are Key Vault references (<c>@Microsoft.KeyVault(...)</c>) and whether the platform
    /// could resolve each one right now. Read-only. A reference without a version follows the secret's newest version, but the app
    /// only re-reads it periodically (about once a day) or when it restarts.
    /// </summary>
    /// <summary>
    /// Forces the app to resolve all its Key Vault references again right now (documented App Service call
    /// <c>POST .../config/configreferences/appsettings/refresh</c>), without restarting the app. Useful after a secret was rotated:
    /// without it the app keeps the version it cached for up to 24 hours.
    /// </summary>
    public Task RefreshKeyVaultReferencesAsync(string functionAppResourceId, CancellationToken ct = default) =>
        _arm.PostAsync(functionAppResourceId + "/config/configreferences/appsettings/refresh", "2022-03-01", ct);

    public async Task<List<KeyVaultReferenceInfo>> GetKeyVaultReferencesAsync(string functionAppResourceId, CancellationToken ct = default)
    {
        using var doc = await _arm.GetAsync(functionAppResourceId + "/config/configreferences/appsettings", "2022-03-01", ct: ct);

        return ArmJson.Items(doc.RootElement, "value")
            .Select(item =>
            {
                var p = ArmJson.Obj(item, "properties");
                return new KeyVaultReferenceInfo(
                    ArmJson.Str(item, "name"), ArmJson.Str(p, "vaultName"), ArmJson.Str(p, "secretName"), ArmJson.Str(p, "secretVersion"),
                    ArmJson.Str(p, "identityType"), ArmJson.Str(p, "status"), ArmJson.Str(p, "details"));
            })
            .OrderBy(r => r.Setting, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<FunctionAppInfo>> ListFunctionAppsAsync(
        string subscriptionId, string resourceGroupName, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var rgId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        var rg = client.GetResourceGroupResource(rgId);

        return await CollectFunctionAppsAsync(rg.GetWebSites().GetAllAsync(cancellationToken: ct));
    }

    /// <summary>Lists every Function App across every resource group in the subscription, in one call.</summary>
    public async Task<List<FunctionAppInfo>> ListAllFunctionAppsAsync(string subscriptionId, CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        return await CollectFunctionAppsAsync(subscription.GetWebSitesAsync(ct));
    }

    private static async Task<List<FunctionAppInfo>> CollectFunctionAppsAsync(IAsyncEnumerable<WebSiteResource> sites)
    {
        var result = new List<FunctionAppInfo>();
        await foreach (var site in sites)
        {
            if (site.Data.Kind?.Contains("functionapp", StringComparison.OrdinalIgnoreCase) == true)
            {
                result.Add(new FunctionAppInfo(
                    site.Data.Name, site.Data.Location.Name, site.Data.State ?? "Unknown",
                    site.Id!.ResourceGroupName ?? "-", site.Id!.ToString()));
            }
        }

        return result;
    }

    public async Task<List<FunctionInfo>> ListFunctionsAsync(string functionAppResourceId, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(functionAppResourceId));

        var result = new List<FunctionInfo>();
        await foreach (var function in site.GetSiteFunctions().GetAllAsync(ct))
        {
            result.Add(new FunctionInfo(
                function.Data.Name ?? "-",
                function.Data.IsDisabled ?? false,
                function.Data.Language));
        }

        return result;
    }

    /// <summary>Host-level keys: "default" is the master key, others are host function keys shared by every function.</summary>
    public async Task<Dictionary<string, string>> GetHostKeysAsync(string functionAppResourceId, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(functionAppResourceId));
        var hostKeys = await site.GetHostKeysAsync(ct);

        var result = new Dictionary<string, string>();
        if (hostKeys.Value.MasterKey is { } masterKey)
        {
            result["_master"] = masterKey;
        }

        foreach (var (name, value) in hostKeys.Value.FunctionKeys)
        {
            result[name] = value;
        }

        return result;
    }

    /// <summary>Keys scoped to a single function, usable to invoke only that function.</summary>
    public async Task<Dictionary<string, string>> GetFunctionKeysAsync(
        string functionAppResourceId, string functionName, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(functionAppResourceId));
        var function = await site.GetSiteFunctionAsync(functionName, ct);
        var keys = await function.Value.GetFunctionKeysAsync(ct);

        return new Dictionary<string, string>(keys.Value.Properties);
    }

    public async Task SetFunctionEnabledAsync(
        string functionAppResourceId, string functionName, bool enabled, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(functionAppResourceId));
        var function = await site.GetSiteFunctionAsync(functionName, ct);

        var data = function.Value.Data;
        data.IsDisabled = !enabled;
        await function.Value.UpdateAsync(Azure.WaitUntil.Completed, data, ct);
    }

    public async Task<Dictionary<string, string>> GetAppSettingsAsync(string functionAppResourceId, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(functionAppResourceId));
        var settings = await site.GetApplicationSettingsAsync(ct);
        return new Dictionary<string, string>(settings.Value.Properties);
    }

    public async Task SetAppSettingsAsync(
        string functionAppResourceId, Dictionary<string, string> settings, CancellationToken ct = default)
    {
        var site = _clientFactory.GetClient().GetWebSiteResource(new Azure.Core.ResourceIdentifier(functionAppResourceId));
        var content = new AppServiceConfigurationDictionary();
        foreach (var (key, value) in settings)
        {
            content.Properties[key] = value;
        }

        await site.UpdateApplicationSettingsAsync(content, ct);
    }

    /// <summary>
    /// Invocations from the Application Insights resource linked to this function app (matched by instrumentation key found in
    /// its app settings): a per-function summary of failures with the latest exception message, plus the invocation list.
    /// Returns HasAppInsights = false, with no error, when none is configured: that's a normal setup, not a fault.
    /// </summary>
    public async Task<FunctionInvocationReport> GetInvocationReportAsync(
        string subscriptionId, string functionAppResourceId, FunctionInvocationQuery query, CancellationToken ct = default)
    {
        var appSettings = await GetAppSettingsAsync(functionAppResourceId, ct);
        var instrumentationKey = ExtractInstrumentationKey(appSettings);
        if (instrumentationKey is null)
        {
            return FunctionInvocationReport.NoAppInsights;
        }

        var client = _clientFactory.GetClient();
        var subscription = client.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        ApplicationInsightsComponentResource? component = null;
        await foreach (var candidate in subscription.GetApplicationInsightsComponentsAsync(ct))
        {
            if (string.Equals(candidate.Data.InstrumentationKey?.ToString(), instrumentationKey, StringComparison.OrdinalIgnoreCase))
            {
                component = candidate;
                break;
            }
        }

        if (component is null)
        {
            return FunctionInvocationReport.NoAppInsights;
        }

        // A network block on the Azure Monitor Query endpoint tends to hang silently (dropped
        // packets, not a rejection) rather than fail fast, so these calls get their own timeout
        // instead of waiting on the caller forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

        var logsClient = new LogsQueryClient(_clientFactory.GetCredential());
        var range = new QueryTimeRange(TimeSpan.FromHours(query.Hours));
        var rows = Math.Clamp(query.MaxRows, 1, 5000);
        var failedFilter = query.OnlyFailures ? "| where tostring(success) =~ 'False'\n" : "";

        // `success` is text ("True"/"False") in this table, hence tostring(...) =~ everywhere.
        const string summaryQuery =
            "requests\n" +
            "| summarize Total = count(), Failures = countif(tostring(success) =~ 'False'), LastFailure = maxif(timestamp, tostring(success) =~ 'False') by name\n" +
            "| order by Failures desc, Total desc\n" +
            "| take 200";
        const string errorQuery =
            "exceptions\n" +
            "| summarize arg_max(timestamp, type, outerMessage, innermostMessage) by operation_Name";
        // Functions often log a failed run as an error trace (severity 3+) and no exception at all: use it when there is none.
        const string traceQuery =
            "traces\n" +
            "| where severityLevel >= 3 and message !startswith \"Executed '\"\n" +
            "| summarize arg_max(timestamp, message) by operation_Name";
        var listQuery =
            "requests\n" + failedFilter +
            "| order by timestamp desc\n" +
            $"| take {rows}\n" +
            "| project timestamp, name, success, resultCode, duration";

        async Task<Azure.Monitor.Query.Models.LogsTable> Run(string kusto) =>
            (await logsClient.QueryResourceAsync(component.Id!, kusto, range, cancellationToken: timeoutCts.Token)).Value.Table;

        Azure.Monitor.Query.Models.LogsTable summaryTable, errorTable, traceTable, listTable;
        try
        {
            var summaryTask = Run(summaryQuery);
            var errorTask = Run(errorQuery);
            var traceTask = Run(traceQuery);
            var listTask = Run(listQuery);
            await Task.WhenAll(summaryTask, errorTask, traceTask, listTask);
            (summaryTable, errorTable, traceTable, listTable) = (summaryTask.Result, errorTask.Result, traceTask.Result, listTask.Result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                T("The log query did not respond within 90 seconds. This is typical of a network block towards the Azure Monitor endpoints (packets dropped rather than rejected) — check the outbound firewall/proxy from this machine to *.applicationinsights.azure.com and *.ods.opinsights.azure.com."));
        }

        // Latest exception per operation (= function name): the reason behind "Error, result code 0".
        var lastErrors = new Dictionary<string, (string Type, string Message)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in errorTable.Rows)
        {
            var name = row.GetString("operation_Name");
            if (string.IsNullOrEmpty(name)) continue;

            var message = row.GetString("innermostMessage");
            if (string.IsNullOrWhiteSpace(message)) message = row.GetString("outerMessage");
            lastErrors[name] = (row.GetString("type") ?? "-", RedactAndShorten(message ?? "-"));
        }

        foreach (var row in traceTable.Rows)
        {
            var name = row.GetString("operation_Name");
            if (string.IsNullOrEmpty(name) || lastErrors.ContainsKey(name)) continue;

            lastErrors[name] = (T("error log"), RedactAndShorten(row.GetString("message") ?? "-"));
        }

        var summary = new List<FunctionFailureSummary>();
        foreach (var row in summaryTable.Rows)
        {
            var name = row.GetString("name") ?? "-";
            lastErrors.TryGetValue(name, out var error);
            summary.Add(new FunctionFailureSummary(
                name, ReadLong(row, "Total"), ReadLong(row, "Failures"),
                row.GetDateTimeOffset("LastFailure"), error.Type, error.Message));
        }

        var invocations = new List<FunctionInvocationLog>();
        foreach (var row in listTable.Rows)
        {
            invocations.Add(new FunctionInvocationLog(
                row.GetDateTimeOffset("timestamp") ?? default,
                row.GetString("name") ?? "-",
                ReadBool(row, "success"),
                row.GetString("resultCode") ?? "-",
                ReadDouble(row, "duration")));
        }

        return new FunctionInvocationReport(true, summary, invocations);
    }

    // Exception messages can carry connection strings or keys: cut anything that looks like one and keep the text short.
    private static string RedactAndShorten(string message)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            message, @"(?i)\b(password|pwd|secret|accountkey|sharedaccesskey|sharedaccesssignature|sig|token|apikey|key)\s*[=:]\s*[^;&\s""']+", "$1=" + T("[redacted]"));
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static long ReadLong(Azure.Monitor.Query.Models.LogsTableRow row, string column) => row[column] switch
    {
        long value => value,
        int value => value,
        double value => (long)value,
        string text when long.TryParse(text, out var parsed) => parsed,
        _ => 0,
    };

    // In the Application Insights "requests" table `success` comes back as the text "True"/"False" (not a boolean column) and
    // other columns can be typed differently from one resource to another: GetBoolean/GetDouble throw on that, so read tolerantly.
    private static bool ReadBool(Azure.Monitor.Query.Models.LogsTableRow row, string column) => row[column] switch
    {
        bool value => value,
        string text => text.Equals("true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static double ReadDouble(Azure.Monitor.Query.Models.LogsTableRow row, string column) => row[column] switch
    {
        double value => value,
        int value => value,
        long value => value,
        decimal value => (double)value,
        string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    private static string? ExtractInstrumentationKey(Dictionary<string, string> appSettings)
    {
        if (appSettings.TryGetValue("APPINSIGHTS_INSTRUMENTATIONKEY", out var key))
        {
            return key;
        }

        if (appSettings.TryGetValue("APPLICATIONINSIGHTS_CONNECTION_STRING", out var connectionString))
        {
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && pair[0].Equals("InstrumentationKey", StringComparison.OrdinalIgnoreCase))
                {
                    return pair[1];
                }
            }
        }

        return null;
    }
}
