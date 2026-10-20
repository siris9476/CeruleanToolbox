using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;

namespace Toolbox.Core.Services;

public sealed record DiagCheck(string Id, string Status, string Label, string Text);

public sealed record DiagnosticReport(
    string VmName, bool IsWindows, DateTimeOffset At, IReadOnlyList<DiagCheck> Checks, IReadOnlyList<string> Conclusions, string RawOutput);

public sealed record AgentServiceRestartResult(bool Done, bool NoService, string Output);

public sealed record LogSection(string Title, string Text);

public sealed record CustomScriptLog(
    string VmName, bool IsWindows, DateTimeOffset At, IReadOnlyList<LogSection> Sections, string RawOutput);

/// <summary>What the diagnostic looks at. General fits any VM; the agent profile adds the Azure DevOps build agent checks.</summary>
public enum DiagnosticProfile
{
    General,
    AzureDevOpsAgent,
}

/// <summary>
/// Runs a read-only script on a VM (VM run-command, Linux or Windows chosen from the OS disk) and turns its
/// "CHECK|id|STATUS|detail" lines into a report. The General profile reports disk space, whether the VM reaches Azure
/// and Microsoft Entra ID, and what the last custom-script run complained about. The AzureDevOpsAgent profile also checks
/// whether the Azure DevOps agent is installed, running as a service, busy and able to reach Azure DevOps and its download
/// host. The scripts only read: they never print the agent's credentials, and every detail is stripped of anything that
/// looks like a secret.
/// </summary>
public sealed class VmDiagnosticsService
{
    private readonly AzureClientFactory _clientFactory;
    private readonly ArmRestClient _arm;

    public VmDiagnosticsService(AzureClientFactory clientFactory, ArmRestClient arm)
    {
        _clientFactory = clientFactory;
        _arm = arm;
    }

    // Runs one of two scripts on the VM through the run-command, picking the Linux or the Windows one from the OS disk.
    private async Task<(string Raw, bool Windows)> RunScriptAsync(
        string vmId, Func<bool, string> script, CancellationToken ct)
    {
        using var doc = await _arm.GetAsync(vmId, "2024-03-01", ct: ct);
        var osDisk = ArmJson.Obj(ArmJson.Obj(ArmJson.Obj(doc.RootElement, "properties"), "storageProfile"), "osDisk");
        var windows = ArmJson.Str(osDisk, "osType") == "Windows";

        var input = new RunCommandInput(windows ? "RunPowerShellScript" : "RunShellScript");
        foreach (var line in Localize(script(windows)).Replace("\r\n", "\n").Split('\n'))
        {
            input.Script.Add(line);
        }

        var vm = _clientFactory.GetClient().GetVirtualMachineResource(new ResourceIdentifier(vmId));
        var operation = await vm.RunCommandAsync(WaitUntil.Completed, input, ct);
        var raw = string.Join("\n", operation.Value.Value.Select(s => s.Message).Where(m => !string.IsNullOrEmpty(m)));
        return (raw, windows);
    }

    /// <summary>
    /// The tail of what the custom-script extension printed on the VM (its stdout, stderr and the handler log), read-only.
    /// The script strips anything that looks like a secret and so does this method on the result, in case a line slips through.
    /// </summary>
    public async Task<CustomScriptLog> GetCustomScriptLogAsync(string vmId, CancellationToken ct)
    {
        var (raw, windows) = await RunScriptAsync(vmId, w => w ? WindowsScriptLog : LinuxScriptLog, ct);

        var sections = new List<LogSection>();
        string? title = null;
        var body = new List<string>();
        void Flush()
        {
            if (title is not null) sections.Add(new LogSection(title, Redact(string.Join("\n", body)).Trim()));
            body.Clear();
        }

        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("@@SECTION|"))
            {
                Flush();
                title = line["@@SECTION|".Length..].Trim();
            }
            else if (title is not null)
            {
                body.Add(line);
            }
        }

        Flush();
        return new CustomScriptLog(vmId.Split('/')[^1], windows, DateTimeOffset.UtcNow, sections, raw);
    }

    /// <summary>
    /// Restarts the Azure DevOps agent SERVICE on the VM (not the VM): systemd units named vsts.agent.* on Linux, services named vstsagent*
    /// on Windows. A job running on that agent is interrupted. Runs through the run-command, so it needs the VM to be running.
    /// </summary>
    public async Task<AgentServiceRestartResult> RestartAgentServiceAsync(string vmId, CancellationToken ct)
    {
        var (raw, _) = await RunScriptAsync(vmId, w => w ? WindowsRestartAgent : LinuxRestartAgent, ct);
        var text = raw.Replace("\r", "").Trim();
        return new AgentServiceRestartResult(text.Contains("RESULT|done"), text.Contains("RESULT|no-service"), text);
    }

    private const string LinuxRestartAgent = """"
units=$(systemctl list-units --type=service --all --no-legend 2>/dev/null | grep -i -E 'vsts\.agent|azpipelines' | sed 's/^[^a-zA-Z]*//' | awk '{print $1}')
if [ -z "$units" ]; then echo "RESULT|no-service"; exit 0; fi
for u in $units; do
  echo "<<L:restarting>> $u"
  systemctl restart "$u" 2>&1 | tail -n 3
  sleep 5
  echo "<<L:state>>: $(systemctl is-active "$u")"
done
echo "RESULT|done"
"""";

    private const string WindowsRestartAgent = """"
$svc = @(Get-Service -Name 'vstsagent*' -ErrorAction SilentlyContinue)
if ($svc.Count -eq 0) { 'RESULT|no-service'; return }
foreach ($s in $svc) {
  "<<L:restarting>> $($s.Name)"
  Restart-Service -Name $s.Name -Force
  Start-Sleep -Seconds 5
  "<<L:state>>: $((Get-Service -Name $s.Name).Status)"
}
'RESULT|done'
"""";

    // The scripts that run on the VM print a few words for the user (restarting, state, section titles). They are written
    // as <<L:English text>> or <<L:English text with {0}|argument>> and replaced here by the text in the current language,
    // so the script that reaches the VM is plain text.
    private static string Localize(string script) =>
        Regex.Replace(script, @"<<L:(.*?)(?:\|(.*?))?>>", m => m.Groups[2].Success ? T(m.Groups[1].Value, m.Groups[2].Value) : T(m.Groups[1].Value));

    // Azure DevOps personal access tokens are 52 characters of lowercase letters and digits.
    // The scripts mask secrets with the fixed marker [redatto]; it is shown in the current language.
    private static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)(--token|--password|--pass)[ =]+\S+", "$1 [redatto]");
        text = Regex.Replace(text, @"(?i)\b(token|password|passwd|secret|pat|key)([=: ]+)\S+", "$1$2[redatto]");
        text = Regex.Replace(text, @"\b[a-z0-9]{52}\b", "[redatto]");
        return text.Replace("[redatto]", T("[redacted]"));
    }

    public async Task<DiagnosticReport> RunAsync(string vmId, DiagnosticProfile profile, CancellationToken ct)
    {
        var (raw, windows) = await RunScriptAsync(vmId, w => w ? WindowsScript(profile) : LinuxScript(profile), ct);

        var checks = Regex.Matches(raw.Replace("\r", ""), @"(?m)^CHECK\|([^|\n]+)\|([^|\n]+)\|(.*)$")
            .Select(m => Describe(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim()))
            .ToList();

        return new DiagnosticReport(vmId.Split('/')[^1], windows, DateTimeOffset.UtcNow, checks, Conclude(profile, checks), raw);
    }

    private static Dictionary<string, string> Labels => new()
    {
        ["system"] = T("System"),
        ["disk"] = T("Disk space"),
        ["agent-installed"] = T("Azure DevOps agent installed"),
        ["agent-service"] = T("Agent service"),
        ["agent-listener"] = T("Agent running"),
        ["agent-worker"] = T("Job in progress"),
        ["agent-lastjob"] = T("Last job run"),
        ["agent-log"] = T("Agent log"),
        ["net-devops"] = T("Azure DevOps reachability"),
        ["net-download"] = T("Agent download address (new)"),
        ["net-old"] = T("Agent download address (old, azureedge.net)"),
        ["net-arm"] = T("Azure reachability (management.azure.com)"),
        ["net-login"] = T("Microsoft Entra ID reachability (login.microsoftonline.com)"),
        ["customscript"] = T("Last custom script run"),
        ["tools"] = T("Installed tools"),
    };

    // Turns the script's terse facts into a sentence; the details in the script are plain data on purpose.
    private static DiagCheck Describe(string id, string status, string detail)
    {
        var label = Labels.GetValueOrDefault(id, id);
        var parts = detail.Split(';').Select(p => p.Trim()).ToArray();
        var text = (id, status) switch
        {
            ("agent-installed", "FAIL") => T("No installation found: there is no agent configuration file on the VM."),
            ("agent-installed", _) => T("Folder {0}; {1}", parts.ElementAtOrDefault(0), string.Join(", ", parts.Skip(1))),
            ("agent-service", "WARN") => T("No system service: the agent does not restart by itself after a VM reboot."),
            ("agent-service", "INFO") => T("No service (the agent is not installed)."),
            ("agent-service", _) => T("Service {0}, status {1}.", parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1)),
            ("agent-listener", "FAIL") => T("Not running: the agent shows as offline in Azure DevOps."),
            ("agent-listener", "INFO") => T("Not running (the agent is not installed)."),
            ("agent-listener", _) => T("Running for {0}.", Elapsed(detail)),
            ("agent-worker", _) when detail == "idle" => T("No job in progress."),
            ("agent-worker", _) => T("A job has been running for {0}.", Elapsed(detail)),
            ("agent-lastjob", _) => T("{0} ({1}).", parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1)),
            ("agent-log", "OK") => T("No recent errors."),
            ("net-old", _) when detail.StartsWith("unresolved") => T("The name no longer resolves: Microsoft has retired this address. Scripts that use it cannot download the agent."),
            ("customscript", "OK") => T("No errors detected."),
            _ when detail.StartsWith("unresolved:") => T("The name {0} does not resolve from the VM.", detail["unresolved:".Length..]),
            _ when detail.StartsWith("https-fail:") => T("{0}: the name resolves but the HTTPS connection fails.", detail["https-fail:".Length..]),
            _ when detail.StartsWith("reach:") => T("Reachable ({0}).", detail["reach:".Length..]),
            _ => detail,
        };

        return new DiagCheck(id, status, label, text);
    }

    // Linux ps prints [dd-][hh:]mm:ss, Windows [d.]hh:mm:ss: say it the same way ("6 days 07:21:43").
    private static string Elapsed(string value)
    {
        var m = Regex.Match(value, @"^(\d+)[-.](\d{1,2}:\d{2}(:\d{2})?)$");
        if (!m.Success) return value;

        var days = int.Parse(m.Groups[1].Value);
        return days == 0 ? m.Groups[2].Value : days == 1 ? T("{0} day {1}", days, m.Groups[2].Value) : T("{0} days {1}", days, m.Groups[2].Value);
    }

    // Short, conservative readings of what the checks add up to. They name the likely cause, not a certainty.
    private static List<string> Conclude(DiagnosticProfile profile, IReadOnlyList<DiagCheck> checks)
    {
        var conclusions = profile == DiagnosticProfile.AzureDevOpsAgent ? ConcludeAgent(checks) : ConcludeGeneral(checks);

        if (checks.Count == 0)
        {
            conclusions.Add(T("The script returned no readable checks: see the raw output."));
        }

        return conclusions;
    }

    private static List<string> ConcludeGeneral(IReadOnlyList<DiagCheck> checks)
    {
        DiagCheck? Get(string id) => checks.FirstOrDefault(c => c.Id == id);
        var conclusions = new List<string>();

        if (Get("disk")?.Status == "WARN")
        {
            conclusions.Add(T("Disk space is almost full: extensions, updates and agents may fail. Free up space or enlarge the disk."));
        }

        if (checks.Any(c => c.Id is "net-arm" or "net-login" && c.Status == "FAIL"))
        {
            conclusions.Add(T("The VM cannot reach Azure or Microsoft Entra ID: check DNS, network rules (NSG), firewall and proxy. ") +
                            T("Without this connection, Azure extensions and agents do not work."));
        }

        if (Get("customscript") is { Status: "WARN" })
        {
            conclusions.Add(T("The last custom script reported errors (see the detail below). If it already did what was needed you can ignore them, ") +
                            T("otherwise fix the cause and run the provisioning again."));
        }

        if (conclusions.Count == 0 && checks.Count > 0)
        {
            conclusions.Add(checks.All(c => c.Status is "OK" or "INFO")
                ? T("No problems found: disk, network to Azure and the last custom script are fine.")
                : T("No blocking problem: see the warnings in the table below."));
        }

        return conclusions;
    }

    private static List<string> ConcludeAgent(IReadOnlyList<DiagCheck> checks)
    {
        DiagCheck? Get(string id) => checks.FirstOrDefault(c => c.Id == id);
        var notInstalled = Get("agent-installed")?.Status == "FAIL";
        var conclusions = new List<string>();

        var script = Get("customscript");
        if (notInstalled && script is { Status: "WARN" } && Regex.IsMatch(script.Text, "(?i)has expired"))
        {
            conclusions.Add(T("The script downloaded the agent but could not register it in Azure DevOps: access denied, and the message mentions an ") +
                            T("expired token (PAT). Renew a token with the Agent Pools (read & manage) permission and update it where the script reads it ") +
                            T("(pipeline variable, Key Vault or extension setting)."));
        }
        else if (notInstalled && script is { Status: "WARN" } && Regex.IsMatch(script.Text, "(?i)needs Manage permissions"))
        {
            conclusions.Add(T("The script downloaded the agent and the token was accepted, but Azure DevOps refuses the registration: the user who owns ") +
                            T("the token (the name is in the message below) does not have the Manage permission on the pool. They must be added as a pool administrator ") +
                            T("(Azure DevOps → Organization settings → Agent pools → the pool → Security), or the PAT must be created by a user who is."));
        }
        else if (notInstalled && script is { Status: "WARN" } &&
            Regex.IsMatch(script.Text, "(?i)Access Denied|Unauthorized|TF400813|\\b401\\b|\\b403\\b"))
        {
            conclusions.Add(T("The script downloaded the agent but could not register it in Azure DevOps: access denied. Check that the token used ") +
                            T("is valid, has the Agent Pools (read & manage) permission and belongs to a user with the Manage permission on the pool."));
        }
        else if (notInstalled && script is { Status: "WARN" } &&
            Regex.IsMatch(script.Text, "(?i)resolve host|Name or service not known|Could not resolve"))
        {
            conclusions.Add(T("The agent is not installed and the last custom script could not download it: the name it downloads from does not resolve. ") +
                            T("If it uses vstsagentpackage.azureedge.net, that address has been retired: replace it with download.agent.dev.azure.com."));
        }
        else if (notInstalled)
        {
            conclusions.Add(T("The agent is not installed on this VM: until it is installed and registered in the pool, jobs that need it stay queued."));
        }

        if (Get("agent-service")?.Status == "WARN" && Get("agent-listener")?.Status == "FAIL")
        {
            conclusions.Add(T("The agent is installed but is not running and is not a service: it will not restart after a reboot. Install it as a service (svc.sh install, svc.sh start)."));
        }
        else if (Get("agent-service")?.Status == "FAIL")
        {
            conclusions.Add(T("The agent service is installed but stopped: start it (svc.sh start) and check the agent log."));
        }

        var devOps = Get("net-devops");
        if (devOps is { Status: "FAIL" })
        {
            conclusions.Add(T("The VM cannot reach Azure DevOps (dev.azure.com): check DNS, network rules and firewall. Without this the agent cannot receive jobs."));
        }

        var download = Get("net-download");
        if (notInstalled && download is { Status: "FAIL" })
        {
            conclusions.Add(T("The new agent download address is not reachable from the VM: allow download.agent.dev.azure.com in the firewall."));
        }

        var worker = Get("agent-worker");
        if (worker is { Text: var t } && t.StartsWith(T("A job has been running for {0}.", "").TrimEnd('.')) && Regex.IsMatch(t, @"(\d+-\d{2}:|\b[1-9]\d*\.\d{2}:|\b(0[6-9]|[1-9]\d):\d{2}:\d{2}|\.(0[6-9]|[1-9]\d):\d{2}:\d{2})"))
        {
            conclusions.Add(T("A job has been running for a long time: if unexpected it may be stuck, and it keeps the agent busy."));
        }

        var log = Get("agent-log");
        if (log is { Status: "WARN" } && Regex.IsMatch(log.Text, @"(?i)\b401\b|unauthorized|authentication failed"))
        {
            conclusions.Add(T("The agent log reports authentication errors (401). If the agent shows as offline in Azure DevOps, or jobs do not start, ") +
                            T("the credentials it is registered with may no longer be valid: in that case it must be registered again. ") +
                            T("If the agent is online and working, the warnings may be temporary."));
        }

        if (conclusions.Count == 0 && checks.Count > 0 && checks.All(c => c.Status is "OK" or "INFO"))
        {
            conclusions.Add(T("No problems found: the agent is installed, running and reaches Azure DevOps."));
        }
        else if (conclusions.Count == 0 && checks.Count > 0)
        {
            conclusions.Add(T("No blocking problem in installation, service or network: see the warnings in the table below."));
        }

        return conclusions;
    }

    // ---- scripts ------------------------------------------------------------------------------------------------------
    // Every line is "CHECK|id|STATUS|detail": facts only (no accents or markdown), pipes replaced, secrets stripped.
    // A script is a header (helpers), the blocks every profile runs, the agent block (agent profile only) and the network checks.

    private static string LinuxScript(DiagnosticProfile profile)
    {
        var agent = profile == DiagnosticProfile.AzureDevOpsAgent;
        return string.Join("\n",
            LinuxHeader,
            LinuxSystem,
            agent ? LinuxAgent : "",
            agent ? LinuxNetAgent : LinuxNetGeneral,
            LinuxCustomScript,
            agent ? LinuxTools : "");
    }

    private static string WindowsScript(DiagnosticProfile profile)
    {
        var agent = profile == DiagnosticProfile.AzureDevOpsAgent;
        return string.Join("\n",
            WindowsHeader,
            WindowsSystem,
            agent ? WindowsAgent : "",
            agent ? WindowsNetAgent : WindowsNetGeneral,
            WindowsCustomScript,
            agent ? WindowsTools : "");
    }

    private const string LinuxHeader = """"
#!/bin/bash
san() { tr '\r\n' '  ' | tr '|' '/' | sed -E 's/(token|password|passwd|secret|pat|key)([=: ]+)[^ ]+/\1\2[redatto]/Ig' | cut -c1-320; }
chk() { printf 'CHECK|%s|%s|%s\n' "$1" "$2" "$(printf '%s' "$3" | san)"; }
net() {
  ip=$(getent hosts "$1" 2>/dev/null | awk '{print $1}' | head -1)
  if [ -z "$ip" ]; then chk "$2" "$3" "unresolved:$1"; return; fi
  code=$(curl -s -o /dev/null -m 10 -w '%{http_code}' "https://$1/" 2>/dev/null)
  if [ -z "$code" ] || [ "$code" = "000" ]; then chk "$2" "$3" "https-fail:$1 ($ip)"; else chk "$2" OK "reach:$1 - $ip - HTTP $code"; fi
}
"""";

    private const string LinuxSystem = """"
. /etc/os-release 2>/dev/null
chk system INFO "$(hostname) - ${PRETTY_NAME:-Linux} - boot $(uptime -s 2>/dev/null) - load $(cut -d' ' -f1-3 /proc/loadavg)"
use=$(df / | awk 'NR==2{gsub("%","",$5); print $5}')
st=OK; [ "${use:-0}" -ge 90 ] && st=WARN
chk disk "$st" "$(df -h / | awk 'NR==2{print $5" <<L:used>>, "$4" <<L:free>>"}')"
"""";

    private const string LinuxAgent = """"
AGENTS=$(find /home /opt /srv /root /var/lib/waagent -maxdepth 6 -name .agent -type f 2>/dev/null | head -3)
if [ -z "$AGENTS" ]; then
  chk agent-installed FAIL "none"
else
  for f in $AGENTS; do
    chk agent-installed OK "$(dirname "$f"); $(grep -o -E '"(agentName|poolName|poolId|serverUrl)": *"?[^",}]*' "$f" | tr -d '"' | tr '\n' ';')"
  done
fi

SVC=$(systemctl list-units --type=service --all --no-legend 2>/dev/null | grep -i -E 'vsts\.agent|azpipelines|vstsagent' | sed 's/^[^a-zA-Z]*//')
if [ -n "$SVC" ]; then
  echo "$SVC" | while read -r unit load active sub rest; do
    st=OK; [ "$active" != "active" ] && st=FAIL
    chk agent-service "$st" "$unit; $active/$sub"
  done
elif [ -n "$AGENTS" ]; then
  chk agent-service WARN "no-service"
else
  chk agent-service INFO "n/a"
fi

LST=$(ps -eo etime,args | grep -E 'Agent\.Listener' | grep -v grep | head -1 | awk '{print $1}')
WRK=$(ps -eo etime,args | grep -E 'Agent\.Worker' | grep -v grep | head -1 | awk '{print $1}')
if [ -n "$LST" ]; then chk agent-listener OK "$LST"; elif [ -n "$AGENTS" ]; then chk agent-listener FAIL "stopped"; else chk agent-listener INFO "n/a"; fi
if [ -n "$WRK" ]; then chk agent-worker INFO "$WRK"; else chk agent-worker INFO "idle"; fi

for f in $AGENTS; do
  DIAG="$(dirname "$f")/_diag"
  [ -d "$DIAG" ] || continue
  LASTW=$(ls -t "$DIAG"/Worker_*.log 2>/dev/null | head -1)
  [ -n "$LASTW" ] && chk agent-lastjob INFO "$(basename "$LASTW"); $(date -u -r "$LASTW" '+%Y-%m-%d %H:%M')Z"
  RUN=$(ls -t "$DIAG"/Runner_*.log "$DIAG"/Agent_*.log 2>/dev/null | head -1)
  if [ -n "$RUN" ]; then
    ERR=$(tail -n 300 "$RUN" | grep -i -E 'error|exception|unauthorized|forbidden|failed' | tail -3)
    if [ -n "$ERR" ]; then chk agent-log WARN "$ERR"; else chk agent-log OK "none"; fi
  fi
done
"""";

    private const string LinuxNetAgent = """"
net dev.azure.com net-devops FAIL
net download.agent.dev.azure.com net-download FAIL
net vstsagentpackage.azureedge.net net-old INFO
"""";

    private const string LinuxNetGeneral = """"
net management.azure.com net-arm FAIL
net login.microsoftonline.com net-login FAIL
"""";

    private const string LinuxCustomScript = """"
CS=$(ls -dt /var/lib/waagent/custom-script/download/* 2>/dev/null | head -1)
if [ -n "$CS" ] && [ -f "$CS/stderr" ]; then
  CSE=$(tr -d '\r' < "$CS/stderr" | grep -i -E 'error|fail|unable|not found|cannot|denied|no such|resolve|expired' | tail -4)
  WHEN=$(date -u -r "$CS/stderr" '+%Y-%m-%d')
  if [ -n "$CSE" ]; then chk customscript WARN "[log del $WHEN] $CSE"; else chk customscript OK "none; log del $WHEN"; fi
fi
"""";

    private const string LinuxTools = """"
T=""
for t in kubectl helm az kubelogin docker; do
  if command -v $t >/dev/null 2>&1; then T="$T $t=yes"; else T="$T $t=no"; fi
done
chk tools INFO "$T"
"""";

    // Custom-script log: sections start with "@@SECTION|title", everything after is the (cleaned) tail of a file.
    private const string LinuxScriptLog = """"
#!/bin/bash
san() { tr -d '\r' | sed -E 's/(--token|--password|--pass)[ =]+[^ ]+/\1 [redatto]/Ig; s/(token|password|passwd|secret|pat|key)([=: ]+)[^ ]+/\1\2[redatto]/Ig; s/\b[a-z0-9]{52}\b/[redatto]/g' | cut -c1-400; }
# The run-command returns about 4 KB at most (and keeps the end): few lines, wget's progress bars removed, the most
# interesting file (stdout) last.
noprogress() { grep -v -E '^ *[0-9]+K [. ]+|^ *\.+ +[0-9]+%' ; }
H=/var/log/azure/custom-script/handler.log
if [ -f "$H" ]; then
  echo "@@SECTION|handler.log - <<L:modified>> $(date -u -r "$H" '+%Y-%m-%d %H:%M:%S')Z (<<L:last lines>>)"
  tail -n 8 "$H" | san | cut -c1-200 | tail -c 900
fi
CS=$(ls -dt /var/lib/waagent/custom-script/download/* 2>/dev/null | head -1)
if [ -z "$CS" ]; then
  echo "@@SECTION|<<L:No custom script run found>>"
  echo "<<L:The folder {0} does not exist or is empty.|/var/lib/waagent/custom-script/download>>"
else
  if [ -f "$CS/stderr" ]; then
    echo "@@SECTION|stderr - <<L:last run, modified>> $(date -u -r "$CS/stderr" '+%Y-%m-%d %H:%M:%S')Z (<<L:last lines>>)"
    tail -n 12 "$CS/stderr" | noprogress | san | cut -c1-200 | tail -c 700
  fi
  if [ -f "$CS/stdout" ]; then
    echo "@@SECTION|stdout - <<L:last run, modified>> $(date -u -r "$CS/stdout" '+%Y-%m-%d %H:%M:%S')Z (<<L:last lines, without the download progress bar>>)"
    noprogress < "$CS/stdout" | tail -n 30 | san | cut -c1-200 | tail -c 2200
  fi
fi
"""";

    private const string WindowsScriptLog = """"
function Clean([string]$s) {
  $t = ($s -replace '[\r\n]+', ' ') -replace '(?i)(--token|--password|--pass)[ =]+\S+', '$1 [redatto]' -replace '(?i)(token|password|secret|pat|key)([=: ]+)\S+', '$1$2[redatto]' -replace '\b[a-z0-9]{52}\b', '[redatto]'
  if ($t.Length -gt 400) { $t.Substring(0, 400) } else { $t.Trim() }
}
$h = Get-ChildItem 'C:\WindowsAzure\Logs\Plugins\Microsoft.Compute.CustomScriptExtension' -Recurse -Filter 'CustomScriptHandler.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($h) {
  "@@SECTION|CustomScriptHandler.log - <<L:modified>> $($h.LastWriteTime.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))Z (<<L:last 80 lines>>)"
  Get-Content $h.FullName -Tail 80 | ForEach-Object { Clean $_ }
} else {
  "@@SECTION|<<L:No custom script log found>>"
  '<<L:The folder {0} does not exist.|C:\WindowsAzure\Logs\Plugins\Microsoft.Compute.CustomScriptExtension>>'
}
"""";

    private const string WindowsHeader = """"
function Clean([string]$s) {
  $t = ($s -replace '[\r\n|]+', ' ') -replace '(?i)(token|password|secret|pat|key)([=: ]+)\S+', '$1$2[redatto]'
  if ($t.Length -gt 320) { $t.Substring(0, 320) } else { $t.Trim() }
}
function Chk($id, $status, $detail) { "CHECK|$id|$status|$(Clean ([string]$detail))" }
function Net($hostName, $id, $failStatus) {
  $ip = $null
  try { $ip = (Resolve-DnsName $hostName -Type A -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -First 1).IPAddress } catch { }
  if (-not $ip) { Chk $id $failStatus "unresolved:$hostName"; return }
  # A plain TCP connection to port 443 decides: Invoke-WebRequest in Windows PowerShell 5.1 can fail on TLS settings
  # even where the agent itself connects without trouble.
  $tcp = New-Object System.Net.Sockets.TcpClient
  $open = $false
  try { $open = $tcp.BeginConnect($ip, 443, $null, $null).AsyncWaitHandle.WaitOne(8000) -and $tcp.Connected } catch { } finally { $tcp.Close() }
  if ($open) { Chk $id OK "reach:$hostName - $ip - TCP 443" } else { Chk $id $failStatus "https-fail:$hostName ($ip)" }
}
"""";

    private const string WindowsSystem = """"
$os = Get-CimInstance Win32_OperatingSystem
Chk system INFO "$env:COMPUTERNAME - $($os.Caption) - boot $($os.LastBootUpTime.ToUniversalTime().ToString('yyyy-MM-dd HH:mm'))Z"
$c = Get-PSDrive C
$freePct = [math]::Round(100 * $c.Free / ($c.Free + $c.Used))
Chk disk $(if ($freePct -lt 10) { 'WARN' } else { 'OK' }) "$freePct% liberi ($([math]::Round($c.Free / 1GB, 1)) GB)"
"""";

    private const string WindowsAgent = """"
$listener = Get-Process Agent.Listener -ErrorAction SilentlyContinue | Select-Object -First 1
$roots = @('C:\agent', 'C:\azagent', 'C:\vsts', 'D:\agent', 'D:\azagent')
$roots += @(Get-ChildItem 'C:\Packages\Plugins' -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
if ($listener) { $roots = @(Split-Path (Split-Path $listener.Path)) + $roots }
$agentFiles = @($roots | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -Filter .agent -Recurse -Depth 4 -Force -ErrorAction SilentlyContinue } | Sort-Object FullName -Unique | Select-Object -First 3)
if ($agentFiles.Count -eq 0) {
  Chk agent-installed FAIL 'none'
} else {
  foreach ($f in $agentFiles) {
    $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
    Chk agent-installed OK "$($f.DirectoryName); agentName=$($j.agentName); poolName=$($j.poolName); poolId=$($j.poolId); serverUrl=$($j.serverUrl)"
  }
}

$svc = @(Get-Service -Name 'vstsagent*' -ErrorAction SilentlyContinue)
if ($svc.Count -gt 0) {
  foreach ($s in $svc) { Chk agent-service $(if ($s.Status -eq 'Running') { 'OK' } else { 'FAIL' }) "$($s.Name); $($s.Status)" }
} elseif ($agentFiles.Count -gt 0) {
  Chk agent-service WARN 'no-service'
} else {
  Chk agent-service INFO 'n/a'
}

if ($listener) { Chk agent-listener OK ("{0:d\.hh\:mm\:ss}" -f ((Get-Date) - $listener.StartTime)) }
elseif ($agentFiles.Count -gt 0) { Chk agent-listener FAIL 'stopped' }
else { Chk agent-listener INFO 'n/a' }
$worker = Get-Process Agent.Worker -ErrorAction SilentlyContinue | Select-Object -First 1
if ($worker) { Chk agent-worker INFO ("{0:d\.hh\:mm\:ss}" -f ((Get-Date) - $worker.StartTime)) } else { Chk agent-worker INFO 'idle' }

foreach ($f in $agentFiles) {
  $diag = Join-Path $f.DirectoryName '_diag'
  if (-not (Test-Path $diag)) { continue }
  $lastW = Get-ChildItem $diag -Filter 'Worker_*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  if ($lastW) { Chk agent-lastjob INFO "$($lastW.Name); $($lastW.LastWriteTime.ToUniversalTime().ToString('yyyy-MM-dd HH:mm'))Z" }
  $run = Get-ChildItem $diag -Include 'Runner_*.log', 'Agent_*.log' -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  if ($run) {
    $err = @(Get-Content $run.FullName -Tail 300 | Select-String -Pattern 'error|exception|unauthorized|forbidden|failed' | Select-Object -Last 3 | ForEach-Object { $_.Line })
    if ($err.Count -gt 0) { Chk agent-log WARN ($err -join ' ') } else { Chk agent-log OK 'none' }
  }
}
"""";

    private const string WindowsNetAgent = """"
Net 'dev.azure.com' 'net-devops' 'FAIL'
Net 'download.agent.dev.azure.com' 'net-download' 'FAIL'
Net 'vstsagentpackage.azureedge.net' 'net-old' 'INFO'
"""";

    private const string WindowsNetGeneral = """"
Net 'management.azure.com' 'net-arm' 'FAIL'
Net 'login.microsoftonline.com' 'net-login' 'FAIL'
"""";

    private const string WindowsCustomScript = """"
$csLog = Get-ChildItem 'C:\WindowsAzure\Logs\Plugins\Microsoft.Compute.CustomScriptExtension' -Recurse -Filter 'CustomScriptHandler.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($csLog) {
  # Specific patterns only: the handler log is chatty ("Waiting for pending data from output and error streams").
  $cse = @(Select-String -Path $csLog.FullName -Pattern '\[ERROR\]|failed|exit code:? *[1-9]|could not resolve|denied|expired' | Select-Object -Last 4 | ForEach-Object { $_.Line })
  $when = $csLog.LastWriteTime.ToUniversalTime().ToString('yyyy-MM-dd')
  if ($cse.Count -gt 0) { Chk customscript WARN "[log del $when] $($cse -join ' ')" } else { Chk customscript OK "none; log del $when" }
}
"""";

    private const string WindowsTools = """"
$tools = foreach ($t in 'kubectl', 'helm', 'az', 'kubelogin', 'docker') { if (Get-Command $t -ErrorAction SilentlyContinue) { "$t=yes" } else { "$t=no" } }
Chk tools INFO ($tools -join ' ')
"""";
}
