# CeruleanToolbox

*A toolbox for Microsoft Azure.*

[English](README.md)

Web app locale (.NET 8 Blazor Server) per eseguire operazioni su Azure usando
le credenziali di un **App Registration** di Microsoft Entra ID (service
principal: Tenant ID, Client ID, Client Secret).

CeruleanToolbox è un progetto open source indipendente. Non è affiliato, approvato né sponsorizzato da Microsoft. Microsoft, Azure, Microsoft Entra e Azure DevOps sono marchi del gruppo di aziende Microsoft.

Questo repository contiene **CeruleanToolbox** (web e desktop Windows) e la libreria
che usa, `src/Toolbox.Core`: connessioni salvate, client Azure, diagnostica delle VM.

L'interfaccia è disponibile in **inglese** e in **italiano**: scegli la lingua
dal selettore in alto nella pagina (al primo avvio si usa la lingua di Windows).
Il codice e la documentazione principale sono in inglese; i testi italiani sono
in `Locales/it.json` di ogni progetto. Questa versione del README è la
traduzione italiana di [README.md](README.md).

**Traduzioni:** ogni testo nel codice è scritto in inglese come `T("Testo")`
(oppure `T("Caricati {0} elementi", n)`), e `Locales/<codice>.json` associa il
testo inglese alla traduzione. Un testo senza voce si vede in inglese, quindi una
traduzione può essere parziale. Per aggiungere una lingua: il codice e il nome in
`Tr.Languages` (`src/Toolbox.Core/Localization/Tr.cs`) e un file `<codice>.json`
nella cartella `Locales` di ogni progetto. I file italiani sono stati controllati
confrontando ogni pagina della versione precedente, solo in italiano, con quella
nuova.

## Schermate

Le schermate usano una connessione inventata ("Contoso") e mostrano le pagine prima di scegliere una subscription, quindi non contengono nessun tenant, subscription o risorsa reale. L'interfaccia è mostrata in inglese.

![Home: le sezioni dell'app e la connessione attiva](docs/images/home.png)

*Home: le sezioni dell'app, il selettore di lingua e la connessione attiva.*

![Connessioni: service principal salvati](docs/images/connections.png)

*Connessioni: verifica e salva i service principal, attiva o disattiva una connessione, mostra il secret, esportalo.*

![Query con Resource Graph](docs/images/query.png)

*Query: Azure Resource Graph (KQL) sulle subscription raggiungibili dalla connessione.*

![Console PowerShell](docs/images/console.png)

*Console PowerShell: un terminale vero con il service principal attivo, con comandi di aiuto per `kubectl` e `helm` su AKS.*

## Stato del progetto

Il progetto è nelle prime fasi e finora l'ha provato solo l'autore. Sviluppo e verifiche sono stati quasi sempre in sola lettura o su risorse di una sandbox. Ecco cosa manca:

- **10 cancellazioni/revoche sono disattivate** e non sono mai state provate su
  risorse reali. Il codice vero c'è, ma è sotto un blocco `DEBUG-TEST` che
  stampa un messaggio e basta. Cercale con `grep -rn "DEBUG-TEST" src`:
  - App Configuration: impostazione (`DeleteSettingAsync`)
  - Bastion: link condiviso (`DeleteShareableLinkAsync`)
  - Compute Extra: disco gestito e snapshot (`DeleteManagedDiskAsync`, `DeleteSnapshotAsync`)
  - Container Registry: tag e repository (`DeleteTagAsync`, `DeleteRepositoryAsync`)
  - Key Vault: certificato (`DeleteCertificateAsync`)
  - Networking: regola NSG (`DeleteNsgRuleAsync`)
  - Ruoli: assegnazione (`DeleteAssignmentAsync`)
  - SQL: regola firewall (`DeleteFirewallRuleAsync`)
- **Provate a metà:** le azioni che scrivono (avvio, arresto, riavvio, purge, aggiornamento di segreti) sono state controllate fino al passo prima del salvataggio. Non tutte sono state eseguite fino in fondo su risorse reali.
- **Non provate ancora:**
  - lo svuotamento della cache di Front Door/CDN dalla tabella Endpoint del
    profilo (l'autore ne ha autorizzato l'uso, ma l'esecuzione dall'app non è
    confermata);
  - in Key Vault, gli elenchi di segreti, chiavi, certificati ed eliminati e le
    versioni dei segreti sui dati reali;
  - l'installazione su un altro PC;
  - il comportamento con un service principal a permessi ridotti;
  - nella pagina VM, la colonna «Ultima disponibile» delle estensioni (letta dal
    catalogo delle estensioni della regione);
  - copia e incolla nella console PowerShell (selezione, `Ctrl+C`, `Ctrl+V`,
    tasto destro);
  - nella pagina Container Registry, i «Dettagli» di un tag (sistema, dimensione,
    comando di avvio, etichette, variabili d'ambiente con i valori nascosti se il
    nome fa pensare a un segreto, cronologia di costruzione e un'ipotesi sul
    runtime): sola lettura, provati solo su manifest di esempio, mai su un
    registry reale;
  - nella pagina Container Registry, la scheda Firewall: aggiunta e rimozione
    delle regole IP (attiva, ma mai eseguita su un registry reale; riscrive
    l'intero elenco delle regole del registry, quindi provala prima su un
    registry di prova);
  - la finestra desktop per le funzioni più recenti.

Le prove complete sono in [CHECKLIST.it.md](CHECKLIST.it.md).

### Cosa è stato verificato

Ecco cosa **è** stato provato, sugli ambienti di sviluppo dell'autore e su una sandbox (le prove che scrivono sono elencate a parte):

- **Pagine di consultazione delle risorse** (AKS, Virtual Machine, App Service,
  Function App, SQL, Storage, Key Vault, Container Registry, App Configuration,
  Application Gateway): le panoramiche leggono le risposte ARM reali, esaminate
  prima in sola lettura su un ambiente vero; l'app le mostra anche con campi
  mancanti o valori predefiniti.
- **Registro attività e dettaglio VM:** verificati nel browser su VM reali di una
  sandbox (disco, estensioni con stato ed errore, eventi recenti, chi ha fatto
  cosa). Non aperti nella finestra desktop. La tabella delle estensioni mostra la
  versione in esecuzione accanto a quella impostata nella definizione della VM e
  se l'aggiornamento automatico è attivo; l'autore l'ha vista su una VM di una
  sandbox, nella finestra desktop.
- **Diagnostica delle VM** (Linux e Windows): disco, rete verso Azure e ultimo
  custom script, provata su due VM reali di una sandbox; legge e non modifica.
- **Console PowerShell:** una sessione reale (un terminale vero) nel browser e nella finestra desktop. `kubectl get nodes` e `helm list -A` hanno girato su un cluster di sviluppo tramite una VM agente, e la verifica di raggiungibilità del cluster dalla VM ha funzionato. L'elenco di cluster e VM viene da Resource Graph: con una connessione che vede 127 subscription, 5 cluster in circa 2,4 s e 121 VM in circa 0,6 s. L'autore ha provato da solo anche `helm upgrade`, `kubectl apply` e `az aks upgrade` dalla console: sono prove riferite dall'autore, non ricontrollate qui.
- **Function App:** i log delle invocazioni mostrano i conteggi degli errori e l'ultimo errore, e un errore di lettura di Application Insights (la colonna `success` è testo) è corretto. Su un'app reale lo stato dei Riferimenti Key Vault ha mostrato un caso di accesso negato per una versione disabilitata. L'autore ha provato la chiamata «Rileggi i riferimenti ora» (il refresh documentato da Microsoft). La conferma digitata per Stop e Riavvia (header ed elenco) è stata provata fino alla conferma.
- **Key Vault:** per «Aggiorna» e le altre scritture l'interfaccia è stata provata fino al salvataggio (pulsante disabilitato se vuoto, annulla), ma non il salvataggio vero.
- **Interfaccia:** la soluzione compila senza errori, anche da un clone pulito, e l'app web risponde su tutte le pagine. Lo stato di ogni pagina (selezione, filtri, elenchi caricati) resta quando cambi voce di menu: provato su Query e sul filtro di Key Vault. L'allineamento dell'intestazione è stato controllato nel browser.
- **Eliminazione di una connessione salvata:** provata su un archivio di prova con connessioni inventate: la conferma digitata resta disabilitata finché non scrivi il nome esatto, eliminare una connessione la toglie dal file e dall'elenco, ed eliminare quella attiva lascia nessuna connessione attiva. Non provata su un archivio di connessioni reali.
- **Interfaccia inglese/italiano:** la prima schermata di ciascuna delle 24 pagine legge in italiano lo stesso testo della versione precedente, solo italiana (0 righe diverse), e non mostra italiano in inglese. Il selettore di lingua funziona nell'app web e nella finestra desktop, e ogni `{0}` delle traduzioni corrisponde al testo inglese. **Non controllato:** le viste che si raggiungono solo scegliendo una risorsa o un tab (panoramiche, tabelle, messaggi di stato, errori) sono state convertite e controllate sul codice, ma non aperte una per una nelle due lingue: lì qualche parola potrebbe ancora essere da correggere. I testi che gli script di diagnostica scrivono sulle VM si traducono quando lo script parte; nessuno script è stato eseguito su una VM dopo la modifica.

### Come puoi aiutare

Vedi [CONTRIBUTING.md](CONTRIBUTING.md) (in inglese) per come compilare, provare in sicurezza e proporre modifiche, e [SECURITY.md](SECURITY.md) per segnalare un problema di sicurezza (senza aprire una issue pubblica).

Sei il benvenuto se vuoi **provarlo e dirci cosa non va**, o contribuire:

- Prova le azioni in una subscription di collaudo (mai in produzione per
  cominciare) e apri una issue con quello che hai visto, anche se tutto funziona:
  ci serve sapere cosa è stato verificato.
- Togli il blocco `DEBUG-TEST` di una cancellazione, provala su una risorsa usa e
  getta e proponi la modifica con una pull request, indicando cosa hai provato.
- Segnala permessi minimi che servono a una pagina, messaggi di errore poco
  chiari, traduzioni e pagine di Azure che mancano.

## Come funziona l'autenticazione

L'app si autentica con un normale service principal (`ClientSecretCredential`):

1. Crea o riusa un **App Registration** in **Microsoft Entra ID** e genera un
   **client secret** (Certificates & secrets → New client secret), oppure
   recupera Tenant ID/Client ID/Client Secret di un App Registration
   esistente.
2. Vai su **Connessioni** (`/connections`) nell'app, inserisci Tenant ID,
   Client ID e Client Secret, premi **Verifica connessione** (elenca i
   tenant e le subscription effettivamente raggiungibili con queste
   credenziali, confermando che sono corrette) e poi **Salva connessione**.

### Dove vengono salvate le credenziali

Il Connection Manager (`ConnectionManagerService`) salva le connessioni in
`%LOCALAPPDATA%\CeruleanToolbox\connections.json`. Il client secret è
cifrato con **Windows DPAPI** (`ProtectedData`, `DataProtectionScope.CurrentUser`):
il file cifrato è leggibile solo da un processo che gira sotto lo stesso
account Windows su questa stessa macchina. Copiarlo su un altro PC o farlo
leggere a un altro utente non basta per decifrarlo. Il file resta fuori dal
repository (è generato a runtime, non versionato). Puoi salvare più
connessioni e attivarne una alla volta dalla stessa pagina; ogni riga ha
anche un pulsante **Mostra secret** per rivedere in chiaro il client secret
salvato (es. per reinserirlo in un altro strumento), quindi evita di
lasciare l'app aperta su questa pagina su una macchina non fidata.

### Portare una connessione su un'altra macchina

Il file cifrato è legato al tuo account Windows e a questo PC, quindi copiare `connections.json` non basta. Per portare con te una connessione usa **Esporta** sulla sua riga in **Connessioni**: l'app chiede una passphrase (da scrivere due volte) e crea un file `.ceruleantoolbox` cifrato con essa (AES-256-GCM, chiave derivata dalla passphrase con PBKDF2-SHA256, 200.000 iterazioni). La versione web scarica il file dal browser; la versione desktop usa la finestra di salvataggio di Windows.

Sull'altra macchina apri **Connessioni**, vai su **Importa connessione**, scegli il file e scrivi la passphrase. I campi Nome, Tenant ID, Client ID e Client Secret in alto nella pagina vengono compilati; poi verifichi e salvi la connessione come al solito. Non viene importato nulla di nascosto.

Tieni il file e la passphrase separati e cancella il file quando hai finito: chi ha entrambi può leggere il client secret.

## Avvio

### Versione web (sviluppo)

```bash
cd src/CeruleanToolbox
dotnet run
```

L'app ascolta solo su `localhost` per default: non esporla su rete/Internet
senza aggiungere autenticazione, dato che detiene un service principal con
permessi su Azure.

### Versione desktop (installabile)

`src/CeruleanToolbox.Desktop` è un'app desktop Windows (WinForms +
[`BlazorWebView`](https://learn.microsoft.com/aspnet/core/blazor/hybrid/))
che ospita gli stessi componenti Razor **in-process**, tramite un canale a
messaggi verso il controllo WebView2: niente Kestrel, niente HTTP, nessuna
porta o IP coinvolti, a differenza della versione web (che resta Blazor
Server). Le pagine e i servizi (`Services/*.cs`) sono condivisi al 100% tra i
due progetti tramite `AddCeruleanToolboxServices()`
(`src/CeruleanToolbox/CeruleanToolboxServiceCollectionExtensions.cs`); l'unica
differenza reale è l'export/import delle connessioni, che nella versione
desktop usa le finestre di dialogo native di Windows invece del download dal
browser (vedi `IConnectionFileDialogService`).

```bash
cd src/CeruleanToolbox.Desktop
dotnet run
```

Oppure pubblica un eseguibile standalone:

```bash
cd src/CeruleanToolbox.Desktop
dotnet publish -c Release -r win-x64 --self-contained
```

Richiede il [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
(preinstallato su Windows 10/11 aggiornati insieme a Edge).

### Libreria condivisa

- `src/Toolbox.Core`: connessioni salvate, client Azure, helper ARM REST e
  diagnostica delle VM (`VmDiagnosticsService` + componente
  `VmDiagnosticsPanel`). Si registra con
  `AddToolboxCore()`.

La pagina Virtual Machine mostra la diagnostica generale (disco, rete verso
Azure, ultimo custom script). La pagina «Connessioni» è il componente
`ConnectionsView` di `Toolbox.Core`.

## Funzionalità

- **Connessioni** (`/connections`): Connection Manager: inserisci, verifica
  (tenant/subscription raggiungibili) e salva credenziali cifrate; gestisci
  più connessioni e passa dall'una all'altra (con una conferma), disattiva o elimina una connessione, ed esporta una connessione in un file protetto da passphrase per importarla su un'altra macchina.
- **Risorse** (`/resources`): Subscription e Resource Group entrambi
  opzionali: seleziona una subscription per vedere VM, App Service, cluster
  AKS, Application Gateway e l'inventario completo su **tutti** i resource
  group in una volta (con colonne Subscription e Resource Group), oppure
  lascia vuoto per vedere/filtrare su tutte le subscription raggiungibili.
  Ogni tabella ha filtri con ricerca testuale sui valori distinti per
  colonna. Azioni: start/stop/restart per VM e App Service, start/stop per
  AKS e Application Gateway. Per ogni VM, il pulsante "Dettagli" mostra
  size, OS, disco OS/data disk (nome e dimensione), NIC (IP privato/
  pubblico) ed estensioni installate (publisher, tipo, versione, stato di
  provisioning), con la possibilità di **ridimensionare** la VM scegliendo tra
  le size disponibili per quella location.
- **Bastion** (`/bastion`): elenco host Azure Bastion su tutta la
  subscription (SKU, DNS name, stato dello shareable link) e generazione di
  un **Bastion Shareable Link** per una VM: un URL che apre una sessione
  RDP/SSH nel browser senza passare dal portale Azure, utile a chi ha solo
  accesso in lettura sulla VM. Richiede un host Bastion Standard con lo
  shareable link abilitato. La creazione del link chiama direttamente
  l'endpoint REST di Azure (l'SDK .NET installato non lo espone ancora,
  solo lettura/revoca), mentre la revoca usa l'SDK.
- **Log Analytics** (`/loganalytics`): elenco workspace Log Analytics su tutta la
  subscription (retention, SKU, Workspace ID dietro toggle Mostra/Nascondi) e query
  KQL libere sui log di infrastruttura/piattaforma raccolti (`AzureActivity`,
  `Heartbeat`, `Perf`, `Syslog`/`Event`, diagnostic settings di altre risorse
  collegate al workspace). È complementare ad App Insights, che invece copre la
  telemetria applicativa. Stessi limiti di App Insights: query con timeout di 60s
  e risultati troncati a 200 righe mostrate. Include anche un elenco delle
  **Solutions legacy** (`Microsoft.OperationsManagement/solutions`, es. Update
  Management, Security & Audit) precedenti all'esperienza unificata di Azure
  Monitor.
- **Automation** (`/automation`): elenca i runbook di un Automation Account
  (tipo, stato, numero di job), avvia un job passando parametri opzionali
  (`chiave=valore`, una coppia per riga) e mostra la cronologia dei job
  recenti con stato, dettaglio errore e timestamp di creazione/avvio/fine.
- **Container Registry** (`/containerregistry`): elenco Container Registry (SKU,
  login server, stato Admin User), lettura/rigenerazione delle credenziali admin,
  navigazione repository/tag delle immagini pubblicate e stato delle
  geo-replication configurate. Un singolo tag o un intero repository si può
  **eliminare**, dietro una conferma digitata: è il caso d'uso più comune per la
  pulizia di immagini vecchie. La lettura e la cancellazione di repository e tag
  usano le API REST specifiche di ACR (`/v2/...` per la lettura, l'estensione
  `/acr/v1/...` per la cancellazione), che l'ARM SDK non copre perché gestisce
  solo la risorsa registry. Si autenticano con le credenziali admin: se l'Admin
  User è disabilitato, la pagina offre di abilitarlo con un click. Lo scambio di
  token AAD→ACR per l'accesso via RBAC senza credenziali admin non è
  implementato.
- **App Configuration** (`/appconfiguration`): elenco App Configuration store,
  lettura di tutte le coppie chiave/valore (con label), creazione/aggiornamento
  di una chiave e cancellazione. Richiede un ruolo dati esplicito sullo store
  (**App Configuration Data Owner** per scrivere, **Data Reader** per la sola
  lettura). È la stessa eccezione RBAC di Key Vault e Storage: vedi le Note più sotto.
- **Networking** (`/networking`): vista principalmente di sola lettura, a tab,
  sulle risorse di rete: Virtual Network (address space, subnet con relativa
  NSG), Network Security Group, Public IP (indirizzo, allocazione, risorsa
  associata risolta dall'IP configuration), Load Balancer (SKU, frontend IP,
  numero di backend pool/regole), Private Endpoint (VNet/subnet di
  appartenenza), Network Interface, Private DNS Zone (con virtual network
  link), Traffic Manager, Network Watcher, WAF Policy e CDN (profili + endpoint
  classici/Front Door). Le **regole di un NSG sono l'eccezione** alla sola
  lettura: dalla tab NSG, "Mostra regole" espande l'elenco (custom + default)
  e apre un form per creare una nuova regola, modificarne una esistente o
  eliminarla (le regole di default di Azure non sono modificabili/eliminabili,
  coerentemente con il comportamento del portale).
- **Compute Extra** (`/computeextras`): vista a tab su risorse compute/hosting
  complementari alle VM: VM Scale Set (capacità, modalità di orchestrazione,
  sola lettura), Managed Disk (evidenzia in giallo quelli non allegati a
  nessuna VM e ne permette l'**eliminazione**, oltre alla **creazione di uno
  snapshot** da qualunque disco), Snapshot (elenco con **eliminazione**), App
  Service Plan (SKU, numero di siti/worker; è il piano di hosting dietro le App
  Service già gestite in Risorse, sola lettura) e DevTest Lab Schedule
  (spegnimento automatico, sola lettura).
- **Monitoring** (`/monitoring`): vista di sola lettura, a tab, sulle risorse
  di configurazione di alerting/monitoraggio: Action Group, Metric Alert, Data
  Collection Rule, Web Test, Workbook, Smart Detector Alert Rule e automazioni
  di Microsoft Defender for Cloud. Complementare ad App Insights/Log
  Analytics, che invece interrogano i dati di telemetria veri e propri.
- **Identity** (`/identity`): vista di sola lettura su Managed Identity
  assegnate dall'utente (Principal ID/Client ID, utili per assegnare ruoli
  RBAC da Ruoli) e directory AAD B2C.
- **Deploy** (`/deploy`): distribuisci un template ARM JSON (o Bicep,
  compilato al volo con la CLI locale `bicep`) su un resource group.
- **Query** (`/query`): interroga le risorse con Azure Resource Graph (KQL),
  utile per inventario e reportistica.
- **App Insights** (`/appinsights`): elenco componenti Application Insights
  su tutta la subscription, connection string (dietro toggle Mostra/Nascondi)
  e query KQL libere sulle tabelle di telemetria (`requests`, `exceptions`,
  `traces`, `dependencies`, ecc.) con intervallo temporale selezionabile
  (1h/24h/7g/30g). A differenza di Query (che interroga i metadati delle
  risorse via Resource Graph), qui interroghi i **dati di telemetria** veri
  e propri raccolti dall'app monitorata. I risultati mostrati sono limitati
  a 200 righe anche se la query ne restituisce di più (con avviso a
  schermo): oltre quel numero il rendering della tabella nel browser
  diventa così pesante da bloccare la tab ("Page Unresponsive" in Chrome).
  Usa `take`, `summarize` o filtri più stretti nella query per risultati
  mirati.
- **Ruoli** (`/roles`): mostra su quali scope (subscription/RG/risorsa) la
  connessione attiva ha effettivamente un'assegnazione di ruolo, a
  differenza dell'inventario risorse in Query che elenca tutto ciò che è
  raggiungibile per ereditarietà RBAC sul **control plane** (per esempio Owner
  sulla subscription copre la gestione di ogni risorsa sotto, cioè avvio/arresto
  e configurazione, ma **non** l'accesso ai dati di Key Vault/Storage, che
  richiede ruoli dati separati; vedi le Note più sotto). L'Object ID del
  service principal viene letto dal claim `oid` del suo stesso token, senza
  bisogno di permessi Microsoft Graph.
  Include anche la **gestione ruoli per tutti gli utenti della subscription**:
  elenco di ogni assegnazione (utenti, gruppi, service principal) su
  subscription e resource group, assegnazione di un nuovo ruolo built-in a
  un Object ID a livello di subscription o di un singolo RG, e revoca (con
  conferma) di un'assegnazione esistente. I nomi dei principal vengono
  risolti via Microsoft Graph quando possibile. Un'assegnazione RBAC, anche
  Owner, non dà di per sé alcun permesso su Graph, quindi senza quei permessi
  vedrai l'Object ID al posto del nome (non è un errore).
- **Key Vault** (`/keyvault`): crea un vault (RBAC-based), gestisci Secrets
  (lettura valore, creazione/aggiornamento), Keys crittografiche RSA/EC
  (creazione, elenco; il materiale privato non è mai esportabile) e
  **Certificates** (elenco con scadenza evidenziata negli ultimi 30 giorni,
  creazione di certificati self-signed con subject/validità configurabili,
  rinnovo (riemissione con la stessa policy, perché Key Vault non ha
  un'operazione di rinnovo dedicata) ed eliminazione).
- **Function Apps** (`/functions`): start/stop/restart, elenco funzioni,
  lettura host keys (master + host function keys) e delle key specifiche di
  una singola funzione, abilitazione/disabilitazione di una singola
  funzione, visualizzazione/modifica delle application settings, e log
  delle invocazioni recenti (se la function app ha un Application Insights
  collegato, rilevato automaticamente dalla instrumentation key nelle app
  settings; altrimenti la lista risulta vuota, e non è un errore).
- **Storage** (`/storage`): sfoglia storage account, container e blob;
  pulsante "Scansiona tutto l'account" che cerca in tutti i container blob
  con nome che termina in `.tfstate` (copre anche i path dei workspace
  Terraform tipo `env:/prod/terraform.tfstate`) e un'azione "Verifica
  contenuto" che scarica il blob e conferma che sia davvero uno state
  Terraform valido (controllando `terraform_version` e `resources`),
  mostrando versione e numero di risorse gestite.
- **SQL** (`/sql`): elenco server e database con SKU/tier correnti, scala
  il tier di un database, gestisci le firewall rules del server (aggiungi/
  rimuovi IP consentiti), genera il template della connection string
  ADO.NET per un database (senza credenziali: l'app si autentica su Azure
  Resource Manager, non ha mai un login SQL).

## Note

- **La Subscription è opzionale in quasi tutte le pagine** (Risorse,
  Function Apps, Key Vault, SQL, Storage, App Insights, Log Analytics, Ruoli,
  Bastion, Automation, Container Registry, App Configuration, Networking,
  Compute Extra, Monitoring, Identity): lasciandola
  vuota ("-- tutte --") puoi vedere/scegliere tra le risorse di tutte le
  subscription raggiungibili, non solo una alla volta. Per motivi di
  performance questa vista aggregata **non si carica automaticamente**:
  serve premere il pulsante "Carica tutte le subscription" che compare
  quando il campo è vuoto, perché itera su ogni subscription e può
  richiedere diversi secondi (o più, con molte subscription/risorse). Su
  Deploy la subscription resta invece obbligatoria, dato che un deployment
  deve avere un target specifico.
- Il deploy di Bicep richiede la [Bicep CLI](https://learn.microsoft.com/azure/azure-resource-manager/bicep/install)
  installata e raggiungibile nel `PATH`.
- I ruoli effettivi disponibili in app dipendono da quelli già assegnati al
  service principal della service connection (RBAC su subscription/resource
  group).
- **Owner/Contributor/Reader NON danno accesso al data plane** di Key Vault
  e Storage, nemmeno Owner sulla subscription. È un'eccezione documentata
  da Microsoft: questi ruoli permettono di *gestire* la risorsa (crearla,
  configurarla) ma non di leggerne/scriverne i dati tramite Azure AD.
  Servono ruoli dati espliciti, assegnati sulla risorsa specifica (o RG/
  subscription):
  - **Key Vault**: `Secrets` in `/keyvault` richiede **Key Vault Secrets
    Officer** (o **Reader** per la sola lettura); `Keys` richiede
    **Key Vault Crypto Officer** (o **User** per la sola lettura).
  - **Storage**: `/storage` richiede **Storage Blob Data Reader** (lettura
    container/blob, verifica tfstate) o **Storage Blob Data Contributor**
    se in futuro servisse anche scrivere.
  - **App Configuration**: `/appconfiguration` richiede **App Configuration
    Data Owner** (lettura/scrittura/cancellazione chiavi) o **Data Reader**
    per la sola lettura.
  Senza questi ruoli, le chiamate falliscono con `403 AuthorizationPermissionMismatch`
  pur avendo Owner confermato su "Check access" nel portale. Puoi assegnare
  questi ruoli al service principal direttamente dalla pagina **Ruoli**
  dell'app (Object ID + ruolo + scope).
- **Attenzione**: un service principal Owner sulla subscription dà a questo
  tool controllo completo su tutte le risorse (comprese le assegnazioni di
  ruolo). Tienilo presente per l'esposizione dell'app: nessun accesso oltre
  `localhost` senza autenticazione, e valuta se per l'uso quotidiano un
  ruolo più circoscritto (es. Contributor + ruoli dati specifici) sia più
  adatto al principio del privilegio minimo.
- Alcuni tipi di risorsa non hanno un SDK .NET dedicato ergonomico (Workbook,
  Smart Detector Alert Rule, automazioni di Microsoft Defender for Cloud,
  Solutions legacy di Log Analytics, directory AAD B2C): per questi la pagina
  mostra un inventario di sola lettura (nome, location, resource group)
  ottenuto tramite l'API generica delle risorse ARM, senza i campi specifici
  che avrebbe un client dedicato.
- Le funzioni "Mostra valore/key" mostrano segreti in chiaro nella pagina:
  usale solo su una macchina fidata e non esporre l'app oltre `localhost`.
- La gestione ruoli in `/roles` assegna e revoca accesso reale su Azure: una
  revoca sbagliata può togliere l'accesso a te stesso o ad altri; non c'è
  undo lato Azure oltre a riassegnare manualmente il ruolo.
- **Le 10 operazioni di cancellazione/revoca elencate in [Stato del progetto](#stato-del-progetto)
  sono disattivate nel codice** (blocchi `DEBUG-TEST`) perché non sono mai
  state provate su risorse reali. L'interfaccia di conferma (il pulsante
  "Elimina" apre un campo dove va ridigitato il nome della risorsa, in stile
  portale Azure) è invece attiva e provata: la conferma funziona, ma
  l'ultimo passo non cancella nulla finché il blocco non viene tolto.

Costruito con Claude (Anthropic) come assistente di programmazione, sotto direzione e revisione umana a ogni passo.

## Licenza e avvertenze

Rilasciato con licenza [MIT](LICENSE).

Questo software esegue operazioni reali su Azure (avvia e ferma risorse, elimina, assegna e revoca ruoli, svuota cache) con le credenziali del service principal che configuri. Viene fornito così com'è, senza garanzie: prova prima le azioni in una subscription di collaudo e dai al service principal solo i ruoli che servono (per esempio Reader per la sola consultazione). Le credenziali restano sulla tua macchina, cifrate con DPAPI di Windows, e vengono usate solo per autenticarsi verso Azure: non vengono inviate a terzi.
