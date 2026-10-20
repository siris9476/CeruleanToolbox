using Azure.Security.KeyVault.Certificates;

namespace CeruleanToolbox.Services;

public sealed record CertificateInfo(
    string Name, bool Enabled, DateTimeOffset? ExpiresOn, DateTimeOffset? UpdatedOn, string? Thumbprint);

public sealed class KeyVaultCertificateService
{
    private readonly AzureClientFactory _clientFactory;

    public KeyVaultCertificateService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    private CertificateClient CreateClient(string vaultUri) => new(new Uri(vaultUri), _clientFactory.GetCredential());

    public async Task<List<CertificateInfo>> ListCertificatesAsync(string vaultUri, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);
        var result = new List<CertificateInfo>();

        await foreach (var props in client.GetPropertiesOfCertificatesAsync(includePending: false, ct))
        {
            result.Add(new CertificateInfo(props.Name, props.Enabled ?? true, props.ExpiresOn, props.UpdatedOn, props.X509ThumbprintString));
        }

        return result;
    }

    /// <summary>
    /// Issues a self-signed certificate ("Self" issuer) — the only kind Key Vault can create
    /// without an external CA integration configured. The operation completes synchronously for
    /// self-signed certs, unlike CA-issued ones which stay pending until the CA responds.
    /// </summary>
    public async Task<CertificateInfo> CreateSelfSignedCertificateAsync(
        string vaultUri, string name, string subjectCommonName, int validityMonths, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);

        var policy = new CertificatePolicy("Self", $"CN={subjectCommonName}")
        {
            ValidityInMonths = validityMonths,
        };

        var operation = await client.StartCreateCertificateAsync(name, policy, cancellationToken: ct);
        await operation.WaitForCompletionAsync(ct);

        var certificate = operation.Value;
        return new CertificateInfo(
            certificate.Name, certificate.Properties.Enabled ?? true,
            certificate.Properties.ExpiresOn, certificate.Properties.UpdatedOn, certificate.Properties.X509ThumbprintString);
    }

    /// <summary>
    /// Key Vault has no dedicated "renew" call: manually renewing means re-running create with the
    /// certificate's existing policy, which issues a fresh version with a new expiry.
    /// </summary>
    public async Task<CertificateInfo> RenewCertificateAsync(string vaultUri, string name, CancellationToken ct = default)
    {
        var client = CreateClient(vaultUri);

        var policy = (await client.GetCertificatePolicyAsync(name, ct)).Value;
        var operation = await client.StartCreateCertificateAsync(name, policy, cancellationToken: ct);
        await operation.WaitForCompletionAsync(ct);

        var certificate = operation.Value;
        return new CertificateInfo(
            certificate.Name, certificate.Properties.Enabled ?? true,
            certificate.Properties.ExpiresOn, certificate.Properties.UpdatedOn, certificate.Properties.X509ThumbprintString);
    }

    public async Task DeleteCertificateAsync(string vaultUri, string name, CancellationToken ct = default)
    {
        // DEBUG-TEST: real deletion disabled so the UI confirm flow can be exercised without
        // side effects. Re-enable the two lines below before real use.
        Console.WriteLine($"[DEBUG-TEST] DeleteCertificateAsync() called for cert={name} on {vaultUri} — no-op, deletion disabled.");
        await Task.CompletedTask;
        return;

        var client = CreateClient(vaultUri);
        await client.StartDeleteCertificateAsync(name, ct);
    }
}
