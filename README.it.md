# AppManager

**Gestisci giochi e programmi su Windows e rimuovili completamente.**
AppManager disinstalla un gioco o un programma e poi trova tutto ciò che lui e Windows hanno lasciato in giro: salvataggi, impostazioni, cache, screenshot, voci di registro, avvii automatici, attività pianificate, servizi, tipi di file, voci del menu contestuale, regole del firewall e collegamenti. Nulla viene eliminato prima che tu abbia visto l'elenco e confermato.

[English](README.md) · **Italiano** · [Français](README.fr.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Русский](README.ru.md) · [Українська](README.uk.md)

**[Scarica](../../releases/latest)** · Windows 10 / 11 · portable, senza installazione · 7 lingue · tema scuro e chiaro

![Libreria dei giochi](docs/images/games-dark.png)

---

## In breve

- **Tutto ciò che è installato, in un posto solo**: Steam, Epic Games, GOG, Ubisoft Connect, EA app, Battle.net, Rockstar, Amazon Games, Xbox / PC Game Pass, app del Microsoft Store, installer classici, repack e copie **portable**.
- **Disinstalla e pulisci**: avvia il programma di disinstallazione giusto, aspetta che finisca e mostra ogni residuo diviso per categoria, con il motivo per cui è stato trovato e quanto AppManager ne è sicuro («Certo», «Probabile», «Da verificare»).
- **Residui di applicazioni già sparite**, compresi i giochi cancellati a mano.
- **Sorveglianza in background** (facoltativa): segue installazioni e disinstallazioni dall'area di notifica e ti avvisa quando una disinstallazione ha lasciato qualcosa.
- **Gestore dei salvataggi**: backup, più profili di salvataggio per gioco, «nuova partita» senza perdere quella vecchia.
- **Prima di tutto la sicurezza**: Cestino e backup zip prima di eliminare, aree di sistema protette, nulla eliminato senza la tua conferma.
- **Sempre portable**: tutto sta nella cartella `Dati` accanto al programma; ogni PC ha il suo profilo.

## Immagini

Le immagini sono della versione inglese; il programma segue la lingua di Windows.

### Panoramica
Il PC in un colpo d'occhio: da quanto è acceso, quando è stato installato Windows, cosa è cambiato dall'ultimo avvio, lo spazio sui dischi e chi occupa di più. L'elenco di giochi e programmi si esporta in CSV.

![Panoramica](docs/images/overview.png)

### Giochi
Tutti i giochi installati con copertina, dimensione e launcher. Il pannello di dettaglio mostra la cartella, l'editore, come è stato trovato il gioco e dà accesso rapido a salvataggi, disinstallazione, avvio, cartella e blocco di Internet. Le copertine mancanti si cercano online (Steam, PCGamingWiki, chiave SteamGridDB facoltativa, Wikipedia). Senza copertina si usa l'icona del gioco.

![Giochi, tema chiaro](docs/images/games-light.png)

I giochi vengono trovati anche senza launcher: dalle impronte dei motori di gioco (Unity, Unreal, CryEngine, Source, RPG Maker, Ren'Py…), dalle firme di crack ed emulatori di Steam (CODEX, RUNE, EMPRESS, Goldberg…), dai nomi dei repack, dai giochi aggiunti a Steam a mano e dagli eseguibili che Windows ricorda di aver avviato, ovunque siano.

### Programmi
Programmi installati, app del Microsoft Store, strumenti di Steam e programmi portable. Runtime, driver e app incluse in Windows stanno dietro «Mostra i componenti di Windows», così non si rimuovono per sbaglio. Di un programma portable dentro un progetto di sviluppo o una cartella di documenti si propone solo l'eseguibile, mai l'intera cartella.

![Programmi](docs/images/programs.png)

### Disinstalla e pulisci
AppManager avvia la disinstallazione giusta (Steam, il programma di disinstallazione, Windows per le app dello Store, rimozione diretta per le copie portable). Quando finisce, cerca i residui.

![Conferma della disinstallazione](docs/images/uninstall.png)

I residui sono divisi per categoria. Quelli «Certi» e «Probabili» sono già selezionati, quelli «Da verificare» no. La stessa finestra, aperta con **Dati**, mostra i dati di un gioco o di un programma senza disinstallare nulla.

![Revisione dei residui](docs/images/leftovers-review.png)

Cosa viene cercato, oltre alle cartelle dei dati in `AppData`, `ProgramData` e `Documenti`:
- avvii automatici (Run, RunOnce, stato in Gestione attività, cartella Esecuzione automatica), attività pianificate e servizi del programma;
- tipi di file, voci «Apri con», app predefinite, menu contestuale, componenti COM ed estensioni della shell;
- cartelle aggiunte alla `PATH`, impostazioni delle notifiche, cronologia di avvio, compatibilità, preferenze GPU, regole del firewall;
- per i giochi: dati di Steam, cartelle Unity e Unreal, salvataggi in `My Games` e `Giochi salvati`, cartelle degli emulatori di Steam, cache degli shader.

Le voci di sistema si propongono solo se sono davvero del programma: ciò che punta a Windows non viene mai toccato.

### Residui di applicazioni già rimosse
Un'unica analisi trova i resti di giochi e programmi non più installati, o cancellati a mano, e li divide tra le sezioni Giochi e Programmi. Ciò che risulta davvero orfano viene selezionato; i dati usati di recente senza questa prova restano «Da verificare».

![Residui](docs/images/leftovers.png)

### Sorveglianza in background
Spenta di serie. Se la attivi, AppManager parte con Windows e resta nell'area di notifica. Segue le voci di disinstallazione di Windows e le librerie di Steam ed Epic. Quando un programma o un gioco sparisce, aspetta che il programma di disinstallazione finisca e poi cerca i residui.

![Cronologia della sorveglianza](docs/images/monitoring.png)

Se è rimasto qualcosa, compare un avviso sopra l'area di notifica, senza coprire la barra delle applicazioni. **Vedi e rimuovi** apre l'elenco, **Ignora** lo chiude. L'avviso sparisce dopo 20 secondi e aspetta se è aperto un gioco a schermo intero o una presentazione. Se arrivano più disinstallazioni di seguito, lo stesso avviso le raccoglie. La pagina **Sorveglianza** conserva la cronologia: installazioni, aggiornamenti e disinstallazioni, anche quelle fatte con AppManager, con ciò che è rimasto e ciò che è già stato tolto. La sorveglianza non elimina mai nulla da sola.

![Avviso](docs/images/notification.png)

### Salvataggi e profili
Per ogni gioco, **Gestisci i salvataggi** trova da solo le cartelle dei salvataggi: Steam, `My Games`, `Giochi salvati`, `AppData`, cartelle Unity e Unreal, cartelle degli emulatori di Steam usate dalle copie non ufficiali e la cartella del gioco, dove salvano molte copie portable. Per i salvataggi che dal nome non si riconoscono consulta anche PCGamingWiki, inviando solo il titolo del gioco.
- **Backup dei salvataggi** in file zip, ripristinabili quando vuoi. Prima di ogni ripristino viene fatto un backup dei salvataggi attuali.
- **Profili**: più insiemi di salvataggi per lo stesso gioco, per esempio uno per ogni giocatore.
- **Nuova partita**: mette da parte i salvataggi attuali e riparte da zero; quando vuoi torni al profilo precedente.

![Salvataggi](docs/images/saves.png)

### Avvio automatico e rete
**Avvio automatico** elenca i programmi che partono con Windows e li attiva o disattiva come Gestione attività. Disattivare non elimina nulla.

![Avvio automatico](docs/images/startup.png)

**Rete** mostra in tempo reale quali programmi in esecuzione sono connessi e verso dove. Puoi bloccare una singola destinazione, o tutto l'accesso a Internet di un programma, con Windows Firewall. Ogni regola si toglie con un clic dalle Impostazioni.

![Rete](docs/images/network.png)

### Impostazioni
Tema (chiaro, scuro o come Windows), lingua, limite di spazio per le immagini scaricate, scelte predefinite per Cestino e backup, sorveglianza in background, cartelle portable, metodi di ricerca e regole di rete.

![Impostazioni](docs/images/settings.png)

## Sicurezza

- **Nulla viene eliminato senza la tua conferma.** Vedi sempre l'elenco completo, con il motivo e l'affidabilità di ogni elemento.
- **Cestino e backup**: di default i file eliminati vanno nel Cestino, in una cartella per applicazione, con i loro percorsi completi. Lì finiscono anche le voci di sistema, come file `.reg`, `.xml` e `.txt`. Salvataggi, impostazioni e voci di sistema vengono anche copiati in un backup zip, ripristinabile dalla sezione **Backup**. Se il backup non riesce, la pulizia non parte.
- **Aree protette**: Windows, le cartelle principali di Programmi e ProgramData, i componenti di Windows, le cartelle dell'utente (Documenti, Desktop, Download, AppData…), le cartelle di sistema nella radice dei dischi, i launcher e le cartelle di tutti i giochi e programmi installati. Una cartella che ne contiene una protetta non viene mai eliminata.
- **I collegamenti non vengono mai seguiti**: si toglie solo la junction o il link simbolico, mai ciò a cui punta.
- **Percorsi strani** (nomi brevi, variabili, `..`, percorsi di rete e `\\?\`) vengono ricondotti al percorso vero prima dei controlli, oppure rifiutati. I percorsi oltre i 260 caratteri sono gestiti.
- **Una copia alla volta**, impostazioni salvate in modo atomico e ogni operazione annotata nel registro attività.

## Privacy

AppManager si connette a Internet solo per cercare le copertine dei giochi e le cartelle dei salvataggi. Per i salvataggi consulta PCGamingWiki inviando solo il titolo del gioco. Entrambe le ricerche si possono disattivare. La chiave SteamGridDB, facoltativa, viene inviata solo a SteamGridDB. Nessuna telemetria. Ogni PC è riconosciuto da un'impronta anonima del suo ID di Windows; l'ID non viene mai salvato né inviato.

## Lingue

English, Italiano, Français, Deutsch, Español, Русский, Українська. La lingua segue Windows (inglese se non è disponibile) e si cambia nelle Impostazioni.

## Scaricare e usare

1. Scarica `AppManager.exe` dall'[ultima release](../../releases/latest).
2. Mettilo in una cartella qualsiasi, anche su una chiavetta, e avvialo. Windows chiede i permessi di amministratore; servono per il Prefetch, le chiavi di registro di sistema, i servizi, le attività pianificate di sistema e le regole del firewall. Senza, AppManager funziona in «modalità limitata» e trova e pulisce comunque i dati del tuo utente.

Requisiti: Windows 10 o 11. Usa .NET Framework 4.8, già incluso in Windows. Per aggiornare basta sostituire `AppManager.exe`: la cartella `Dati` resta.

**Tastiera**: Ctrl+F cerca · F5 aggiorna · Canc disinstalla l'elemento selezionato (apre solo la conferma) · Esc chiude · Invio conferma (mai le azioni che eliminano qualcosa).

## Compilare dal sorgente

Non serve Visual Studio: basta il compilatore C# incluso in Windows (C# 5, .NET Framework 4.8).

```
cd Source
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1 -AsInvoker` crea `bin\AppManager.Test.exe` senza la richiesta di amministratore, per le prove. `build.ps1 -Harness` crea anche `tools\Harness.exe`, lo strumento di verifica da console:

| Comando | Cosa verifica |
|---|---|
| `robust <cartella>` | casi limite: percorsi protetti, collegamenti, percorsi lunghi, nomi Unicode, file bloccati, backup alterati (solo dati finti) |
| `invariants` | in sola lettura: tutto ciò che le analisi propongono sul PC rispetta le regole di sicurezza |
| `savetest <cartella>` | gestore dei salvataggi: backup, nuova partita, cambio di profilo, annullamento in caso di errore, ripristino |
| `recycletest <cartella>` | Cestino organizzato per applicazione, senza usare il Cestino vero |
| `pctest <cartella>` | riconoscimento del PC, risorse, avvio automatico, esportazione, traduzioni, copie piratate e portable, logica della sorveglianza |
| `logtest <cartella>` | registro attività |
| `watchtest <cartella>` | prova completa della sorveglianza con una finta applicazione creata e tolta dalla prova |

`pctest`, `logtest` e `watchtest` vogliono `APPMANAGER_DATA` impostata sulla stessa cartella di prova; `savetest` e `recycletest` su una cartella diversa.

### Struttura del progetto

```
Source/
  src/core   rilevamento, ricerca dei residui, pulizia, backup, salvataggi, sorveglianza (senza interfaccia)
  src/app    ViewModel WPF, icona nell'area di notifica, avvisi
  ui         XAML (caricato all'avvio) e traduzioni in ui/lang/*.txt
  tools      strumento di verifica da console (Harness)
  build.ps1  script di compilazione
docs/images  immagini
```

I testi sono scritti in italiano nel codice e nello XAML e fanno da chiave; le traduzioni sono in `ui/lang/*.txt`.

## Licenza

[MIT](LICENSE) © Crover81
