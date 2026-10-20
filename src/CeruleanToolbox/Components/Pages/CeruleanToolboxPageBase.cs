using CeruleanToolbox.Services;
using Microsoft.AspNetCore.Components;

namespace CeruleanToolbox.Components.Pages;

/// <summary>
/// Shared state/helpers duplicated near-identically across most pages: the loaded subscription
/// list, the alert/busy flags, and the ARM-resource-id-to-subscription-display-name lookup used
/// by every table that shows a "Subscription" column. Pages inherit this via @inherits instead of
/// re-declaring it, so a new page starts from one obvious base instead of copy-pasting the
/// nearest existing page.
/// </summary>
public abstract class CeruleanToolboxPageBase : ComponentBase
{
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    [Inject] private PageStateStore PageState { get; set; } = default!;

    /// <summary>Puts back what the user had on this page before leaving it (see <see cref="PageStateStore"/>).</summary>
    protected override void OnInitialized() => PageState.Attach(this);

    protected List<SubscriptionInfo> Subscriptions { get; set; } = new();

    protected string StatusMessage { get; set; } = "";
    protected bool IsError { get; set; }
    protected bool IsBusy { get; set; }
    protected bool IsLoading { get; set; }
    protected bool HasLoadedAllSubscriptions { get; set; }

    protected string GetSubscriptionName(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var idx = Array.IndexOf(parts, "subscriptions");
        var subId = idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : null;
        if (subId is null) return "-";
        return Subscriptions.FirstOrDefault(s => string.Equals(s.SubscriptionId, subId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? subId;
    }

    protected void SetStatus(string message, bool error)
    {
        StatusMessage = message;
        IsError = error;
    }

    /// <summary>
    /// Resource id passed as ?open=... by the Resources page (or any link) to open that resource's
    /// overview directly after the page has loaded its list.
    /// </summary>
    protected string? RequestedResourceId
    {
        get
        {
            var query = new Uri(Navigation.Uri).Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "open") return Uri.UnescapeDataString(parts[1]);
            }

            return null;
        }
    }

    /// <summary>Lower-case "namespace/type[/subtype]" of <see cref="RequestedResourceId"/>, e.g. "microsoft.compute/disks".</summary>
    protected string? RequestedResourceType
    {
        get
        {
            var parts = RequestedResourceId?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts is null) return null;

            // The last "providers" segment starts the (possibly nested) type: providers/ns/type/name[/subtype/name...].
            var index = Array.FindLastIndex(parts, x => string.Equals(x, "providers", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 3 >= parts.Length) return null;

            var segments = new List<string> { parts[index + 1], parts[index + 2] };
            for (var i = index + 4; i < parts.Length; i += 2) segments.Add(parts[i]);
            return string.Join('/', segments).ToLowerInvariant();
        }
    }

    /// <summary>The last segment of <see cref="RequestedResourceId"/>: the resource name.</summary>
    protected string? RequestedResourceName => RequestedResourceId?.TrimEnd('/').Split('/').LastOrDefault();

    /// <summary>The subscription embedded in <see cref="RequestedResourceId"/>.</summary>
    protected string? RequestedSubscriptionId
    {
        get
        {
            var parts = RequestedResourceId?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var index = parts is null ? -1 : Array.FindIndex(parts, x => string.Equals(x, "subscriptions", StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < parts!.Length ? parts[index + 1] : null;
        }
    }
}
