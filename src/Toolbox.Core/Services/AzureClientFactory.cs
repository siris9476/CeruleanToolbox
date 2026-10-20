using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;

namespace Toolbox.Core.Services;

/// <summary>
/// Builds an <see cref="ArmClient"/> from whichever connection is currently active in the
/// <see cref="ConnectionManagerService"/>, so this app acts with the exact same identity and
/// role assignments as the underlying service principal, without needing a pipeline.
/// Rebuilds the cached client only when the active connection actually changes.
/// </summary>
public sealed class AzureClientFactory
{
    private readonly ConnectionManagerService _connections;
    private readonly object _lock = new();

    private string? _cachedForConnectionId;
    private TokenCredential? _cachedCredential;
    private ArmClient? _cachedClient;

    public AzureClientFactory(ConnectionManagerService connections)
    {
        _connections = connections;
    }

    public bool IsConfigured => _connections.ActiveConnection is not null;

    public string? DefaultSubscriptionId => _connections.ActiveConnection?.DefaultSubscriptionId;

    public Guid TenantId => Guid.Parse(GetActiveConnection().TenantId);

    public string? ActiveConnectionId => _connections.ActiveConnection?.Id;

    /// <summary>
    /// The active service principal's login data, for tools that authenticate on their own (the az CLI in
    /// the PowerShell console). The secret is handed to a child process, so callers must not log it.
    /// </summary>
    public (string ClientId, string TenantId, string Secret) GetServicePrincipalLogin()
    {
        var active = GetActiveConnection();
        return (active.ClientId, active.TenantId, _connections.DecryptSecret(active));
    }

    public ArmClient GetClient()
    {
        EnsureFresh();
        return _cachedClient!;
    }

    /// <summary>
    /// Credential usable directly against Key Vault data-plane clients (SecretClient/KeyClient),
    /// which are constructed per-vault rather than through the ArmClient.
    /// </summary>
    public TokenCredential GetCredential()
    {
        EnsureFresh();
        return _cachedCredential!;
    }

    private SavedConnection GetActiveConnection() => _connections.ActiveConnection
        ?? throw new InvalidOperationException(
            T("No Azure connection is configured. Go to \"Connections\" to add one."));

    private void EnsureFresh()
    {
        var active = GetActiveConnection();

        lock (_lock)
        {
            if (_cachedForConnectionId == active.Id)
            {
                return;
            }

            var secret = _connections.DecryptSecret(active);
            _cachedCredential = new ClientSecretCredential(active.TenantId, active.ClientId, secret);
            _cachedClient = new ArmClient(_cachedCredential);
            _cachedForConnectionId = active.Id;
        }
    }
}
