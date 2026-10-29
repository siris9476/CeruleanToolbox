using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CeruleanToolbox.Services;

/// <param name="Newest">The newest published version of the same major line, as the catalog writes it (for example 1.29.122.0).</param>
/// <param name="IsNewer">True when <paramref name="Newest"/> is later than the version that runs.</param>
public sealed record CatalogAnswer(string Newest, bool IsNewer)
{
    public string Display => IsNewer ? T("{0} available", Newest) : T("Up to date");
}

/// <summary>
/// Says whether a newer version of a VM extension is published, from the catalog Azure keeps per region
/// (public data, the same for every connection, so it is cached for a few hours). It never throws: when the
/// catalog cannot be read the answer is "-" and the page goes on.
/// </summary>
public sealed partial class ExtensionCatalogService
{
    private const string ApiVersion = "2024-07-01";
    private static readonly TimeSpan CacheLife = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<string, (DateTime FetchedAt, IReadOnlyList<(Version Version, string Name)> Versions)> Cache = new();

    private readonly ArmRestClient _arm;

    public ExtensionCatalogService(ArmRestClient arm)
    {
        _arm = arm;
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}")]
    private static partial Regex LeadingVersion();

    // "1.45" and "1.45.0.0" are the same version: pad to four parts, or the shorter one would count as older.
    private static Version? Parse(string text)
    {
        var match = LeadingVersion().Match(text.Trim());
        if (!match.Success || !Version.TryParse(match.Value, out var v))
        {
            return null;
        }

        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    /// <summary>
    /// "Up to date", "1.29.122.0 available" (newest version of the same major line), or "-" when unknown.
    /// <paramref name="forceRefresh"/> skips the cache and reads the catalog again (the Refresh button).
    /// </summary>
    public async Task<string> StatusAsync(
        string subscriptionId, string location, string publisher, string type, string runningVersion,
        CancellationToken ct = default, bool forceRefresh = false)
    {
        var answer = await LookupAsync(subscriptionId, location, publisher, type, runningVersion, ct, forceRefresh);
        return answer?.Display ?? "-";
    }

    /// <summary>The newest published version of the same major line as the running one, or null when it cannot be told.</summary>
    public async Task<CatalogAnswer?> LookupAsync(
        string subscriptionId, string location, string publisher, string type, string runningVersion,
        CancellationToken ct = default, bool forceRefresh = false)
    {
        try
        {
            if (subscriptionId is "-" or "" || location is "-" or "" || publisher is "-" or "" || type is "-" or "")
            {
                return null;
            }

            var current = Parse(runningVersion);
            if (current is null)
            {
                return null;
            }

            var versions = await GetVersionsAsync(subscriptionId, location, publisher, type, forceRefresh, ct);
            var sameLine = versions.Where(v => v.Version.Major == current.Major).ToList();
            if (sameLine.Count == 0)
            {
                return null;
            }

            var newest = sameLine.MaxBy(v => v.Version);
            return new CatalogAnswer(newest.Name, newest.Version > current);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<(Version Version, string Name)>> GetVersionsAsync(
        string subscriptionId, string location, string publisher, string type, bool forceRefresh, CancellationToken ct)
    {
        var key = $"{location}|{publisher}|{type}".ToLowerInvariant();
        if (!forceRefresh && Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheLife)
        {
            return cached.Versions;
        }

        var path = $"/subscriptions/{subscriptionId}/providers/Microsoft.Compute/locations/{location}"
                   + $"/publishers/{publisher}/artifacttypes/vmextension/types/{type}/versions";
        using var doc = await _arm.GetAsync(path, ApiVersion, ct: ct);

        // The answer is a plain JSON array of {name, location, id}; "value" is accepted too in case Azure wraps it.
        var root = doc.RootElement;
        var items = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root.EnumerateArray() : ArmJson.Items(root, "value");
        var versions = new List<(Version Version, string Name)>();
        foreach (var item in items)
        {
            var name = ArmJson.Str(item, "name");
            if (Parse(name) is { } version)
            {
                versions.Add((version, name));
            }
        }

        Cache[key] = (DateTime.UtcNow, versions);
        return versions;
    }
}
