using System.Text.Json;
using System.Text.RegularExpressions;

namespace CeruleanToolbox.Services;

public sealed record ActivityEvent(
    string Id, DateTimeOffset When, string Operation, string Status, string Caller, string AppId, string ClientIp,
    string ResourceId, string ResourceName, string ResourceGroup, string Category, string CorrelationId, string Message);

/// <summary>Events that share a correlation id: one logical action (e.g. a pipeline run's "delete VM, disk and NIC").</summary>
public sealed record ActivityOperation(
    string CorrelationId, DateTimeOffset Start, DateTimeOffset End, string Status, string Title, string Caller, string AppId,
    string ClientIp, string ResourceGroup, bool IsNoise, IReadOnlyList<ActivityEvent> Events);

/// <summary>
/// Reads a subscription's Activity Log (who did what, when, from which IP) as raw JSON. A week is thousands of events in
/// dozens of pages, so the range is split into 24-hour windows fetched in parallel and delivered window by window.
/// </summary>
public sealed class ActivityLogService
{
    private const string ApiVersion = "2015-04-01";
    private const string Select =
        "eventDataId,eventTimestamp,operationName,status,subStatus,caller,claims,httpRequest,resourceId,correlationId," +
        "resourceGroupName,resourceType,category,level,properties";
    private const int MaxPagesPerWindow = 80;
    private const int Parallelism = 4;

    private readonly ArmRestClient _arm;

    public ActivityLogService(ArmRestClient arm)
    {
        _arm = arm;
    }

    /// <summary>
    /// Loads [from, to) and calls <paramref name="onWindow"/> (never concurrently) with the events of each finished 24-hour window.
    /// </summary>
    public async Task LoadAsync(
        string subscriptionId, DateTimeOffset from, DateTimeOffset to, string? resourceId,
        Func<IReadOnlyList<ActivityEvent>, int, int, Task> onWindow, CancellationToken ct)
    {
        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        for (var start = from; start < to; start = start.AddHours(24))
        {
            var end = start.AddHours(24);
            windows.Add((start, end < to ? end : to));
        }

        var done = 0;
        using var requests = new SemaphoreSlim(Parallelism);
        using var callbacks = new SemaphoreSlim(1);

        await Task.WhenAll(windows.Select(async window =>
        {
            await requests.WaitAsync(ct);
            IReadOnlyList<ActivityEvent> events;
            try
            {
                events = await LoadWindowAsync(subscriptionId, window.Start, window.End, resourceId, ct);
            }
            finally
            {
                requests.Release();
            }

            await callbacks.WaitAsync(ct);
            try
            {
                await onWindow(events, Interlocked.Increment(ref done), windows.Count);
            }
            finally
            {
                callbacks.Release();
            }
        }));
    }

    /// <summary>Events of one resource (and its child resources, e.g. a VM's extensions) over the last <paramref name="days"/> days.</summary>
    public async Task<List<ActivityEvent>> LoadForResourceAsync(string subscriptionId, string resourceId, int days, CancellationToken ct)
    {
        var all = new List<ActivityEvent>();
        var to = DateTimeOffset.UtcNow;
        await LoadAsync(subscriptionId, to.AddDays(-days), to, resourceId, (events, _, _) =>
        {
            all.AddRange(events);
            return Task.CompletedTask;
        }, ct);

        return all.GroupBy(e => e.Id).Select(g => g.First()).ToList();
    }

    private async Task<IReadOnlyList<ActivityEvent>> LoadWindowAsync(
        string subscriptionId, DateTimeOffset start, DateTimeOffset end, string? resourceId, CancellationToken ct)
    {
        var filter = $"eventTimestamp ge '{start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}' and eventTimestamp le '{end.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'";
        if (!string.IsNullOrEmpty(resourceId)) filter += $" and resourceUri eq '{resourceId}'";

        var result = new List<ActivityEvent>();
        string? next = null;
        var pages = 0;
        do
        {
            using var doc = next is null
                ? await _arm.GetAsync($"/subscriptions/{subscriptionId}/providers/Microsoft.Insights/eventtypes/management/values",
                    ApiVersion, extraQuery: "$filter=" + Uri.EscapeDataString(filter) + "&$select=" + Select, ct: ct)
                : await NextPageAsync(next, ct);

            foreach (var item in Items(doc.RootElement))
            {
                if (Parse(item) is { } parsed) result.Add(parsed);
            }

            next = Text(Child(doc.RootElement, "nextLink"));
        }
        while (next is not null && ++pages < MaxPagesPerWindow);

        return result;
    }

    // ArmRestClient takes a path and adds api-version itself: split the absolute nextLink into path and remaining query.
    private Task<JsonDocument> NextPageAsync(string nextLink, CancellationToken ct)
    {
        var uri = new Uri(nextLink);
        var rest = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("api-version=", StringComparison.OrdinalIgnoreCase));
        return _arm.GetAsync(uri.AbsolutePath, ApiVersion, extraQuery: string.Join('&', rest), ct: ct);
    }

    /// <summary>The subscription's public IP addresses, to put a name next to the caller IP of an event.</summary>
    public async Task<Dictionary<string, string>> GetPublicIpLabelsAsync(string subscriptionId, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>();
        using var doc = await _arm.GetAsync($"/subscriptions/{subscriptionId}/providers/Microsoft.Network/publicIPAddresses", "2024-05-01", ct: ct);
        foreach (var ip in Items(doc.RootElement))
        {
            var props = Child(ip, "properties");
            var address = Text(Child(props, "ipAddress"));
            if (address is null) continue;

            labels[address] = Text(Child(ip, "name")) ?? T("Public IP");
        }

        return labels;
    }

    /// <summary>Groups events by correlation id, newest first.</summary>
    public static List<ActivityOperation> Group(IEnumerable<ActivityEvent> events) =>
        events.GroupBy(e => string.IsNullOrEmpty(e.CorrelationId) ? e.Id : e.CorrelationId)
            .Select(g =>
            {
                var list = g.OrderBy(e => e.When).ToList();
                var final = list.Where(e => e.Status is "Succeeded" or "Failed").ToList();
                var shown = final.Count > 0 ? final : list;
                var status = list.Any(e => e.Status == "Failed") ? "Failed"
                    : list.Any(e => e.Status == "Succeeded") ? "Succeeded" : list[^1].Status;

                // The title names what was done, not the policy evaluations around it: real changes first, deletions before writes.
                var meaningful = shown.Where(e => !IsNoise(e)).ToList();
                var names = (meaningful.Count > 0 ? meaningful : shown)
                    .Select(e => ShortOperation(e.Operation)).Distinct()
                    .OrderBy(n => n.EndsWith("/delete", StringComparison.OrdinalIgnoreCase) ? 0
                        : n.EndsWith("/write", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ToList();
                var title = names.Count <= 2 ? string.Join(", ", names) : T("{0}, {1} and {2} more", names[0], names[1], names.Count - 2);
                var first = list[0];
                return new ActivityOperation(
                    g.Key, list[0].When, list[^1].When, status, title, first.Caller, first.AppId, first.ClientIp,
                    shown.Select(e => e.ResourceGroup).FirstOrDefault(r => r.Length > 0) ?? "", list.All(IsNoise), list);
            })
            .OrderByDescending(o => o.End)
            .ToList();

    public static string ShortOperation(string operation) =>
        operation.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ? operation["Microsoft.".Length..] : operation;

    // Azure Policy evaluations, Defender and health notifications flood the log (most of a week's events) and are rarely what is looked for.
    public static bool IsNoise(ActivityEvent e) =>
        e.Category is "Policy" or "ResourceHealth" or "ServiceHealth" or "Security" or "Alert" or "Recommendation" ||
        e.Operation.StartsWith("Microsoft.Authorization/policies", StringComparison.OrdinalIgnoreCase) ||
        e.Operation.StartsWith("Microsoft.PolicyInsights", StringComparison.OrdinalIgnoreCase) ||
        e.Operation.StartsWith("Microsoft.Security", StringComparison.OrdinalIgnoreCase) ||
        e.Operation.StartsWith("Microsoft.GuestConfiguration", StringComparison.OrdinalIgnoreCase) ||
        e.Operation.StartsWith("Microsoft.Resourcehealth", StringComparison.OrdinalIgnoreCase);

    private static ActivityEvent? Parse(JsonElement e)
    {
        if (Text(Child(e, "eventTimestamp")) is not { } timestamp || !DateTimeOffset.TryParse(timestamp, out var when)) return null;

        var resourceId = Text(Child(e, "resourceId")) ?? "";
        var operation = Text(Child(Child(e, "operationName"), "value")) ?? "?";
        var status = Text(Child(Child(e, "status"), "value")) ?? "?";
        return new ActivityEvent(
            Text(Child(e, "eventDataId")) ?? Guid.NewGuid().ToString("N"), when, operation, status,
            Text(Child(e, "caller")) ?? "", Text(Child(Child(e, "claims"), "appid")) ?? "",
            Text(Child(Child(e, "httpRequest"), "clientIpAddress")) ?? "",
            resourceId, resourceId.Length > 0 ? resourceId[(resourceId.LastIndexOf('/') + 1)..] : "",
            Text(Child(e, "resourceGroupName")) ?? "", Text(Child(Child(e, "category"), "value")) ?? "",
            Text(Child(e, "correlationId")) ?? "", status == "Failed" ? FailureMessage(e) : "");
    }

    private static string FailureMessage(JsonElement e)
    {
        var sub = Text(Child(Child(e, "subStatus"), "value"));
        var raw = Text(Child(Child(e, "properties"), "statusMessage"));
        string? message = null;
        if (raw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                message = Text(Child(Child(doc.RootElement, "error"), "message")) ?? Text(Child(doc.RootElement, "message")) ?? raw;
            }
            catch (JsonException)
            {
                message = raw;
            }
        }

        var text = string.Join(" - ", new[] { sub, message }.Where(s => !string.IsNullOrWhiteSpace(s)));
        // Error texts can quote command lines or settings: never show something that looks like a secret.
        text = Regex.Replace(text, @"(?i)(token|pat|password|secret|sas|key)(\s*[=:]\s*|\s+)\S+", "$1=" + T("[redacted]"));
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > 600 ? text[..600] + "..." : text;
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement element)
    {
        var array = element.ValueKind == JsonValueKind.Object ? Child(element, "value") : element;
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    private static string? Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
