using System.Diagnostics;
using Azure;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;

namespace CeruleanToolbox.Services;

public sealed record DeploymentOutcome(bool Succeeded, string Message, string? CorrelationId);

public sealed class DeploymentService
{
    private readonly AzureClientFactory _clientFactory;

    public DeploymentService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    /// <summary>
    /// Compiles a Bicep file to ARM JSON using the local `bicep` (or `az bicep`) CLI.
    /// Requires the Bicep CLI to be installed and reachable on PATH.
    /// </summary>
    public async Task<string> CompileBicepAsync(string bicepSource, CancellationToken ct = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bicep");
        await File.WriteAllTextAsync(tempFile, bicepSource, ct);

        try
        {
            var psi = new ProcessStartInfo("bicep", $"build \"{tempFile}\" --stdout")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException(T("Unable to start the 'bicep' process."));

            var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
            var stdErr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(T("Bicep compilation failed: {0}", stdErr));
            }

            return stdOut;
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    public async Task<DeploymentOutcome> DeployTemplateAsync(
        string subscriptionId,
        string resourceGroupName,
        string armTemplateJson,
        string? parametersJson,
        string deploymentName,
        CancellationToken ct = default)
    {
        var client = _clientFactory.GetClient();
        var rgId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        var rg = client.GetResourceGroupResource(rgId);

        var properties = new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
        {
            Template = BinaryData.FromString(armTemplateJson),
        };

        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            properties.Parameters = BinaryData.FromString(parametersJson);
        }

        var content = new ArmDeploymentContent(properties);

        try
        {
            var operation = await rg.GetArmDeployments().CreateOrUpdateAsync(
                WaitUntil.Completed, deploymentName, content, ct);

            var provisioningState = operation.Value.Data.Properties?.ProvisioningState?.ToString() ?? "Unknown";
            return new DeploymentOutcome(true, T("Deployment completed. Status: {0}", provisioningState), null);
        }
        catch (RequestFailedException ex)
        {
            return new DeploymentOutcome(false, ex.Message, ex.ErrorCode);
        }
    }
}
