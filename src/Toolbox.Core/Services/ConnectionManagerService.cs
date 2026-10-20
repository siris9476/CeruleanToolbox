using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;

namespace Toolbox.Core.Services;

/// <summary>
/// Stores service principal connections (tenant id, client id, client secret of a Microsoft
/// Entra ID App Registration) on the local machine, encrypted with Windows DPAPI so the secret
/// is only readable by the current Windows user account. Also verifies a connection by listing
/// which Entra tenants and subscriptions it can actually reach before saving it.
/// </summary>
public sealed class ConnectionManagerService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private ConnectionStoreData _data;

    /// <summary>
    /// The connection store of one app, under <c>%LocalAppData%\&lt;appFolderName&gt;\connections.json</c>. Every app names its own
    /// folder (for example CeruleanToolbox), so no two apps share a file. The encrypted export files are the same format everywhere,
    /// so a connection moves between apps with "Export" and "Import".
    /// </summary>
    public ConnectionManagerService(string appFolderName)
    {
        if (string.IsNullOrWhiteSpace(appFolderName) || appFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(T("Invalid connections folder name."), nameof(appFolderName));
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appFolderName);
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "connections.json");
        _data = Load();
    }

    /// <summary>Raised after the active connection changed (a new active one saved, or another one selected).</summary>
    public event Action? ActiveConnectionChanged;

    /// <summary>Currently selected connection, used by <see cref="AzureClientFactory"/>. Null if none is set up yet.</summary>
    public SavedConnection? ActiveConnection
    {
        get
        {
            lock (_lock)
            {
                Reload();
                return _data.Connections.FirstOrDefault(c => c.Id == _data.ActiveConnectionId);
            }
        }
    }

    public List<SavedConnection> GetAll()
    {
        lock (_lock)
        {
            Reload();
            return _data.Connections.ToList();
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(
        string tenantId, string clientId, string clientSecret, CancellationToken ct = default)
    {
        try
        {
            TokenCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var armClient = new ArmClient(credential);

            var tenants = new List<TenantSummary>();
            await foreach (var tenant in armClient.GetTenants().GetAllAsync(ct))
            {
                tenants.Add(new TenantSummary(tenant.Data.TenantId?.ToString() ?? tenantId, tenant.Data.DisplayName));
            }

            var subscriptionCount = 0;
            await foreach (var _ in armClient.GetSubscriptions().GetAllAsync(ct))
            {
                subscriptionCount++;
            }

            return new TestConnectionResult(true, null, tenants, subscriptionCount);
        }
        catch (Exception ex)
        {
            return new TestConnectionResult(false, ex.Message, Array.Empty<TenantSummary>(), 0);
        }
    }

    public SavedConnection SaveConnection(
        string name, string tenantId, string clientId, string clientSecret,
        TestConnectionResult? verification, bool makeActive)
    {
        SavedConnection connection;
        bool activeChanged;
        lock (_lock)
        {
            Reload();
            connection = new SavedConnection
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                TenantId = tenantId,
                ClientId = clientId,
                ProtectedSecret = Protect(clientSecret),
                CreatedOn = DateTimeOffset.UtcNow,
                LastVerifiedOn = verification?.Success == true ? DateTimeOffset.UtcNow : null,
                LastVerifiedTenantCount = verification?.Success == true ? verification.Tenants.Count : null,
                LastVerifiedSubscriptionCount = verification?.Success == true ? verification.SubscriptionCount : null,
            };

            _data.Connections.Add(connection);
            activeChanged = makeActive || _data.ActiveConnectionId is null;
            if (activeChanged)
            {
                _data.ActiveConnectionId = connection.Id;
            }

            Persist();
        }

        if (activeChanged) ActiveConnectionChanged?.Invoke();
        return connection;
    }

    public void SetActive(string id)
    {
        lock (_lock)
        {
            Reload();
            if (_data.Connections.All(c => c.Id != id))
            {
                throw new InvalidOperationException(T("Connection not found."));
            }

            _data.ActiveConnectionId = id;
            Persist();
        }

        ActiveConnectionChanged?.Invoke();
    }

    /// <summary>Leaves no connection active: the pages then ask for one. The saved connections are kept.</summary>
    public void ClearActive()
    {
        lock (_lock)
        {
            Reload();
            _data.ActiveConnectionId = null;
            Persist();
        }

        ActiveConnectionChanged?.Invoke();
    }

    public void Delete(string id)
    {
        var activeChanged = false;
        lock (_lock)
        {
            Reload();
            _data.Connections.RemoveAll(c => c.Id == id);

            // Deleting the active connection leaves none active: silently moving to another one could point the pages at a
            // different environment (for example production) without the user noticing.
            if (_data.ActiveConnectionId == id)
            {
                _data.ActiveConnectionId = null;
                activeChanged = true;
            }

            Persist();
        }

        if (activeChanged) ActiveConnectionChanged?.Invoke();
    }

    internal string DecryptSecret(SavedConnection connection) => Unprotect(connection.ProtectedSecret);

    /// <summary>
    /// Exports a saved connection as a passphrase-encrypted, portable file — unlike the DPAPI
    /// encryption used for local storage, this can be decrypted on another machine/user account
    /// with the same passphrase.
    /// </summary>
    public byte[] ExportEncrypted(string connectionId, string passphrase)
    {
        lock (_lock)
        {
            Reload();
            var connection = _data.Connections.FirstOrDefault(c => c.Id == connectionId)
                ?? throw new InvalidOperationException(T("Connection not found."));

            var payload = JsonSerializer.Serialize(new ExportedConnectionPayload(
                connection.Name, connection.TenantId, connection.ClientId, DecryptSecret(connection)));

            return PassphraseCrypto.Encrypt(payload, passphrase);
        }
    }

    /// <summary>
    /// Decrypts a previously exported connection file. Returns the plaintext fields without
    /// saving anything — the caller is expected to run them through the normal verify/save flow,
    /// same as manual entry, rather than trusting an imported file blindly.
    /// </summary>
    public static ExportedConnectionPayload DecryptExport(byte[] fileBytes, string passphrase)
    {
        var json = PassphraseCrypto.Decrypt(fileBytes, passphrase);
        return JsonSerializer.Deserialize<ExportedConnectionPayload>(json)
            ?? throw new InvalidOperationException(T("Invalid export file."));
    }

    /// <summary>Decrypts and returns the client secret of a saved connection, for on-demand display in the UI.</summary>
    public string RevealSecret(string connectionId)
    {
        lock (_lock)
        {
            Reload();
            var connection = _data.Connections.FirstOrDefault(c => c.Id == connectionId)
                ?? throw new InvalidOperationException(T("Connection not found."));

            return DecryptSecret(connection);
        }
    }

    private void Reload() => _data = Load();

    private ConnectionStoreData Load()
    {
        if (!File.Exists(_filePath))
        {
            return new ConnectionStoreData();
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<ConnectionStoreData>(json) ?? new ConnectionStoreData();
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private static string Protect(string plaintext) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string protectedBase64) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser));
}
