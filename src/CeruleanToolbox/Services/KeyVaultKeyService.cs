using Azure.Security.KeyVault.Keys;

namespace CeruleanToolbox.Services;

public sealed record KeyInfo(string Name, bool Enabled, string KeyType, DateTimeOffset? UpdatedOn);

public sealed class KeyVaultKeyService
{
    private readonly AzureClientFactory _clientFactory;

    public KeyVaultKeyService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    private KeyClient CreateClient(string vaultUri) => new(new Uri(vaultUri), _clientFactory.GetCredential());

    public async Task<List<KeyInfo>> ListKeysAsync(string vaultUri, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var result = new List<KeyInfo>();

        await foreach (var props in client.GetPropertiesOfKeysAsync(ct))
        {
            result.Add(new KeyInfo(props.Name, props.Enabled ?? true, "-", props.UpdatedOn));
        }

        return result;
    }

    /// <summary>
    /// Creates a new key (or a new version of an existing one). Only the public portion is ever
    /// readable back; the private key material never leaves the vault's HSM/software backend.
    /// </summary>
    public async Task<KeyInfo> CreateKeyAsync(string vaultUri, string name, string keyType, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);

        var azureKeyType = keyType.Equals("EC", StringComparison.OrdinalIgnoreCase)
            ? KeyType.Ec
            : KeyType.Rsa;

        var result = await client.CreateKeyAsync(name, azureKeyType, cancellationToken: ct);
        var key = result.Value;

        return new KeyInfo(key.Name, key.Properties.Enabled ?? true, key.KeyType.ToString(), key.Properties.UpdatedOn);
    }
}
