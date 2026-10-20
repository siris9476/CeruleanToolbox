using System.Text.Json;

namespace Toolbox.Core.Services;

/// <summary>
/// Runs an Azure Resource Graph query over every subscription the active connection can read (no subscription list needed) and returns
/// all rows as column → text, following the paging. Much faster than listing resources subscription by subscription: one service call
/// per thousand rows. Resource Graph can lag a few minutes behind changes, so it suits pickers and overviews, not decisions on fresh state.
/// </summary>
public sealed class ResourceGraphClient
{
    private readonly ArmRestClient _arm;

    public ResourceGraphClient(ArmRestClient arm)
    {
        _arm = arm;
    }

    public async Task<List<Dictionary<string, string>>> QueryRowsAsync(string query, CancellationToken ct = default)
    {
        const int PageSize = 1000;
        var rows = new List<Dictionary<string, string>>();
        string? skipToken = null;

        for (var page = 0; page < 50; page++)
        {
            var options = new Dictionary<string, object> { ["$top"] = PageSize, ["resultFormat"] = "objectArray" };
            if (skipToken is not null) options["$skipToken"] = skipToken;

            using var doc = await _arm.PostJsonAsync(
                "/providers/Microsoft.ResourceGraph/resources", "2022-10-01", new { query, options }, ct);

            foreach (var item in ArmJson.Items(doc.RootElement, "data"))
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in item.EnumerateObject())
                {
                    row[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
                }

                rows.Add(row);
            }

            skipToken = doc.RootElement.TryGetProperty("$skipToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (string.IsNullOrEmpty(skipToken)) break;
        }

        return rows;
    }
}
