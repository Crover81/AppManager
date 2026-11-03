# AppManager

**Gérez vos jeux et programmes sous Windows, et supprimez-les complètement.**
AppManager désinstalle un jeu ou un programme, puis retrouve tout ce que lui et Windows ont laissé derrière eux : parties sauvegardées, paramètres, caches, captures d'écran, entrées du registre, démarrages automatiques, tâches planifiées, services, types de fichiers, entrées du menu contextuel, règles du pare-feu et raccourcis. Rien n'est supprimé avant que vous ayez vu la liste et confirmé.

[English](README.md) · [Italiano](README.it.md) · **Français** · [Deutsch](README.de.md) · [Español](README.es.md) · [Русский](README.ru.md) · [Українська](README.uk.md)

**[Télécharger](../../releases/latest)** · Windows 10 / 11 · portable, sans installation · 7 langues · thème sombre et clair

![Bibliothèque de jeux](docs/images/games-dark.png)

---

## En bref

- **Tout ce qui est installé, au même endroit** : Steam, Epic Games, GOG, Ubisoft Connect, EA app, Battle.net, Rockstar, Amazon Games, Xbox / PC Game Pass, applis du Microsoft Store, installateurs classiques, repacks et copies **portables**.
- **Désinstaller et nettoyer** : lance le bon programme de désinstallation, attend la fin, puis affiche chaque résidu classé par catégorie, avec la raison pour laquelle il a été trouvé et le degré de certitude (« Certain », « Probable », « À vérifier »).
- **Résidus d'applications déjà disparues**, y compris les jeux supprimés à la main.
- **Surveillance en arrière-plan** (facultative) : suit les installations et désinstallations depuis la zone de notification et vous prévient quand une désinstallation a laissé quelque chose.
- **Gestionnaire de parties** : sauvegardes, plusieurs profils de parties par jeu, « nouvelle partie » sans perdre l'ancienne.
- **La sécurité d'abord** : Corbeille et sauvegarde zip avant de supprimer, zones système protégées, rien n'est jamais supprimé sans votre confirmation.
- **Toujours portable** : tout se trouve dans un dossier `Dati` à côté du programme ; chaque PC a son propre profil.

## Captures d'écran

Les captures montrent la version anglaise ; le programme suit la langue de Windows.

### Aperçu
Le PC d'un coup d'œil : depuis combien de temps il est allumé, quand Windows a été installé, ce qui a changé depuis le dernier démarrage, l'espace disque et ce qui en occupe le plus. La liste des jeux et programmes s'exporte en CSV.

![Aperçu](docs/images/overview.png)

### Jeux
Tous les jeux installés avec jaquette, taille et lanceur. Le panneau de détail indique le dossier, l'éditeur, comment le jeu a été trouvé, et donne un accès rapide aux parties, à la désinstallation, au lancement, au dossier et au blocage d'Internet. Les jaquettes manquantes sont cherchées en ligne (Steam, PCGamingWiki, clé SteamGridDB facultative, Wikipédia). Sans jaquette, l'icône du jeu est utilisée.

![Jeux, thème clair](docs/images/games-light.png)

Les jeux sont trouvés même sans lanceur : grâce aux empreintes des moteurs de jeu (Unity, Unreal, CryEngine, Source, RPG Maker, Ren'Py…), aux signatures de cracks et d'émulateurs Steam (CODEX, RUNE, EMPRESS, Goldberg…), aux noms de repacks, aux jeux ajoutés à Steam à la main et aux exécutables que Windows se souvient d'avoir lancés, où qu'ils soient.

### Programmes
Programmes installés, applis du Microsoft Store, outils Steam et programmes portables. Les runtimes, pilotes et applis intégrées à Windows sont masqués derrière « Afficher les composants Windows », pour ne pas les supprimer par erreur. Un programme portable situé dans un projet de développement ou un dossier de documents est proposé sous la forme de son seul exécutable, jamais du dossier entier.

![Programmes](docs/images/programs.png)

### Désinstaller et nettoyer
AppManager lance la bonne désinstallation (Steam, le programme de désinstallation, Windows pour les applis du Store, suppression directe pour les copies portables). Une fois terminée, il cherche les résidus.

![Confirmation de désinstallation](docs/images/uninstall.png)

Les résidus sont classés par catégorie. Les éléments « Certain » et « Probable » sont présélectionnés, les éléments « À vérifier » non. La même fenêtre, ouverte avec **Données**, affiche les données d'un jeu ou d'un programme sans rien désinstaller.

![Examen des résidus](docs/images/leftovers-review.png)

Ce qui est recherché, en plus des dossiers de données dans `AppData`, `ProgramData` et `Documents` :
- démarrages automatiques (Run, RunOnce, état dans le Gestionnaire des tâches, dossier Démarrage), tâches planifiées et services du programme ;
- types de fichiers, entrées « Ouvrir avec », applis par défaut, menu contextuel, composants COM et extensions du shell ;
- dossiers ajoutés au `PATH`, paramètres de notification, historique de lancement, compatibilité, préférences GPU, règles du pare-feu ;
- pour les jeux : données Steam, dossiers Unity et Unreal, parties dans `My Games` et `Parties enregistrées`, dossiers des émulateurs Steam, caches de shaders.

Les entrées système ne sont proposées que si elles appartiennent vraiment au programme : ce qui pointe vers Windows n'est jamais touché.

### Résidus d'applications déjà supprimées
Une seule analyse retrouve les restes des jeux et programmes qui ne sont plus installés, ou supprimés à la main, et les répartit entre les sections Jeux et Programmes. Ce qui est clairement orphelin est sélectionné ; les données utilisées récemment sans cette preuve restent « À vérifier ».

![Résidus](docs/images/leftovers.png)

### Surveillance en arrière-plan
Désactivée par défaut. Une fois activée, AppManager démarre avec Windows et reste dans la zone de notification. Il suit les entrées de désinstallation de Windows et les bibliothèques Steam et Epic. Quand un programme ou un jeu disparaît, il attend la fin du programme de désinstallation puis cherche les résidus.

![Historique de la surveillance](docs/images/monitoring.png)

S'il reste quelque chose, un avis apparaît au-dessus de la zone de notification, sans couvrir la barre des tâches. **Voir et supprimer** ouvre la liste, **Ignorer** la ferme. L'avis disparaît au bout de 20 secondes et attend si un jeu en plein écran ou une présentation est en cours. Si plusieurs désinstallations se suivent, le même avis les regroupe. La page **Surveillance** conserve l'historique : installations, mises à jour et désinstallations, y compris celles faites avec AppManager, avec ce qui est resté et ce qui a déjà été supprimé. La surveillance ne supprime jamais rien d'elle-même.

![Avis](docs/images/notification.png)

### Parties et profils
Pour chaque jeu, **Gérer les parties** trouve tout seul ses dossiers de sauvegarde : Steam, `My Games`, `Parties enregistrées`, `AppData`, dossiers Unity et Unreal, dossiers des émulateurs Steam utilisés par les copies non officielles, et le dossier du jeu lui-même, où sauvegardent beaucoup de copies portables. Pour les parties impossibles à reconnaître par leur nom, il consulte aussi PCGamingWiki ; seul le titre du jeu est envoyé.
- **Sauvegardes des parties** en fichiers zip, restaurables à tout moment. Avant chaque restauration, les parties actuelles sont d'abord sauvegardées.
- **Profils** : plusieurs ensembles de parties pour le même jeu, par exemple un par joueur.
- **Nouvelle partie** : met de côté les parties actuelles et repart de zéro ; vous revenez au profil précédent quand vous voulez.

![Parties](docs/images/saves.png)

### Démarrage et réseau
**Démarrage** liste les programmes qui démarrent avec Windows et les active ou désactive comme le Gestionnaire des tâches. Désactiver ne supprime rien.

![Démarrage](docs/images/startup.png)

**Réseau** montre en temps réel quels programmes en cours sont connectés, et vers où. Vous pouvez bloquer une seule destination, ou tout l'accès à Internet d'un programme, avec le Pare-feu Windows. Chaque règle se supprime en un clic depuis les Paramètres.

![Réseau](docs/images/network.png)

### Paramètres
Thème (clair, sombre ou comme Windows), langue, limite d'espace pour les images téléchargées, choix par défaut pour la Corbeille et la sauvegarde, surveillance en arrière-plan, dossiers portables, méthodes de recherche et règles réseau.

![Paramètres](docs/images/settings.png)

## Sécurité

- **Rien n'est supprimé sans votre confirmation.** Vous voyez toujours la liste complète, avec la raison et le degré de certitude de chaque élément.
- **Corbeille et sauvegarde** : par défaut, les fichiers supprimés vont dans la Corbeille, dans un dossier par application, avec leurs chemins complets. Les entrées système y sont aussi enregistrées, sous forme de fichiers `.reg`, `.xml` et `.txt`. Parties, paramètres et entrées système sont également copiés dans une sauvegarde zip, restaurable depuis la section **Sauvegardes**. Si la sauvegarde échoue, le nettoyage ne démarre pas.
- **Zones protégées** : Windows, les dossiers principaux de Program Files et ProgramData, les composants de Windows, les dossiers de l'utilisateur (Documents, Bureau, Téléchargements, AppData…), les dossiers système à la racine des disques, les lanceurs et les dossiers de tous les jeux et programmes installés. Un dossier qui en contient un protégé n'est jamais supprimé.
- **Les liens ne sont jamais suivis** : seule la jonction ou le lien symbolique est supprimé, jamais ce vers quoi il pointe.
- **Chemins inhabituels** (noms courts, variables, `..`, chemins réseau et `\\?\`) : ils sont ramenés au vrai chemin avant toute vérification, ou refusés. Les chemins de plus de 260 caractères sont pris en charge.
- **Une seule copie à la fois**, paramètres enregistrés de façon atomique, et chaque opération notée dans le journal d'activité.

## Confidentialité

AppManager se connecte à Internet uniquement pour chercher les jaquettes des jeux et les dossiers de parties. Pour les parties, il consulte PCGamingWiki en n'envoyant que le titre du jeu. Les deux recherches peuvent être désactivées. La clé SteamGridDB, facultative, n'est envoyée qu'à SteamGridDB. Aucune télémétrie. Chaque PC est reconnu par une empreinte anonyme de son identifiant Windows ; l'identifiant lui-même n'est jamais enregistré ni envoyé.

## Langues

English, Italiano, Français, Deutsch, Español, Русский, Українська. La langue suit celle de Windows (anglais si elle n'est pas disponible) et se change dans les Paramètres.

## Télécharger et utiliser

1. Téléchargez `AppManager.exe` depuis la [dernière version](../../releases/latest).
2. Placez-le dans n'importe quel dossier, même sur une clé USB, et lancez-le. Windows demande les droits d'administrateur ; ils servent pour le Prefetch, les clés de registre système, les services, les tâches planifiées système et les règles du pare-feu. Sans eux, AppManager fonctionne en « Mode limité » et trouve et nettoie quand même les données de votre utilisateur.

Configuration requise : Windows 10 ou 11. Il utilise .NET Framework 4.8, déjà inclus dans Windows. Pour mettre à jour, remplacez `AppManager.exe` : le dossier `Dati` reste.

**Clavier** : Ctrl+F recherche · F5 actualise · Suppr désinstalle l'élément sélectionné (ouvre seulement la confirmation) · Échap ferme · Entrée confirme (jamais pour les actions qui suppriment quelque chose).

## Compiler depuis les sources

Pas besoin de Visual Studio : le compilateur C# inclus dans Windows suffit (C# 5, .NET Framework 4.8).

```
cd Source
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1 -AsInvoker` crée `bin\AppManager.Test.exe` sans demande d'administrateur, pour les tests. `build.ps1 -Harness` crée aussi `tools\Harness.exe`, l'outil de test en console :

| Commande | Ce qu'elle vérifie |
|---|---|
| `robust <dossier>` | cas limites : chemins protégés, liens, chemins longs, noms Unicode, fichiers verrouillés, sauvegardes altérées (données fictives uniquement) |
| `invariants` | en lecture seule : tout ce que les analyses proposent sur ce PC respecte les règles de sécurité |
| `savetest <dossier>` | gestionnaire de parties : sauvegardes, nouvelle partie, changement de profil, annulation en cas d'erreur, restauration |
| `recycletest <dossier>` | Corbeille organisée par application, sans utiliser la vraie Corbeille |
| `pctest <dossier>` | reconnaissance du PC, ressources, démarrage, export, traductions, détection des copies piratées et portables, logique de la surveillance |
| `logtest <dossier>` | journal d'activité |
| `watchtest <dossier>` | test complet de la surveillance avec une fausse application créée puis supprimée par le test |

`pctest`, `logtest` et `watchtest` demandent `APPMANAGER_DATA` sur le même dossier de test ; `savetest` et `recycletest` sur un dossier différent.

### Structure du projet

```
Source/
  src/core   détection, recherche des résidus, nettoyage, sauvegardes, parties, surveillance (sans interface)
  src/app    ViewModels WPF, icône de la zone de notification, avis
  ui         XAML (chargé au démarrage) et traductions dans ui/lang/*.txt
  tools      outil de test en console (Harness)
  build.ps1  script de compilation
docs/images  captures d'écran
```

Les textes sont écrits en italien dans le code et le XAML et servent de clés ; les traductions sont dans `ui/lang/*.txt`.

## Licence

[MIT](LICENSE) © Crover81
