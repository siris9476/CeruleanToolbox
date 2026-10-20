# Checklist prima di pubblicare

Stato al 20/10/2026. Legenda: **OK** = provato nel modo indicato; **Da provare** = non provato nel passo che conta; **Bloccante** = va sistemato prima di pubblicare.
Regola: le prove con scritture si fanno in una subscription separata di collaudo, con un service principal limitato a quella.

## 1. Codice

| Cosa | Dove | Stato |
|---|---|---|
| Blocchi DEBUG-TEST: la cancellazione/revoca non fa nulla, stampa solo un messaggio. Vanno tolti (e il `return;` con il codice reale sotto riattivato). | `AppConfigurationService.DeleteSettingAsync`; `BastionService.DeleteShareableLinkAsync`; `ComputeExtrasService.DeleteManagedDiskAsync` e `DeleteSnapshotAsync`; `ContainerRegistryService.DeleteTagAsync` e `DeleteRepositoryAsync`; `KeyVaultCertificateService.DeleteCertificateAsync`; `NetworkingService.DeleteNsgRuleAsync`; `RoleAssignmentService.DeleteAssignmentAsync`; `SqlManagementService.DeleteFirewallRuleAsync` | Dichiarato nel README (10 punti): restano disattivati di proposito |
| Dopo averli tolti, provare ogni cancellazione su una risorsa usa e getta | stesse pagine | Da provare |
| Pulsanti di cancellazione: conferma digitata prima dell'azione | tutte le pagine sopra | Da provare dopo la riattivazione |

## 2. Connessioni e segreti

| Cosa | Stato |
|---|---|
| Salvataggio di una connessione con verifica (tenant, subscription raggiunte) | Da provare con un secret nuovo |
| Cambio connessione attiva: l'intestazione e le pagine seguono; lo stato salvato delle pagine si azzera | Stato salvato: **OK** solo su Query e sul filtro di Key Vault (web) |
| Eliminazione di una connessione salvata (conferma digitata; eliminare quella attiva lascia nessuna attiva) | **OK** su un archivio di prova con connessioni inventate |
| Esporta / Importa `.ceruleantoolbox` con passphrase | Da provare su un'altra macchina o un altro utente Windows |
| Il segreto non compare in log, pagine o file di esportazione senza passphrase | Da provare |

## 3. Letture (rischio basso: campione)

Provare con subscription vuota, con una sola e con molte (la Console ha già mostrato che 127 subscription rallentano).

Risorse, Query, Virtual Machine (panoramica, dischi, estensioni, eventi), App Service, Application Gateway, AKS, Storage, SQL, App Insights, Log Analytics, Automation, Monitoring, Identity, Registro attività, Networking (sola lettura), Home.

| Cosa | Stato |
|---|---|
| Registro attività, panoramica VM, diagnostica agente (Linux e Windows) | **OK** nel browser su VM reali della sandbox; finestra desktop non guardata |
| Console PowerShell (Subscription, Use-Aks, caricamento con Resource Graph) | **OK** con la connessione di collaudo; menu Subscription ripristinato |
| Altre pagine di sola lettura | Da provare a campione |

## 4. Scritture e azioni distruttive (rischio alto: tutte da provare in collaudo)

| Funzione | Stato |
|---|---|
| Function App: Start/Stop/Restart (con conferma digitata, in intestazione e in elenco) | Conferma **OK** in prova; esecuzione reale non ripetuta ora |
| Function App: "Riferimenti Key Vault" (refresh documentato) | **OK** diagnosi sul caso reale; da ripetere |
| Key Vault: aggiorna segreto, versioni (ripristina/disabilita), Eliminati (recupera, purge) | Letture **OK**; scritture non tutte provate fino in fondo |
| Front Door/CDN: purge cache dalla tabella Endpoint | **OK** (purge in produzione accettato, provato in sandbox) |
| VM: Start/Deallocate/Restart | Da provare |
| Deploy (modelli ARM), Role assignments (crea/elimina), regole NSG e firewall SQL, ACR (tag/repository), dischi e snapshot, Bastion (link condivisi), App Configuration | Da provare, dopo aver tolto i DEBUG-TEST |

## 5. Permessi e errori

| Cosa | Stato |
|---|---|
| Service principal di sola lettura: ogni azione fallisce con un messaggio leggibile, non con un'eccezione | Da provare |
| Service principal con un solo gruppo di risorse | Da provare |
| Segreto scaduto o tenant sbagliato: messaggio chiaro | Da provare |
| Nessuna connessione attiva: ogni pagina lo dice | Da guardare |

## 6. Le due app e il desktop

| Cosa | Stato |
|---|---|
| Compilazione di tutta la soluzione | **OK** (0 errori, 20/10/2026) |
| Web CeruleanToolbox (5166): pagine principali rispondono | **OK** |
| App desktop: avvio, menu, pagine recenti (Function App log e Key Vault refresh, Eliminati, Front Door purge, Agenti) | Da provare, finestra ridotta a icona durante le prove |
| Installazione pulita su un altro PC (WebView2, .NET, percorso dei dati) | Da provare |
| Porta di debug del WebView2 non lasciata aperta | **OK** (riavvio senza porta) |

## 7. Prima di distribuire

- Documentazione (README, permessi minimi del service principal per ogni pagina).
- Versione e nome dei pacchetti, licenze delle dipendenze.
- Registro errori del desktop (`desktop.log`) controllato dopo il giro di prove.
