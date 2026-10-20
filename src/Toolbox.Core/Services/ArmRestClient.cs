using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace Toolbox.Core.Services;

/// <summary>
/// Reads ARM resources as raw JSON with the active connection's credential. Used for resource
/// overviews because the SDK's typed models throw on responses real resources return (null arrays,
/// missing sections), and because a portal-style overview needs fields the models don't expose.
/// </summary>
public sealed class ArmRestClient
{
    private const string Endpoint = "https://management.azure.com";
    private const string Scope = "https://management.azure.com/.default";
    private static readonly HttpClient Http = new();

    private readonly AzureClientFactory _clientFactory;

    public ArmRestClient(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<JsonDocument> GetAsync(
        string path, string apiVersion, string? extraQuery = null, CancellationToken ct = default)
    {
        var token = await _clientFactory.GetCredential().GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);
        var query = $"{(extraQuery is null ? "" : extraQuery + "&")}api-version={apiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}{path}?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribeError(response.StatusCode, body));
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// A POST (an "action" call such as a refresh or a cache purge), with an optional JSON body. Returns the status code
    /// (202 = accepted, finished later); anything but 2xx throws with Azure's own explanation.
    /// </summary>
    public async Task<HttpStatusCode> PostAsync(string path, string apiVersion, CancellationToken ct = default, object? body = null)
    {
        var token = await _clientFactory.GetCredential().GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}{path}?api-version={apiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(
            body is null ? "" : JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribeError(response.StatusCode, await response.Content.ReadAsStringAsync(ct)));
        }

        return response.StatusCode;
    }

    /// <summary>A POST with a JSON body that returns the JSON answer (used for Azure Resource Graph queries).</summary>
    public async Task<JsonDocument> PostJsonAsync(string path, string apiVersion, object body, CancellationToken ct = default)
    {
        var token = await _clientFactory.GetCredential().GetTokenAsync(new TokenRequestContext(new[] { Scope }), ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}{path}?api-version={apiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribeError(response.StatusCode, text));
        }

        return JsonDocument.Parse(text);
    }

    // Azure explains why it refused in the body; surface that instead of a bare status code.
    private static string DescribeError(HttpStatusCode status, string body)
    {
        var message = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var m))
            {
                message = m.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
        }

        return T("Azure responded {0}: {1}", (int)status, message);
    }
}

/// <summary>Tolerant accessors for raw ARM JSON: a missing or null field becomes "-", never an exception.</summary>
public static class ArmJson
{
    public static JsonElement Obj(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;

    // Booleans come back as the translated words T("Yes") / T("No"): callers must compare with T("Yes"), never a literal.
    public static string Str(JsonElement element, string key)
    {
        var value = Obj(element, key);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : "-",
            JsonValueKind.True => T("Yes"),
            JsonValueKind.False => T("No"),
            JsonValueKind.Number => value.ToString(),
            _ => "-",
        };
    }

    public static IEnumerable<JsonElement> Items(JsonElement element, string key)
    {
        var value = Obj(element, key);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    public static string LastSegment(string value) => value == "-" ? value : value[(value.LastIndexOf('/') + 1)..];

    // Azure uses 0001-01-01 for "not set": show that as "-" rather than a real-looking date.
    public static string Date(string value) =>
        DateTimeOffset.TryParse(value, out var date) ? (date.Year <= 1 ? "-" : date.LocalDateTime.ToString("g")) : value;

    public static KeyValuePair<string, string> Row(string label, string value) => new(label, value);
}
