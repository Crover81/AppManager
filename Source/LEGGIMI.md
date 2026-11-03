# AppManager

Programma per gestire **giochi e programmi** installati e rimuoverli **completamente**. Oltre al gioco o al programma elimina tutto ciò che lui e Windows lasciano in giro: salvataggi, impostazioni, dati, cache, screenshot, voci di registro, avvii automatici, attività pianificate, servizi, tipi di file, voci del menu contestuale, regole del firewall e collegamenti.

Avvia **`AppManager.exe`**. Non va installato: è sempre portable e usa .NET Framework 4.8, già incluso in Windows 10/11.

All'avvio Windows chiede i permessi di amministratore (UAC). Servono per:
- leggere il Prefetch di Windows;
- pulire le chiavi di registro di sistema, i servizi, le attività pianificate di sistema e le regole del firewall.

Se rifiuti, il programma non parte. Senza privilegi di amministratore (per esempio da un account standard) funziona in "modalità limitata": trova e pulisce comunque i dati del tuo utente.

Il programma ha due sezioni separate, **Programmi** e **Giochi**, ognuna con la sua libreria e i suoi residui, più le sezioni comuni **Panoramica**, **Avvio automatico**, **Rete**, **Backup**, **Log** e **Impostazioni**.

## Lingua e tema
- **Lingua**: quella di Windows; se non è disponibile, l'inglese. Lingue: Deutsch, English, Español, Français, Italiano, Русский, Українська. Si può scegliere in Impostazioni › Aspetto e lingua (serve un riavvio). Il registro attività è in italiano o in inglese.
- **Tema**: chiaro, scuro o come Windows. Cambia subito, senza riavvio, anche dal pulsante accanto al nome del programma.

## Panoramica
La pagina iniziale: nome del PC, da quanto è acceso (dall'ultima accensione o ripresa dalla sospensione), da quanto è installato Windows (una reinstallazione da zero azzera il conto), cosa è stato installato o tolto dall'ultimo avvio, lo spazio sui dischi e chi occupa di più. Da qui si esporta anche l'elenco di giochi e programmi in un file CSV.

## Avvio automatico
Elenca i programmi che partono con Windows (registro e cartella Esecuzione automatica) e li attiva o disattiva come Gestione attività. Disattivare non elimina nulla. Le voci «tutti gli utenti» richiedono l'amministratore.

## Sorveglianza in background
Spenta di serie; si attiva nelle Impostazioni o nella pagina **Sorveglianza**, e vale solo per il PC su cui la attivi. AppManager parte allora con Windows e resta nell'area di notifica, accanto a orologio, volume e rete (da amministratore con un'attività pianificata «AppManager», altrimenti con la chiave Run dell'utente). Chiudendo la finestra resta attivo; per chiuderlo davvero si usa il menu della sua icona.

Segue le voci di disinstallazione di Windows e le librerie di Steam ed Epic (le app di Microsoft Store no). Quando un programma o un gioco sparisce aspetta che il programma di disinstallazione finisca, poi cerca i residui come dopo una disinstallazione fatta con AppManager. Se trova residui «Certi» o «Probabili» lo segnala con un avviso sopra l'area di notifica: **Vedi e rimuovi** apre l'elenco per scegliere cosa eliminare, **Ignora** lo chiude. L'avviso sparisce da solo dopo 20 secondi (resta finché il mouse ci è sopra) e aspetta se è aperto un gioco a schermo intero o una presentazione. Se arriva un'altra disinstallazione mentre l'avviso è aperto, lo stesso avviso le raccoglie tutte.

La pagina **Sorveglianza** conserva la cronologia: installazioni, aggiornamenti e disinstallazioni (anche quelle fatte con AppManager), con ciò che è rimasto e ciò che è stato già tolto. La sorveglianza non elimina mai nulla da sola. Gli aggiornamenti che cambiano voce di disinstallazione non contano come disinstallazioni; per runtime e componenti di Windows i residui non si cercano.

## Sempre portable: PC e risorse
Tutti i dati stanno nella cartella **`Dati`** accanto al programma. Se lì non si può scrivere (chiavetta protetta), si usa `%LOCALAPPDATA%\AppManager` e AppManager lo segnala. Non serve nessun file per attivare questa modalità.

AppManager riconosce ogni PC da un'impronta anonima dell'ID di Windows (l'ID non viene salvato né inviato) e tiene un profilo in `Dati\pc`:
- **Un PC già conosciuto** ritrova cartelle, elementi nascosti e giochi aggiunti a mano.
- **Un PC nuovo** parte senza i percorsi degli altri PC. Tema, lingua e chiave di SteamGridDB valgono ovunque.

Le risorse si tengono in ordine da sole:
- **Immagini scaricate**: restano finché il gioco è noto su questo PC o su un altro PC conosciuto, quindi cambiare PC non le cancella. Un gioco sparito resta noto per 14 giorni.
- **Limite di spazio**: automatico (15% dello spazio disponibile, da 50 MB a 1 GB) o a scelta. Oltre il limite si tolgono prima le immagini dei residui, poi quelle dei giochi degli altri PC. Quelle dei giochi di questo PC restano. Con meno di 64 MB liberi non si scarica nulla.
- **PC non più visti**: dopo 6 mesi vengono dimenticati, con le immagini usate solo da loro. Si possono dimenticare anche a mano.
- **Sul PC**: alla chiusura si tolgono le cartelle predefinite di backup e salvataggi rimaste vuote; all'avvio i file temporanei rimasti da operazioni interrotte. I dati delle versioni non portable vengono spostati in `Dati` e la vecchia cartella viene tolta.
- **Memoria**: restano caricate solo le immagini grandi degli ultimi elementi guardati; con la finestra ridotta a icona la memoria viene rilasciata.

## Giochi

### Libreria
Elenca i giochi installati:
- **Launcher:** Steam (anche su più librerie), Epic Games, GOG, Ubisoft Connect, EA app, Battle.net, Rockstar, Amazon Games, Xbox/PC Game Pass.
- **Giochi senza launcher:** repack, installer classici e **portable**. Li riconosce dalle "impronte" dei motori e delle librerie tipiche dei videogiochi (Unity, Unreal, `steam_api`, DLSS, redistributable…).

I giochi vengono trovati in quattro modi, in quest'ordine, ovunque siano sul PC (Impostazioni › Ricerca dei giochi mostra quanti ne trova ciascuno):
1. **Launcher**;
2. **Programmi installati**: i giochi installati con un installer (come i repack) compaiono in "App installate" di Windows, in qualunque cartella siano;
3. **Cartelle dei giochi portable**: sulla radice dei dischi e sul Desktop, le cartelle che iniziano con `Games`, `Giochi`, `Gaming` sono controllate da sole; le altre si aggiungono nelle Impostazioni. Fuori da queste, i dischi si esplorano fino a 4 livelli (con limiti di cartelle e di tempo): vale come gioco una cartella con un motore riconoscibile o le firme di crack ed emulatori di Steam (CODEX, RUNE, EMPRESS, Goldberg…), o con un eseguibile dello stesso nome e una sigla di repack nel nome. Si trovano anche i giochi aggiunti a Steam a mano e quelli che Windows ricorda di aver avviato, ovunque siano;
4. **Collegamenti** a un gioco sul desktop e nel menu Start, compresi quelli di Steam ed Epic (`steam://…`).

Nella libreria ogni gioco compare una volta sola, anche se lo trovano più metodi: "Rilevato tramite" nel pannello del gioco indica il primo, "Trovato anche da" gli altri. Nelle Impostazioni ogni metodo mostra quanti giochi trova in tutto e, sotto, quanti di questi erano "già trovati prima" da un metodo precedente (per esempio un gioco di Steam con un collegamento sul desktop).

Per ogni gioco:
- **Disinstalla e pulisci**: avvia la disinstallazione nel modo giusto (Steam, programma di disinstallazione del gioco, rimozione diretta per Epic e per i portable), aspetta che finisca e mostra tutto ciò che è rimasto, diviso per categoria.
- **Dati del gioco**: mostra salvataggi, impostazioni e tracce senza disinstallare nulla.
- **Gestisci i salvataggi**: backup e ripristino dei salvataggi, profili e nuova partita (vedi sotto).

### Salvataggi e profili
Dal pannello di ogni gioco, **Gestisci i salvataggi** apre il gestore dei salvataggi. AppManager trova da solo le cartelle dei salvataggi (Steam, Documenti\My Games, Giochi salvati, AppData, Unity, Unreal…), consultando anche PCGamingWiki per quelle che dal nome non si riconoscono. Cerca anche nelle cartelle degli emulatori di Steam usate dalle copie non ufficiali (per AppID, anche se la copia non lo dichiara) e nella cartella del gioco, dove salvano molte copie portable; le altre si aggiungono a mano. A scelta si includono anche le impostazioni del gioco (grafica, comandi…).
- **Backup dei salvataggi**: una copia dei salvataggi in un file zip, che compare anche nella sezione Backup. **Ripristina** rimette esattamente quella situazione: prima viene sempre fatto un backup automatico dei salvataggi attuali («Prima del ripristino»), così niente va perso.
- **Profili**: più insiemi di salvataggi per lo stesso gioco, per esempio uno per ogni giocatore sullo stesso PC, o uno con impostazioni diverse. Il profilo «in uso» ha i suoi file nelle cartelle del gioco; gli altri sono messi da parte nel deposito (`Documenti\AppManager Salvataggi`). **Usa questo** scambia i profili in un attimo.
- **Nuova partita**: mette da parte i salvataggi attuali nel profilo in uso e lascia le cartelle del gioco vuote, così il gioco riparte da zero; al profilo precedente si torna quando si vuole.

Ogni cambio di profilo è annotato passo per passo: se qualcosa non riesce (un file in uso, un disco pieno) tutto torna com'era. Con il gioco aperto i profili non si cambiano. Se i salvataggi sono sincronizzati con **Steam Cloud**, AppManager lo segnala: conviene cambiare profilo con Steam chiuso, oppure disattivare Steam Cloud per quel gioco. Il deposito non viene mai toccato dalle disinstallazioni né proposto come residuo. Quando elimini l'ultimo profilo messo da parte (i suoi file vanno nel Cestino) resta solo il profilo in uso, che non ha bisogno del deposito: la cartella del gioco viene tolta da `AppManager Salvataggi`. Il nome proposto per un nuovo profilo è sempre il primo libero («Profilo 2», «Profilo 3»…).

### Copertine e immagini
Per ogni gioco servono la copertina verticale (nell'elenco), la testata orizzontale e l'immagine "hero" con il logo (nel pannello di dettaglio). Quando mancano vengono cercate così:
1. l'immagine inclusa nella cartella del gioco (molti repack ne hanno una), usata subito anche senza Internet;
2. Steam: per AppID o per nome e, per le raccolte vendute come pacchetto, le immagini del pacchetto;
3. PCGamingWiki, usato solo come dizionario per trovare l'ID Steam giusto;
4. **SteamGridDB**, se inserisci la tua chiave API gratuita nelle Impostazioni (accedi a steamgriddb.com con Steam, poi Preferenze › API). La chiave viene inviata solo a SteamGridDB e resta salvata sul PC (o sulla chiavetta, in modalità portable);
5. Wikipedia, per la copertina;
6. per le raccolte, la copertina di uno dei giochi contenuti.

Se non si trova nessuna copertina si usa l'icona del gioco: quella dell'eseguibile principale, poi quella che il gioco dichiara a Windows (per i giochi di Steam è il loro logo), poi quella degli altri eseguibili o di un file `.ico` nella sua cartella. Se nessuno ne ha una restano le iniziali del titolo.

Ogni risultato deve corrispondere al titolo completo: i numeri dei seguiti devono coincidere ("Wreckfest 2" non prende quella di "Wreckfest") e un titolo più generico, come il nome della serie, viene scartato. Viene inviato solo il nome del gioco; le immagini restano in una cache locale, che si svuota da sola per i giochi non più presenti. L'opzione si può disattivare nelle Impostazioni.

## Programmi

### Libreria
Elenca i programmi, installati o **portable**, esclusi i giochi (che hanno la loro sezione):
- **App installate di Windows**, compresi i pacchetti MSI che non indicano la cartella (la si ricava dai collegamenti);
- **app del Microsoft Store**, con nome e icona;
- **applicazioni e strumenti di Steam** (facoltativi, nelle Impostazioni);
- **programmi portable** trovati dai collegamenti sul desktop e nel menu Start, dalle cartelle dei programmi portable (`PortableApps`, `Portable`, `Programmi…`, `Tools`… sulla radice dei dischi e sul Desktop, e quelle aggiunte nelle Impostazioni), dalle cartelle su qualsiasi disco con un eseguibile dello stesso nome o **aggiunti a mano**. Di un progetto di sviluppo si propone solo l'eseguibile, mai la cartella.

Runtime (Visual C++, .NET…), driver e app incluse in Windows servono ad altri programmi: sono nascosti, e si vedono con il link **«Mostra i componenti di Windows»** accanto al riepilogo. Disinstallarli è possibile, ma AppManager avvisa prima.

Un programma portable è "a file singolo" quando il suo eseguibile sta in una cartella che non è solo sua: una raccolta di strumenti (per esempio `D:\Utility`), una cartella con documenti o un progetto di sviluppo. In quel caso, disinstallandolo, viene proposto solo l'eseguibile e mai l'intera cartella.

### Disinstallazione
- Programmi installati: si apre il loro programma di disinstallazione; al termine si passa da soli alla ricerca dei residui.
- App dello Store: le rimuove Windows, come da Impostazioni › App. Se Windows rifiuta la rimozione (app di sistema, criteri), AppManager lo dice e non propone di cancellare i file a mano.
- Portable: la cartella (o il solo eseguibile) viene proposta per l'eliminazione insieme ai residui.

### Tracce che vengono cercate
Oltre alle cartelle dei dati in `AppData`, `ProgramData`, `Documenti` e alle cartelle "nascoste" nel profilo utente (come `.vscode` o `.gradle`):
- **avvio automatico**: chiavi Run e RunOnce, stato in Gestione attività, cartella Esecuzione automatica;
- **attività pianificate** (mai quelle di Windows) e **servizi** del programma (mai driver o servizi di Windows);
- **tipi di file** registrati (ProgID), voci **"Apri con"**, **app predefinite** (RegisteredApplications e Clients);
- **menu contestuale** di Esplora risorse, componenti **COM** ed estensioni della shell (icone sovrapposte comprese);
- cartelle aggiunte alla **variabile PATH**;
- impostazioni delle **notifiche**, cronologia di avvio (UserAssist), compatibilità, preferenze GPU, regole del **firewall**.

Le voci di sistema vengono proposte solo se risultano del programma: un tipo di file, un comando o un componente che punta a Windows non viene mai toccato.

## Rete

Il monitor **Rete** mostra, in tempo reale, le connessioni dei programmi in esecuzione: per ogni eseguibile, verso quali indirizzi è connesso (per esempio «da gioco.exe verso 192.168.5.33:443»), lo stato e il protocollo. Si aggiorna da solo ogni due secondi mentre la pagina è aperta (si può mettere in pausa) e legge soltanto: la lettura delle connessioni non richiede l'amministratore.

Da qui puoi:
- **bloccare una singola destinazione**: nessun programma (o solo quello scelto) potrà più connettersi a quell'indirizzo;
- **bloccare tutto l'accesso a Internet di un programma**, in entrata e in uscita.

Lo stesso interruttore **«Blocca l'accesso a Internet»** è nel pannello di dettaglio di ogni gioco e programma (sezione RETE): utile, per esempio, per tenere offline un gioco. Per un gioco vengono bloccati tutti i suoi eseguibili.

I blocchi usano **Windows Firewall** (creare o togliere una regola richiede l'amministratore) e restano finché non li togli. Li ritrovi tutti in **Impostazioni › Regole di rete**, dove puoi rimuoverli con un clic. AppManager non reindirizza né altera il traffico: si limita a vederlo e, se vuoi, a bloccarlo.

## Residui
Un'unica analisi trova i resti di giochi e programmi **già disinstallati** (o cancellati a mano) e li divide tra le due sezioni. Per i giochi: dati di Steam, cartelle Unity e Unreal, salvataggi in `Documenti\My Games` e `Giochi salvati`, cache degli shader, tracce di eseguibili che non esistono più. Per i programmi: cartelle rimaste in Programmi e AppData senza più eseguibili, voci di disinstallazione "fantasma", avvii automatici, attività, servizi, tipi di file e voci della PATH che puntano a file spariti.

Vengono selezionati da soli i resti di ciò che risulta davvero cancellato. Restano "da verificare" (non selezionati) i dati usati di recente per cui non c'è questa prova.

Le cache vengono eliminate direttamente, senza passare dal Cestino. I file in uso (per esempio dal driver video) vengono eliminati al riavvio successivo.

## Backup
Prima di ogni pulizia salvataggi, impostazioni, voci di registro, attività pianificate, servizi e voci della PATH vengono salvati in un file zip (di default in `Documenti\AppManager Backup`). Se il backup non riesce, la pulizia non parte.

I backup sono **divisi per applicazione**: ognuno ha una sottocartella con il nome del gioco o del programma, e dentro lo zip ci sono il nome, il tipo e la copertina (o l'icona), così resta riconoscibile anche dopo la disinstallazione. Pulendo i residui di più applicazioni insieme, viene creato un backup per ciascuna.

La sezione **Backup** funziona come le librerie: a sinistra le applicazioni che hanno dei backup (con immagine, tipo e numero di backup, filtrabili tra giochi e programmi e cercabili per nome), a destra la scheda dell'applicazione scelta con il suo nome, se è ancora installata, la cartella, l'editore e la versione, lo spazio occupato dai backup e la data dell'ultimo. Sotto ci sono i suoi backup, dal più recente, con il motivo (Disinstallazione, Pulizia dei dati, Residui, Salvataggi, Prima del ripristino), la data e il contenuto: da lì si ripristinano, si mostrano o si eliminano (nel Cestino), anche tutti insieme. Nel pannello di ogni gioco o programma la voce «Backup» dice quanti ne ha e «Vedi i backup» apre questa sezione su di lui. I servizi ripristinati tornano attivi dopo il riavvio del PC. Nelle Impostazioni si può scegliere di conservare solo gli ultimi 3, 5 o 10 backup di ogni applicazione. I backup delle versioni precedenti restano visibili e ripristinabili.

## Cestino ed eliminazione
In **Impostazioni › Eliminazione** si scelgono le caselle di partenza di ogni pulizia (pagine Residui, pulizia dei dati di un gioco o programma, residui dopo una disinstallazione); in ogni pulizia si possono cambiare solo per quella volta:
- **Usa il Cestino**: ciò che viene tolto va nel Cestino di Windows, raccolto in **una cartella per applicazione** chiamata «AppManager - nome del programma». Dentro, ogni file mantiene il suo percorso completo (per esempio `C\Program Files\Programma\programma.exe`), e le voci di sistema tolte diventano file nella sottocartella **«File registro»**: `.reg` per le chiavi e i valori del registro e per i servizi (si rimettono con un doppio clic), `.xml` per le attività pianificate (Utilità di pianificazione › Importa attività), `.txt` per le voci della PATH e le regole del firewall. Per recuperare qualcosa si ripristina la cartella dal Cestino e si ha tutto sotto mano. C'è una cartella per ogni unità (il Cestino è per unità): la cartella viene creata nella radice dell'unità, per esempio `C:\AppManager - Programma`, e subito mandata nel Cestino. Se il Cestino la rifiuta (per esempio perché è troppo grande) resta lì, con tutto dentro, e AppManager lo segnala. Spento: i file vengono eliminati definitivamente.
- **Backup prima di eliminare**: prima di togliere qualcosa, salvataggi, impostazioni e voci di sistema vengono copiati in un file zip, che si rimette al suo posto con un clic dalla sezione Backup, anche dopo aver svuotato il Cestino. Le cartelle dei programmi, gli screenshot, le cache e i collegamenti non sono inclusi.

Le due scelte sono indipendenti: con entrambe accese ci sono due modi per tornare indietro.

## Log
La sezione **Log** ha due schede:
- **Log e diagnostica del PC**: trova ed elimina segnalazioni di errore di Windows, dump di arresti anomali (anche quelli delle schermate blu, come amministratore), log di Steam, Epic, EA, Ubisoft, GOG e Battle.net, log e crash dei giochi Unity e Unreal (mai i loro salvataggi) e log dimenticati nella cartella dei file temporanei. Di default solo i file più vecchi di 7 giorni. Non tocca mai i registri eventi di Windows né i file in uso.
- **Registro di AppManager**: il registro attività, con filtro per livello (informazioni, avvisi, errori) e ricerca; si può esportare o svuotare. Le voci più vecchie di 30 giorni (o 7, 90, o mai) vengono tolte all'avvio e il file non supera i 2 MB; le righe ripetute di seguito vengono compattate.

## Aggiornare da GameManager
AppManager è la nuova versione di GameManager:
- i dati di `%LOCALAPPDATA%\GameManager` (impostazioni, chiave di SteamGridDB, copertine) vengono spostati da soli in `%LOCALAPPDATA%\AppManager` al primo avvio;
- la cartella dei backup `Documenti\GameManager Backup` diventa `Documenti\AppManager Backup`; i backup già fatti restano visibili e ripristinabili;
- sulle chiavette basta sostituire il programma: la cartella `Dati` resta valida. I vecchi file `AppManager.portable` e `GameManager.portable` non servono più.

## Sicurezza
- **Nulla viene eliminato senza la tua conferma**: vedi sempre l'elenco completo, con il motivo di ogni elemento e un'indicazione di affidabilità ("Certo", "Probabile", "Da verificare").
- **Aree protette**: Windows, le cartelle principali di Programmi e ProgramData, i componenti di Windows (Common Files, WindowsApps, Defender…), le cartelle dell'utente (Documenti, Desktop, Download, AppData…), le cartelle di sistema nella radice dei dischi (`$Recycle.Bin`, `Recovery`…), i launcher e le cartelle di **tutti** i giochi e programmi installati. Una cartella che ne contiene un'altra protetta non viene mai eliminata.
- **Profilo utente**: direttamente in `C:\Users\<nome>` si possono eliminare solo le cartelle "nascoste" dei programmi (quelle che iniziano con il punto). Cartelle come Dropbox, Nextcloud, `source` o le librerie di Zotero restano sempre tue.
- **Collegamenti** (junction e link simbolici): non vengono mai seguiti. Si toglie solo il collegamento, e i dati a cui punta restano dove sono. Se un collegamento in una cartella superiore porta in un'area protetta, l'elemento viene rifiutato.
- **Percorsi strani**: nomi brevi (`C:\PROGRA~1`), variabili (`%SystemRoot%`), `..`, percorsi di rete o `\\?\` vengono ricondotti al percorso vero prima dei controlli, oppure rifiutati. I percorsi oltre i 260 caratteri sono gestiti.
- **Registro, servizi, attività e PATH**: mai le chiavi di Windows, i driver, i servizi ospitati da svchost, le attività di Windows (anche quelle fuori da `\Microsoft\`) e le cartelle di sistema nella PATH.
- **Cestino e backup**: di default i file vanno nel Cestino (nella cartella «AppManager - nome del programma») e viene creato un backup. Se il Cestino rifiuta la cartella, i file restano raccolti in quella cartella nella radice dell'unità, senza essere eliminati. Sulle unità senza Cestino (chiavette, unità di rete) Windows chiede prima di eliminare definitivamente; senza la tua conferma, l'elemento viene eliminato definitivamente solo se è già nel backup. I dati che non si riescono a leggere per il backup, e le voci di sistema di cui non si riesce a salvare una copia in «File registro», non vengono eliminati.
- **Ripristino**: un backup rimette le cose solo dove AppManager avrebbe potuto toglierle. Un backup alterato non può scrivere in cartelle di sistema né modificare altre chiavi del registro.
- **Robustezza**: le impostazioni vengono salvate in modo atomico (un file danneggiato viene conservato come `settings.json.danneggiato`). Può essere aperta una sola copia del programma. Chiudere la finestra durante una pulizia chiede conferma.
- Ogni operazione viene annotata in `Dati\log.txt`, consultabile nella sezione Log.
- **Dati di AppManager**: la sua cartella dati, i backup e il deposito dei salvataggi non vengono mai proposti come residui.
- AppManager si connette a Internet solo per cercare le copertine dei giochi e le cartelle dei loro salvataggi (su PCGamingWiki, inviando solo il titolo). Entrambe le ricerche si possono disattivare.

## Scorciatoie da tastiera
Ctrl+F cerca · F5 aggiorna la pagina · Canc disinstalla l'elemento selezionato (apre solo la conferma) · Esc chiude la finestra aperta o svuota la ricerca · Invio conferma (mai le azioni che eliminano qualcosa, che si confermano solo con un clic).

## Ricompilare e verificare
Il sorgente è in `src` (C#) e `ui` (XAML). Per ricompilare non serve Visual Studio:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Con `-Harness` viene compilato anche `tools\Harness.exe`, lo strumento di verifica da console. Tra i suoi comandi:
- `robust <cartella di prova>`: le prove sui casi limite (percorsi protetti, collegamenti, percorsi lunghi, nomi Unicode, file bloccati, backup alterati…). Usa solo dati finti creati e poi rimossi;
- `invariants`: in sola lettura, controlla che tutto ciò che le analisi propongono sul PC rispetti le regole di sicurezza;
- `savetest <cartella di prova>`: le prove del gestore dei salvataggi (backup, nuova partita, cambio di profilo, annullamento in caso di errore, ripristino) su dati finti;
- `pctest <cartella dati>`: le prove di riconoscimento del PC, risorse, avvio automatico, esportazione e lingue (da eseguire con `APPMANAGER_DATA` sulla stessa cartella);
- `logtest <cartella dati>`: le prove del registro attività (da eseguire con la variabile `APPMANAGER_DATA` impostata sulla stessa cartella di prova);
- `recycletest <cartella di prova>`: le prove del Cestino organizzato per applicazione (percorsi completi, «File registro», file in uso), senza usare il Cestino vero;
- `watchtest <cartella dati>`: la prova completa della sorveglianza in background con una finta applicazione (voce di disinstallazione, cartella e chiave create dalla prova e poi tolte): la voce sparisce e i residui devono comparire nella cronologia. Da eseguire con `APPMANAGER_DATA` sulla stessa cartella.

`savetest` e `recycletest` vanno eseguiti con `APPMANAGER_DATA` su una cartella diversa da quella di prova: AppManager protegge i propri dati. `recycletest` la vuole sul disco C.

I testi sono in italiano nel codice e nello XAML e fanno da chiave; le traduzioni sono in `ui\lang\*.txt`. Le voci dal numero 5000 (registro attività) esistono solo in inglese.
