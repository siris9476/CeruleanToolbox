using System.Text;

namespace CeruleanToolbox.Services;

/// <summary>
/// A real interactive PowerShell terminal (ConPTY + pwsh) for the console page, wired to the active connection:
/// <c>az</c> logs in with its service principal on first use, into a private <c>AZURE_CONFIG_DIR</c> that only
/// this session uses (the user's own <c>~/.azure</c> login is never touched), and <c>Invoke-Arm</c> calls ARM with
/// the same identity. The session is tied to the connection it started with.
/// </summary>
public sealed class TerminalSessionService : IDisposable
{
    private const int BacklogLimit = 200_000;

    // Runs inside the terminal at startup. Secrets come from the child's environment, never from this text.
    // Text the script prints is translated when the script is composed (P escapes it for a PowerShell single-quoted string);
    // messages with values use the PowerShell -f operator with {0}, {1}... placeholders.
    private static string P(string english) => T(english).Replace("'", "''");

    private static string InitScript => $$"""
$ErrorActionPreference = 'Continue'
$script:ArmToken = $null
$script:ArmTokenExpires = [datetime]::MinValue
function Get-ArmToken {
    if ($script:ArmToken -and (Get-Date) -lt $script:ArmTokenExpires) { return $script:ArmToken }
    $body = @{ grant_type = 'client_credentials'; client_id = $env:CTB_CLIENT_ID; client_secret = $env:CTB_CLIENT_SECRET; scope = 'https://management.azure.com/.default' }
    $r = Invoke-RestMethod -Method POST -Uri ('https://login.microsoftonline.com/' + $env:CTB_TENANT_ID + '/oauth2/v2.0/token') -Body $body
    $script:ArmToken = $r.access_token
    $script:ArmTokenExpires = (Get-Date).AddSeconds([int]$r.expires_in - 120)
    $script:ArmToken
}
function Invoke-Arm {
    param(
        [ValidateSet('GET','PUT','POST','PATCH','DELETE')][string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [string]$ApiVersion,
        $Body)
    $uri = if ($Path -match '^https://') { $Path } else { 'https://management.azure.com' + $Path }
    if ($ApiVersion) { $uri += $(if ($uri.Contains('?')) { '&' } else { '?' }) + 'api-version=' + $ApiVersion }
    $request = @{ Method = $Method; Uri = $uri; Headers = @{ Authorization = 'Bearer ' + (Get-ArmToken) } }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 30 }
    }
    Invoke-RestMethod @request
}
$script:AzLoggedIn = $false
function az {
    $azPath = (Get-Command az.cmd -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $azPath) { $azPath = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' }
    if (-not (Test-Path $azPath)) { throw '{{P("az CLI not found: winget install Microsoft.AzureCLI and restart the app.")}}' }
    $first = if ($args.Count -gt 0) { [string]$args[0] } else { '' }
    if (-not $script:AzLoggedIn -and $env:CTB_CLIENT_ID -and $first -notin @('login', 'logout', 'version', '--version', 'config', 'help', '--help')) {
        & $azPath login --service-principal --username $env:CTB_CLIENT_ID --password=$env:CTB_CLIENT_SECRET --tenant $env:CTB_TENANT_ID --output none
        if ($LASTEXITCODE -ne 0) { throw '{{P("az login failed with the service principal of the active connection.")}}' }
        $script:AzLoggedIn = $true
    }
    & $azPath @args
}
$script:AksTarget = $null
function Use-Aks {
    <# Picks the AKS cluster that kubectl and helm act on in this session. By default they go THROUGH AZURE
       (az aks command invoke): this is the way to reach a private cluster, with no VPN and no kubelogin.
       -Direct instead fetches a kubeconfig (converted with kubelogin -l azurecli; -Admin takes the admin kubeconfig)
       so kubectl/helm talk to the API server themselves; that needs a network path to it. -Agent runs them on a VM
       inside the cluster's network with the admin credentials, like a pipeline with useClusterAdmin. #>
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [string]$ResourceGroup,
        [string]$SubscriptionId,
        [switch]$Direct,
        [switch]$Admin,
        [string]$Agent,
        [string]$AgentResourceGroup)
    $subArgs = if ($SubscriptionId) { @('--subscription', $SubscriptionId) } else { @() }
    $found = @(az aks list @subArgs --query ('[?name==''' + $Name + '''].{rg:resourceGroup, sub:id}') -o json | ConvertFrom-Json)
    if ($ResourceGroup) { $found = @($found | Where-Object { $_.rg -eq $ResourceGroup }) }
    if ($found.Count -eq 0) { throw ('{{P("Cluster {0} not found.")}}' -f $Name) }
    if ($found.Count -gt 1) { throw ('{{P("More than one cluster named {0}: specify -ResourceGroup.")}}' -f $Name) }
    $sub = ($found[0].sub -split '/')[2]
    # The commands typed afterwards (az aks update -g ... -n ..., az vm list, ...) must hit the cluster's subscription, not whichever
    # one az picked first at login. The az config folder is private to this session, so this touches nothing outside it.
    az account set --subscription $sub | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ('{{P("az account set failed for subscription {0}.")}}' -f $sub) }
    if ($Agent -and -not $AgentResourceGroup) {
        $agentRgs = @(az vm list --subscription $sub --query ('[?name==''' + $Agent + '''].resourceGroup') -o json | ConvertFrom-Json)
        if ($agentRgs.Count -ne 1) { throw ('{{P("Agent VM {0} not found (or ambiguous): specify -AgentResourceGroup.")}}' -f $Agent) }
        $AgentResourceGroup = $agentRgs[0]
    }
    $script:AksTarget = @{ Name = $Name; ResourceGroup = $found[0].rg; Subscription = $sub; Direct = [bool]$Direct; Agent = $Agent; AgentResourceGroup = $AgentResourceGroup }
    if ($Agent -and -not $Direct) {
        Remove-Item Env:KUBECONFIG -ErrorAction SilentlyContinue
        Write-Host ('{{P("kubectl and helm will use {0} FROM the VM {1} (az vm run-command, cluster admin credentials). Each command takes 1-3 minutes. Try: kubectl get nodes")}}' -f $Name, $Agent) -ForegroundColor Green
        return
    }
    if (-not $Direct) {
        Remove-Item Env:KUBECONFIG -ErrorAction SilentlyContinue
        Write-Host ('{{P("kubectl and helm will use {0} through Azure with your Entra identity (az aks command invoke): an AKS RBAC role on the cluster is required. For admin credentials like the pipeline, use -Agent <vm>.")}}' -f $Name) -ForegroundColor Green
        return
    }
    $kubelogin = (Get-Command kubelogin -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (-not $kubelogin -and -not $Admin) { throw '{{P("kubelogin not found: winget install Microsoft.Azure.Kubelogin and restart the session.")}}' }
    $file = Join-Path $env:AZURE_CONFIG_DIR 'kubeconfig.yaml'
    $adminArgs = if ($Admin) { @('--admin') } else { @() }
    az aks get-credentials --resource-group $found[0].rg --name $Name --subscription $sub --file $file --overwrite-existing @adminArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '{{P("az aks get-credentials failed.")}}' }
    if (-not $Admin) {
        & $kubelogin convert-kubeconfig -l azurecli --kubeconfig $file
        if ($LASTEXITCODE -ne 0) { throw '{{P("kubelogin convert-kubeconfig failed.")}}' }
    }
    $env:KUBECONFIG = $file
    Write-Host ('{{P("KUBECONFIG set for {0} (direct access). Try: kubectl get nodes")}}' -f $Name) -ForegroundColor Green
}
function ConvertTo-ShellWord {
    param([string]$Word)
    if ($Word -match '^[A-Za-z0-9_./:=,@%+-]+$') { return $Word }
    # POSIX single-quote quoting: it is the shell inside the cluster's pod that parses this line.
    $q = [string][char]39
    $q + $Word.Replace($q, $q + '\' + $q + $q) + $q
}
function Test-AksAgent {
    <# Real reachability test of a private AKS cluster FROM a VM: runs a read-only script on the VM (az vm run-command)
       that resolves the cluster's private name and tries a TCP connection to port 443. Unlike the network-metadata
       estimate in the page, it covers NSGs, firewalls, routes and peerings in one go. #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ResourceGroup,
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$Agent,
        [Parameter(Mandatory)][string]$AgentResourceGroup)
    $fqdn = az aks show --resource-group $ResourceGroup --name $Name --subscription $SubscriptionId --query 'privateFqdn || fqdn' -o tsv
    if (-not $fqdn) { throw '{{P("The cluster does not expose a network name.")}}' }
    $lines = @(
        '#!/bin/bash',
        ('FQDN=' + (ConvertTo-ShellWord $fqdn)),
        'echo "DNS=$(getent hosts $FQDN | head -1 | awk ''{print $1}'')"',
        'if timeout 8 bash -c "exec 3<>/dev/tcp/$FQDN/443" 2>/dev/null; then echo TCP443=OK; else echo TCP443=FAIL; fi',
        'rm -f "$0"')
    $scriptFile = Join-Path $env:AZURE_CONFIG_DIR ('probe-' + [guid]::NewGuid().ToString('N') + '.sh')
    [IO.File]::WriteAllText($scriptFile, (($lines -join [string][char]10) + [string][char]10), (New-Object Text.UTF8Encoding $false))
    Write-Host ('{{P("Checking from {0} whether {1} is reachable (1-3 minutes)...")}}' -f $Agent, $fqdn) -ForegroundColor DarkGray
    try {
        $message = az vm run-command invoke --resource-group $AgentResourceGroup --name $Agent --subscription $SubscriptionId --command-id RunShellScript --scripts ('@' + $scriptFile) --query 'value[0].message' -o tsv
        if ($LASTEXITCODE -ne 0) { return }
    } finally { Remove-Item $scriptFile -Force -ErrorAction SilentlyContinue }
    $text = ($message -join [string][char]10)
    $dns = if ($text -match 'DNS=(\S*)') { $Matches[1] } else { '' }
    $tcp = if ($text -match 'TCP443=(OK|FAIL)') { $Matches[1] } else { '' }
    if ($tcp -eq 'OK') {
        Write-Host ('{{P("OK: {0} reaches {1} ({2} -> {3}, port 443 open).")}}' -f $Agent, $Name, $fqdn, $dns) -ForegroundColor Green
    } elseif (-not $dns) {
        Write-Host ('{{P("NO: from {0} the name {1} does not resolve (private DNS zone not linked to the VM network?).")}}' -f $Agent, $fqdn) -ForegroundColor Red
    } elseif ($tcp -eq 'FAIL') {
        Write-Host ('{{P("NO: the name resolves ({0}) but port 443 does not respond from {1} (NSG, firewall or route).")}}' -f $dns, $Agent) -ForegroundColor Red
    } else {
        Write-Host ('{{P("Unreadable result:")}}' + [string][char]10 + $text) -ForegroundColor Yellow
    }
}
function Invoke-AksOnAgent {
    # Runs a kubectl/helm command ON a build-agent VM that can reach the private API server, like a pipeline with
    # useClusterAdmin: the admin kubeconfig is fetched here with the active service principal (listClusterAdminCredential)
    # and travels inside the run-command script, which deletes it (and itself) when it ends. Residual risk: if the
    # command is cut off, the script file under /var/lib/waagent/run-command may stay on the VM until the next run.
    param($Target, [string]$Command, [string[]]$Files)
    $t = $Target
    $clusterId = '/subscriptions/' + $t.Subscription + '/resourceGroups/' + $t.ResourceGroup + '/providers/Microsoft.ContainerService/managedClusters/' + $t.Name
    $cred = Invoke-Arm -Method POST -Path ($clusterId + '/listClusterAdminCredential') -ApiVersion '2024-05-01'
    $kube = [string]$cred.kubeconfigs[0].value
    $lines = @(
        '#!/bin/bash',
        'WORK=$(mktemp -d)',
        'export KUBECONFIG=$WORK/kubeconfig',
        'trap ''rm -rf "$WORK" "$0"'' EXIT',
        'cd "$WORK"',
        ('echo ' + $kube + ' | base64 -d > "$KUBECONFIG"'),
        'chmod 600 "$KUBECONFIG"')
    foreach ($f in $Files) {
        $leaf = [IO.Path]::GetFileName($f)
        $lines += ('echo ' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($f)) + ' | base64 -d > ' + (ConvertTo-ShellWord $leaf))
    }
    $lines += $Command
    $lines += 'rc=$?'
    $lines += 'echo "__CTB_EXIT=$rc"'
    $scriptFile = Join-Path $env:AZURE_CONFIG_DIR ('agent-' + [guid]::NewGuid().ToString('N') + '.sh')
    [IO.File]::WriteAllText($scriptFile, (($lines -join [string][char]10) + [string][char]10), (New-Object Text.UTF8Encoding $false))
    Write-Host ('{{P("[{0} from VM {1}] {2}")}}' -f $t.Name, $t.Agent, $Command) -ForegroundColor DarkGray
    try {
        $message = az vm run-command invoke --resource-group $t.AgentResourceGroup --name $t.Agent --command-id RunShellScript --scripts ('@' + $scriptFile) --query 'value[0].message' -o tsv
        if ($LASTEXITCODE -ne 0) { return }
    } finally { Remove-Item $scriptFile -Force -ErrorAction SilentlyContinue }
    $text = ($message -join [string][char]10)
    $stdout = $text
    $stderr = ''
    if ($text -match '(?s)\[stdout\]\s*(.*?)\s*\[stderr\]\s*(.*)$') { $stdout = $Matches[1]; $stderr = $Matches[2] }
    $code = 0
    if ($stdout -match '__CTB_EXIT=(\d+)') { $code = [int]$Matches[1]; $stdout = ($stdout -replace '(?m)^__CTB_EXIT=\d+\s*$', '').TrimEnd() }
    if ($stdout) { $stdout }
    if ($stderr.Trim()) { Write-Host $stderr.TrimEnd() -ForegroundColor Yellow }
    $global:LASTEXITCODE = $code
}
function Invoke-AksKube {
    # Runs kubectl/helm inside the selected cluster via az aks command invoke. Not interactive (no -it, no -w);
    # local files named in the arguments (e.g. -f values.yaml in the current folder) are attached automatically.
    param([string]$Tool, [object[]]$ToolArgs)
    $t = $script:AksTarget
    $words = @($ToolArgs | ForEach-Object { [string]$_ })
    $attach = @()
    $files = @()
    foreach ($w in $words) {
        if ($w -notlike '-*' -and (Test-Path -LiteralPath $w -PathType Leaf)) { $attach += @('--file', $w); $files += $w }
    }
    $command = $Tool + ' ' + (($words | ForEach-Object { ConvertTo-ShellWord $_ }) -join ' ')
    if ($t.Agent) { Invoke-AksOnAgent -Target $t -Command $command -Files @($files | Where-Object { $_ -notmatch '[\\/]' }); return }
    Write-Host ('[' + $t.Name + ' via Azure] ' + $command) -ForegroundColor DarkGray
    $json = az aks command invoke --resource-group $t.ResourceGroup --name $t.Name --subscription $t.Subscription --command $command @attach -o json
    if ($LASTEXITCODE -ne 0) { return }
    $r = $json | ConvertFrom-Json
    if ($r.logs) { $r.logs.TrimEnd() }
    $global:LASTEXITCODE = [int]$r.exitCode
}
function kubectl {
    if ($script:AksTarget -and -not $script:AksTarget.Direct) { Invoke-AksKube 'kubectl' $args; return }
    & (Get-Command kubectl -CommandType Application | Select-Object -First 1).Source @args
}
function helm {
    if ($script:AksTarget -and -not $script:AksTarget.Direct) { Invoke-AksKube 'helm' $args; return }
    & (Get-Command helm -CommandType Application | Select-Object -First 1).Source @args
}
Write-Host -NoNewline ($([char]27) + '[2J' + $([char]27) + '[H')
Write-Host 'Console CeruleanToolbox' -ForegroundColor Cyan
Write-Host ('{{P("  az            automatic login with the service principal {0}")}}' -f $env:CTB_CLIENT_ID)
Write-Host '{{P("  Invoke-Arm    REST calls to Azure Resource Manager with the same identity")}}'
Write-Host '{{P("  Use-Aks <name>  chooses the cluster: kubectl and helm then go through Azure (private clusters included)")}}'
Write-Host '{{P("                  -Agent <vm>: commands run on an agent VM in the cluster network (admin credentials, like the pipeline)")}}'
Write-Host '{{P("                  -Direct: direct access from your PC (needs network access to the cluster; with -Admin it uses the admin kubeconfig)")}}'
Write-Host '{{P("                  Test-AksAgent: checks whether a VM really reaches the cluster")}}'
Write-Host ''
""";

    private readonly AzureClientFactory _clientFactory;
    private readonly object _lock = new();
    private readonly StringBuilder _backlog = new();

    private ConPtyProcess? _process;
    private string? _connectionId;
    private string? _azConfigDir;

    public TerminalSessionService(AzureClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    /// <summary>Raised on a background thread with each chunk of terminal output (raw VT sequences).</summary>
    public event Action<string>? Output;

    /// <summary>Raised when the shell exits (the user typed <c>exit</c>, or the session was stopped).</summary>
    public event Action? Exited;

    public bool IsRunning => _process is not null && !_process.HasExited;

    /// <summary>The shell's exit code once it has ended (negative values are handle problems), otherwise null.</summary>
    public int? ExitCode => _process?.ExitCode;

    /// <summary>The last output, replayed into a freshly attached screen (e.g. after navigating away and back).</summary>
    public string Backlog
    {
        get
        {
            lock (_lock) return _backlog.ToString();
        }
    }

    /// <summary>Starts the shell, or resizes the running one. A session for another connection is replaced.</summary>
    public void StartOrResize(short columns, short rows)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(T("The terminal requires Windows (ConPTY)."));

        lock (_lock)
        {
            if (IsRunning && _connectionId == _clientFactory.ActiveConnectionId)
            {
                _process!.Resize(columns, rows);
                return;
            }

            StopLocked();
            StartLocked(columns, rows);
        }
    }

    public void Resize(short columns, short rows)
    {
        lock (_lock) _process?.Resize(columns, rows);
    }

    public void Write(string data)
    {
        ConPtyProcess? process;
        lock (_lock) process = _process;
        if (process is null || process.HasExited) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            process.Input.Write(bytes, 0, bytes.Length);
            process.Input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the shell closed (or its pipe was disposed) while typing: nothing to deliver the keys to
        }
    }

    /// <summary>Stops the shell and forgets the screen; the next start is a clean session.</summary>
    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StartLocked(short columns, short rows)
    {
        var login = _clientFactory.GetServicePrincipalLogin();
        _connectionId = _clientFactory.ActiveConnectionId;
        _azConfigDir = Path.Combine(Path.GetTempPath(), "aztb-az-" + Guid.NewGuid().ToString("N"));
        _backlog.Clear();

        var environment = new Dictionary<string, string>
        {
            ["AZURE_CONFIG_DIR"] = _azConfigDir,
            ["AZURE_CORE_COLLECT_TELEMETRY"] = "false", // its background telemetry process would rewrite files after cleanup
            ["CTB_CLIENT_ID"] = login.ClientId,
            ["CTB_TENANT_ID"] = login.TenantId,
            ["CTB_CLIENT_SECRET"] = login.Secret,
        };

        // The init script holds no secrets (they come from the environment); it lives in the session's private
        // folder and is removed with it.
        Directory.CreateDirectory(_azConfigDir);
        var initPath = Path.Combine(_azConfigDir, "init.ps1");
        File.WriteAllText(initPath, InitScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var process = StartShell(initPath, columns, rows, environment);
        _process = process;

        var thread = new Thread(() => ReadLoop(process)) { IsBackground = true, Name = "terminal-reader" };
        thread.Start();
    }

    private static ConPtyProcess StartShell(string initPath, short columns, short rows, Dictionary<string, string> environment)
    {
        foreach (var exe in new[] { "pwsh.exe", "powershell.exe" })
        {
            try
            {
                return ConPtyProcess.Start(
                    $"{exe} -NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"{initPath}\"", columns, rows, environment);
            }
            catch (System.ComponentModel.Win32Exception) when (exe == "pwsh.exe")
            {
                // not on PATH: fall back to Windows PowerShell
            }
        }

        throw new InvalidOperationException(T("PowerShell not found."));
    }

    private void ReadLoop(ConPtyProcess process)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192];

        try
        {
            int read;
            while ((read = process.Output.Read(bytes, 0, bytes.Length)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0);
                if (count == 0) continue;

                var text = new string(chars, 0, count);
                lock (_lock)
                {
                    _backlog.Append(text);
                    if (_backlog.Length > BacklogLimit) _backlog.Remove(0, _backlog.Length - BacklogLimit);
                }

                Output?.Invoke(text);
            }
        }
        catch (IOException)
        {
            // pipe closed
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            process.Output.Dispose();
        }

        bool isCurrent;
        lock (_lock)
        {
            isCurrent = ReferenceEquals(_process, process);
            if (isCurrent) DeleteAzConfigDirLocked();
        }

        if (isCurrent) Exited?.Invoke();
    }

    private void StopLocked()
    {
        var process = _process;
        _process = null;
        process?.Dispose();
        DeleteAzConfigDirLocked();
    }

    // The session's az login (token cache) lives only in this temp folder; it must not outlive the session.
    private void DeleteAzConfigDirLocked()
    {
        var dir = _azConfigDir;
        if (dir is null || !Directory.Exists(dir)) return;
        if (TryDeleteDirectory(dir)) return;

        // An az process that was killed with the session can still hold a log open for a moment: retry in the
        // background so the folder (it holds az's token cache) does not outlive the session.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10 && Directory.Exists(dir); attempt++)
            {
                await Task.Delay(500);
                if (TryDeleteDirectory(dir)) return;
            }
        });
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => Stop();
}
