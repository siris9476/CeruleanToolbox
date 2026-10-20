# Pre-release checklist

[Italiano](CHECKLIST.it.md)

Status as of 20/10/2026. Legend: **OK** = tried in the way stated; **To try** = not tried at the step that matters; **Blocking** = must be fixed before a general release.
Rule: checks with writes are done in a separate test subscription, with a service principal limited to it.

## 1. Code

| Item | Where | Status |
|---|---|---|
| `DEBUG-TEST` blocks: the delete/revoke does nothing, it only prints a message. They stay switched off on purpose and are declared in the README until someone tries them on real resources. | `AppConfigurationService.DeleteSettingAsync`; `BastionService.DeleteShareableLinkAsync`; `ComputeExtrasService.DeleteManagedDiskAsync` and `DeleteSnapshotAsync`; `ContainerRegistryService.DeleteTagAsync` and `DeleteRepositoryAsync`; `KeyVaultCertificateService.DeleteCertificateAsync`; `NetworkingService.DeleteNsgRuleAsync`; `RoleAssignmentService.DeleteAssignmentAsync`; `SqlManagementService.DeleteFirewallRuleAsync` | Declared (10 places) |
| After removing a block, try the delete on a throwaway resource | the same pages | To try |
| Delete buttons: typed confirmation before the action | all pages above | To try after re-enabling |

## 2. Connections and secrets

| Item | Status |
|---|---|
| Saving a connection with verification (tenant, reachable subscriptions) | To try with a new secret |
| Changing the active connection: the header and pages follow; the saved page state is reset | Saved state: **OK** only on Query and on the Key Vault filter (web) |
| Deleting a saved connection (typed confirmation; deleting the active one leaves none active) | **OK** on a demo store of made-up connections |
| Export / Import `.ceruleantoolbox` with a passphrase | To try on another machine or another Windows user |
| The secret does not appear in logs, pages or export files without the passphrase | To try |

## 3. Reads (low risk: sample)

Try with an empty subscription, with a single one and with many (the Console already showed that 127 subscriptions are slow).

Resources, Query, Virtual Machine (overview, disks, extensions, events), App Service, Application Gateway, AKS, Storage, SQL, App Insights, Log Analytics, Automation, Monitoring, Identity, Activity log, Networking (read only), Home.

| Item | Status |
|---|---|
| Activity log, VM overview, agent diagnostics (Linux and Windows) | **OK** in the browser on real sandbox VMs; desktop window not looked at |
| PowerShell console (Subscription, Use-Aks, loading with Resource Graph) | **OK** with the test connection; Subscription menu restored |
| Other read-only pages | To try, as a sample |

## 4. Writes and destructive actions (high risk: all to try in a test subscription)

| Feature | Status |
|---|---|
| Function App: Start/Stop/Restart (typed confirmation, in the header and in the list) | Confirmation **OK** in testing; real execution not repeated now |
| Function App: "Key Vault references" tab, refresh (documented) | **OK**, tried by the author |
| Key Vault: update a secret, versions (restore/disable), Deleted (recover, purge) | Reads **OK**; writes not all tried to the end |
| Front Door/CDN: cache purge from the Endpoint table | Not confirmed from the app |
| VM: Start/Deallocate/Restart | To try |
| Deploy (ARM templates), Role assignments (create/delete), NSG and SQL firewall rules, ACR (tag/repository), disks and snapshots, Bastion (shareable links), App Configuration | To try, after removing the `DEBUG-TEST` blocks |

## 5. Permissions and errors

| Item | Status |
|---|---|
| Read-only service principal: every action fails with a readable message, not with an exception | To try |
| Service principal with a single resource group | To try |
| Expired secret or wrong tenant: a clear message | To try |
| No active connection: every page says so | To look at |

## 6. The two apps and the desktop

| Item | Status |
|---|---|
| Build of the whole solution | **OK** |
| Web CeruleanToolbox (5166): the main pages answer | **OK** |
| Language switch (English/Italian) on web and desktop | To try after the translation of the interface |
| Desktop app: start, menu, recent pages (Function App log and Key Vault refresh, Deleted, Front Door purge, Agents) | To try; the window was minimized during the checks |
| Clean install on another PC (WebView2, .NET, data path) | To try |
| WebView2 debug port not left open | **OK** (restarted without the port) |

## 7. Before distributing

- Documentation (README, minimum permissions of the service principal for every page).
- Version and package names, licenses of the dependencies.
- The desktop error log (`desktop.log`) checked after the round of checks.
