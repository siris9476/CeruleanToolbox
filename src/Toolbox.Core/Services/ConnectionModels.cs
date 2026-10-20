namespace Toolbox.Core.Services;

/// <summary>
/// A saved service principal connection. The client secret is never stored in plain text:
/// <see cref="ProtectedSecret"/> holds it encrypted with Windows DPAPI, scoped to the
/// current Windows user account (see <see cref="ConnectionManagerService"/>).
/// </summary>
public sealed class SavedConnection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ProtectedSecret { get; set; } = "";
    public string? DefaultSubscriptionId { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset? LastVerifiedOn { get; set; }
    public int? LastVerifiedTenantCount { get; set; }
    public int? LastVerifiedSubscriptionCount { get; set; }
}

public sealed class ConnectionStoreData
{
    public List<SavedConnection> Connections { get; set; } = new();
    public string? ActiveConnectionId { get; set; }
}

public sealed record TenantSummary(string TenantId, string? DisplayName);

public sealed record TestConnectionResult(
    bool Success, string? ErrorMessage, IReadOnlyList<TenantSummary> Tenants, int SubscriptionCount);

/// <summary>Plaintext payload embedded in a passphrase-encrypted connection export file.</summary>
public sealed record ExportedConnectionPayload(string Name, string TenantId, string ClientId, string ClientSecret);
