using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;

namespace CeruleanToolbox.Services;

public enum KeyVaultObjectKind
{
    Secret,
    Key,
    Certificate,
}

public sealed record DeletedVaultObject(KeyVaultObjectKind Kind, string Name, DateTimeOffset? DeletedOn, DateTimeOffset? ScheduledPurgeDate);

/// <summary>
/// Soft-deleted secrets, keys and certificates of a vault: list them, recover them, or purge them (delete for good).
/// A purge cannot be undone, and Azure refuses it while the vault's purge protection is on and the retention period has not passed.
/// </summary>
public sealed class KeyVaultDeletedService
{
    private readonly AzureClientFactory _clientFactory;

    public KeyVaultDeletedService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    private SecretClient Secrets(string vaultUri) => new(new Uri(vaultUri), _clientFactory.GetCredential());
    private KeyClient Keys(string vaultUri) => new(new Uri(vaultUri), _clientFactory.GetCredential());
    private CertificateClient Certificates(string vaultUri) => new(new Uri(vaultUri), _clientFactory.GetCredential());

    public async Task<List<DeletedVaultObject>> ListDeletedAsync(string vaultUri, CancellationToken ct = default)
    {
        var result = new List<DeletedVaultObject>();

        await foreach (var item in Secrets(vaultUri).GetDeletedSecretsAsync(ct))
        {
            result.Add(new DeletedVaultObject(KeyVaultObjectKind.Secret, item.Name, item.DeletedOn, item.ScheduledPurgeDate));
        }

        await foreach (var item in Keys(vaultUri).GetDeletedKeysAsync(ct))
        {
            result.Add(new DeletedVaultObject(KeyVaultObjectKind.Key, item.Name, item.DeletedOn, item.ScheduledPurgeDate));
        }

        await foreach (var item in Certificates(vaultUri).GetDeletedCertificatesAsync(includePending: false, ct))
        {
            result.Add(new DeletedVaultObject(KeyVaultObjectKind.Certificate, item.Name, item.DeletedOn, item.ScheduledPurgeDate));
        }

        return result.OrderByDescending(x => x.DeletedOn).ToList();
    }

    /// <summary>Brings a soft-deleted object back, with all its versions, and waits until it is available again.</summary>
    public async Task RecoverAsync(string vaultUri, KeyVaultObjectKind kind, string name, CancellationToken ct = default)
    {
        switch (kind)
        {
            case KeyVaultObjectKind.Secret:
                await (await Secrets(vaultUri).StartRecoverDeletedSecretAsync(name, ct)).WaitForCompletionAsync(ct);
                break;
            case KeyVaultObjectKind.Key:
                await (await Keys(vaultUri).StartRecoverDeletedKeyAsync(name, ct)).WaitForCompletionAsync(ct);
                break;
            default:
                await (await Certificates(vaultUri).StartRecoverDeletedCertificateAsync(name, ct)).WaitForCompletionAsync(ct);
                break;
        }
    }

    /// <summary>Permanently deletes a soft-deleted object. Not reversible.</summary>
    public async Task PurgeAsync(string vaultUri, KeyVaultObjectKind kind, string name, CancellationToken ct = default)
    {
        switch (kind)
        {
            case KeyVaultObjectKind.Secret:
                await Secrets(vaultUri).PurgeDeletedSecretAsync(name, ct);
                break;
            case KeyVaultObjectKind.Key:
                await Keys(vaultUri).PurgeDeletedKeyAsync(name, ct);
                break;
            default:
                await Certificates(vaultUri).PurgeDeletedCertificateAsync(name, ct);
                break;
        }
    }
}
