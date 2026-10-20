using Azure.Security.KeyVault.Secrets;

namespace CeruleanToolbox.Services;

public sealed record SecretInfo(string Name, bool Enabled, DateTimeOffset? UpdatedOn);

public sealed record SecretVersionInfo(string Version, bool Enabled, DateTimeOffset? CreatedOn, DateTimeOffset? ExpiresOn);

public sealed class KeyVaultSecretService
{
    private readonly AzureClientFactory _clientFactory;

    public KeyVaultSecretService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    private SecretClient CreateClient(string vaultUri) => new(new Uri(vaultUri), _clientFactory.GetCredential());

    public async Task<List<SecretInfo>> ListSecretsAsync(string vaultUri, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var result = new List<SecretInfo>();

        await foreach (var props in client.GetPropertiesOfSecretsAsync(ct))
        {
            result.Add(new SecretInfo(props.Name, props.Enabled ?? true, props.UpdatedOn));
        }

        return result;
    }

    /// <summary>All versions of a secret, newest first. Metadata only: values are read one at a time on request.</summary>
    public async Task<List<SecretVersionInfo>> ListSecretVersionsAsync(string vaultUri, string name, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var result = new List<SecretVersionInfo>();

        await foreach (var props in client.GetPropertiesOfSecretVersionsAsync(name, ct))
        {
            if (props.Version is null) continue;
            result.Add(new SecretVersionInfo(props.Version, props.Enabled ?? true, props.CreatedOn, props.ExpiresOn));
        }

        return result.OrderByDescending(v => v.CreatedOn).ToList();
    }

    public async Task<string> GetSecretVersionValueAsync(string vaultUri, string name, string version, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var secret = await client.GetSecretAsync(name, version, ct);
        return secret.Value.Value;
    }

    /// <summary>
    /// Enables or disables one version. Key Vault cannot delete a single version; a disabled version stays
    /// in the vault but its value can no longer be read until it is enabled again.
    /// </summary>
    public async Task SetSecretVersionEnabledAsync(string vaultUri, string name, string version, bool enabled, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var properties = new SecretProperties(new Uri($"{vaultUri.TrimEnd('/')}/secrets/{name}/{version}")) { Enabled = enabled };
        await client.UpdateSecretPropertiesAsync(properties, ct);
    }

    /// <summary>Reads an old version and stores its value as a new current version (the old versions stay untouched).</summary>
    public async Task RestoreSecretVersionAsync(string vaultUri, string name, string version, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var old = await client.GetSecretAsync(name, version, ct);
        await client.SetSecretAsync(name, old.Value.Value, ct);
    }

    public async Task<string> GetSecretValueAsync(string vaultUri, string name, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var secret = await client.GetSecretAsync(name, cancellationToken: ct);
        return secret.Value.Value;
    }

    /// <summary>
    /// Sets a secret's value. Key Vault secrets are immutable per version: this creates a new
    /// version and makes it current, it does not mutate an existing version in place.
    /// </summary>
    public async Task SetSecretValueAsync(string vaultUri, string name, string value, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        await client.SetSecretAsync(name, value, ct);
    }
}
