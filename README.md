# Signal Watchdogs

Chien de garde Windows qui surveille tout ce qui peut faire « sauter » ou noircir un écran sur un poste
multi-écrans : connectique HDMI / DisplayPort / USB-C, carte graphique et pilote, USB / docks, sessions
Bureau à distance, applications de visioconférence et de partage d'écran (Teams…). Chaque anomalie est corrélée en
**incident** avec une **cause probable** et des **actions concrètes**.

- **Installateur** : `SignalWatchdogs-Setup-<version>.exe` (Inno Setup), installation par machine dans Program Files, installation et désinstallation silencieuses pour le déploiement sur un parc. Prérequis : .NET Framework 4.8, déjà intégré à Windows 10/11.
- **100 % local** : aucun port ouvert, aucune connexion sortante, aucune télémétrie.
- **Léger** : ~1 % d'un cœur CPU et ~80 Mo de RAM en arrière-plan (mesuré sur un poste à 3 écrans et 2 GPU).
- **Interface** : 10 pages en thème sombre, icône de notification, alertes système, rapport HTML exportable.

## Ce qui est surveillé

| Domaine | Source | Détecte |
|---|---|---|
| Écrans | `QueryDisplayConfig` toutes les 250 ms + `WM_DISPLAYCHANGE` | Perte / retour de chaque écran avec **durée du noir**, changement de résolution / fréquence / écran principal, connectique (HDMI, DP, DP via USB-C, DisplayLink, virtuel), modèle et n° de série (EDID) |
| Écrans | Notifications PnP (interface moniteur) | Signal Hot-Plug perdu / retrouvé |
| Alimentation | `GUID_CONSOLE_DISPLAY_STATE`, veille / reprise, capot, secteur / batterie | Écran éteint par Windows (distingue la veille normale d'une panne) |
| GPU | Journal System : `Display 4101`, `nvlddmkm`, `amdkmdag`, `igfx`, `BasicDisplay`, DxgKrnl Admin/Operational | **TDR** (plantage + relance du pilote), erreurs pilote |
| GPU | Rapports WER `LiveKernelEvent 141 / 117 / 1A8 / 193`, `WerKernel`, `C:\Windows\LiveKernelReports` | Blocages GPU, « écran noir » détecté par le noyau graphique |
| GPU | DXGI + WMI + compteurs PDH + `nvidia-smi` | Inventaire, version / âge du pilote, charge 3D / copie / **encodeur** (partage d'écran, streaming) / décodeur, VRAM, température, lien PCIe, GPU perdu (« fallen off the bus ») |
| Compositeur | Processus `dwm.exe` / `explorer.exe`, plantages applicatifs | Redémarrage ou plantage de DWM (tous les écrans clignotent) |
| USB / USB-C | cfgmgr32 (inventaire différentiel), PnP, journaux USBHUB3 / xHCI / **UCSI** | Dock / hub qui décroche, contrôleur USB-C en défaut, périphériques en erreur (code 10, 43…) |
| Sessions | `WTSRegisterSessionNotification`, LocalSessionManager, client RDP | Connexions RDP entrantes / sortantes, bascules console |
| Applications | Liste surveillée (mstsc, Teams, Zoom, AnyDesk, TeamViewer, OBS, Webex, DisplayLink…) | Applis actives au moment de l'incident |
| Système | Thread haute priorité | Gels système (aucun thread exécuté pendant plus de 1,5 s) |
| Système | Kernel-Power 41, BugCheck, WHEA (PCIe) | Redémarrages brutaux, erreurs matérielles PCI Express |
| Utilisateur | Raccourci **Ctrl+Alt+Maj+B** / bouton | Signalement d'un écran noir, corrélé avec les 2 minutes précédentes |

Au démarrage, Signal Watchdogs **analyse aussi les 30 derniers jours** de journaux Windows (onglet *Historique Windows*)
et reconstitue les incidents passés : un poste qui a déjà fait des TDR est identifié immédiatement.

## Diagnostics produits

| Cause | Indice clé |
|---|---|
| Plantage du pilote graphique (TDR) | `Display 4101`, `nvlddmkm`, LiveKernelEvent 141/117/1A8 |
| Carte graphique perdue | GPU disparu de DXGI / WMI, `nvidia-smi` « GPU is lost » |
| Redémarrage du compositeur DWM | Nouveau PID de `dwm.exe` |
| Décrochage du dock / USB-C | Écran perdu en même temps qu'un hub USB / erreur xHCI / UCSI |
| Perte de signal sur un seul écran | Un écran perdu puis revenu, les autres stables : câble, port, écran |
| Toutes les sorties d'une carte ont décroché | Tous les écrans d'un même GPU perdus ensemble sans TDR |
| Bascule de session RDP | Connexion / déconnexion RDP ou console dans la même fenêtre |
| Mise en veille des écrans | Extinction par Windows juste avant (veille profonde DP) : journalisé, pas d'alerte |
| Écran virtuel (IddCx) | Ajout ou retrait d'un adaptateur d'affichage virtuel |
| Aucune anomalie vue par Windows | Signalement utilisateur sans événement OS : coupure côté câble / écran (HDCP, mode éco) |

L'onglet **Diagnostic** audite aussi la configuration : écrans répartis sur deux GPU (iGPU + carte dédiée),
MPO, HAGS, TdrDelay / TdrLevel, suspension sélective USB, ASPM PCIe, pilote ancien, écrans virtuels installés,
DisplayLink, docks, périphériques en erreur.

## Installation

Lancer `SignalWatchdogs-Setup-<version>.exe` (droits administrateur requis pour installer) :

- installation dans `C:\Program Files\Signal Watchdogs`. Le dossier est imposé : il est protégé, donc un utilisateur ne peut pas remplacer l'exe ;
- raccourci dans le menu Démarrer ;
- option cochée par défaut : lancement automatique, réduit dans la zone de notification, à l'ouverture de session de chaque utilisateur (clé `HKLM\…\Run`). L'application tourne alors **avec les droits de l'utilisateur**, pas en administrateur ;
- page « Journaux » : vide = journaux locaux dans `C:\ProgramData\Signal Watchdogs\Logs`, ou partage réseau dédié pour la vue parc.

### Déploiement silencieux (GPO, Intune, SCCM, script)

```bat
SignalWatchdogs-Setup-1.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOGDIR=\\serveur\wd-logs$
:: sans lancement automatique :  /MERGETASKS="!autostart"
:: journal d'installation      :  /LOG="C:\Windows\Temp\SignalWatchdogs-install.log"
```

Une mise à jour se déploie avec la même commande. Les instances en cours sont arrêtées, puis l'exe est remplacé ;
les réglages et les journaux sont conservés.

### Désinstallation

*Paramètres > Applications > SignalWatchdogs*, ou en silencieux :

```bat
"C:\Program Files\Signal Watchdogs\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Les journaux de `C:\ProgramData\Signal Watchdogs` sont conservés ; la désinstallation interactive propose de les supprimer.

## Utilisation

1. Le logiciel démarre seul à l'ouverture de session ; un double-clic sur l'icône de la zone de notification ouvre le tableau de bord.
2. Fermer la fenêtre la réduit dans la zone de notification ; *clic droit > Quitter* arrête la surveillance.
3. À chaque écran noir : **Ctrl+Alt+Maj+B**.
4. Consulter **Incidents** et exporter le **rapport HTML** pour le support.

Le mode administrateur n'apporte que la lecture de `C:\Windows\LiveKernelReports` (voir [Sécurité](#sécurité)).

| Fichier (version installée) | Rôle |
|---|---|
| `C:\Program Files\Signal Watchdogs\SignalWatchdogs.ini` | Réglages machine, posés par l'installation (non modifiables par les utilisateurs) |
| `%LOCALAPPDATA%\Signal Watchdogs\SignalWatchdogs.ini` | Préférences de l'utilisateur : seules les valeurs qui diffèrent des réglages machine y sont écrites |
| `C:\ProgramData\Signal Watchdogs\Logs\<POSTE>\` | Journaux du poste, communs à tous ses utilisateurs |
| `C:\Program Files\Signal Watchdogs\SignalWatchdogs_Logs\` | Journaux d'une instance lancée en administrateur (séparés par sécurité) |

```text
SignalWatchdogs.exe --minimized                 # démarre dans la zone de notification
SignalWatchdogs.exe --report C:\rapport.html    # diagnostic sans interface (scriptable)
SignalWatchdogs.exe --logdir \\serveur\wd-logs$ # force le dossier des journaux
```

### Plusieurs postes

Avec un dossier des journaux commun (`/LOGDIR=` à l'installation), chaque poste dépose son `status.json` dans
`<partage>\<NOM DU POSTE>\`. L'onglet **Vue parc**, ouvert par l'IT, compare alors tous les postes.

Droits NTFS conseillés sur le dossier des journaux :

```bat
icacls D:\wd-logs /inheritance:r
icacls D:\wd-logs /grant "Utilisateurs du domaine:(AD,RA,RD,X)" "CREATOR OWNER:(OI)(CI)(IO)(M)" "*S-1-5-32-544:(OI)(CI)(F)"
```

Chaque utilisateur peut créer son dossier de poste et y écrire, sans lire ni modifier ceux des autres.
L'IT (administrateurs) lit l'ensemble, notamment via la Vue parc. Sur un poste partagé entre plusieurs
utilisateurs, le dossier appartient au premier qui l'a créé ; les suivants basculent automatiquement sur
des journaux locaux (`%LOCALAPPDATA%\Signal Watchdogs\Logs`).

## Sécurité

**Surface d'attaque minimale**

- Aucun port réseau ouvert, aucune connexion sortante, aucune télémétrie. Le seul trafic réseau éventuel est l'écriture des journaux sur le partage configuré.
- Aucune dépendance tierce (pas de NuGet) : uniquement les bibliothèques de .NET Framework intégrées à Windows.
- Surveillance en lecture seule : Signal Watchdogs ne modifie aucun réglage du système. Les correctifs (MPO, TdrDelay…) sont seulement proposés en texte. Seule exception : la clé `HKCU\…\Run`, si l'utilisateur coche « Démarrer avec Windows ».
- Le raccourci de signalement utilise `RegisterHotKey` : il ne reçoit que cette combinaison, ce n'est pas un enregistreur de frappe.
- Manifeste `asInvoker` : aucune élévation automatique.

**Durcissement dans le code**

- Les DLL système appelées par P/Invoke ne sont chargées que depuis `System32` (`DefaultDllImportSearchPaths` et `SetDefaultDllDirectories`) : pas de détournement par une DLL déposée à côté de l'exe.
- Rapport HTML : toutes les données (messages de journaux, noms de périphériques USB, noms d'écrans) sont encodées.
- Export CSV : neutralisation des formules Excel (`=`, `+`, `-`, `@`).
- JSON lu avec `JavaScriptSerializer`, sans résolution de type (pas de désérialisation d'objets arbitraires), taille bornée.
- Vue parc : les `status.json` des autres postes sont des données non fiables. Aucun chemin issu de leur contenu n'est ouvert ; seul le dossier réellement lu l'est, et après vérification.
- Nettoyage des journaux limité aux fichiers portant exactement les noms créés par Signal Watchdogs.
- `nvidia-smi` est lancé par chemin absolu (System32 ou Program Files), jamais via le `PATH`.
- « Relancer en administrateur » est refusé si l'exe ou son dossier est modifiable par un non-administrateur, ou s'il est sur un partage, un lecteur amovible ou un chemin contenant une jonction. Lancé en administrateur depuis un tel emplacement, Signal Watchdogs le signale comme **Problème** dans l'onglet Diagnostic.
- En administrateur, Signal Watchdogs ignore le fichier de réglages et le dossier de repli du profil utilisateur, et refuse tout dossier de journaux qui passe par une jonction ou un lien symbolique. `status.json` est remplacé de façon atomique.
- Anti-saturation : double limitation des événements (par titre et par source), au plus 1 000 événements et 15 minutes par incident, fichiers journaliers plafonnés à 256 Mo.
- Un faux « dwm.exe » lancé dans la session ne déclenche pas d'alerte : seule la disparition du compositeur suivi compte. Un faux message de mise en veille ne masque la détection que 5 minutes au plus.
- Journal texte nettoyé des caractères de contrôle et d'inversion de texte (pas d'injection de fausses lignes).
- Vue parc : le nom affiché est celui du dossier, pas celui déclaré dans le fichier. Une incohérence est signalée en rouge. La lecture est limitée à 5 000 postes et 1 Mo par fichier.
- Rapport HTML protégé par une Content-Security-Policy stricte (aucun script, aucune ressource externe).

**Risques résiduels assumés**

- Un utilisateur local peut écrire dans le journal *Application* de Windows, et donc **fabriquer de fausses alertes** (par exemple un faux plantage de DWM). Signal Watchdogs est un outil de diagnostic, pas un outil de détection d'intrusion : il ne faut pas s'en servir comme preuve.
- Sur un partage de journaux commun, un utilisateur peut créer le dossier d'un autre poste avant lui (usurpation dans la Vue parc). En environnement strict, l'IT pré-crée un dossier par poste.

**Ce qui reste à la charge du déploiement**

- **Emplacement de l'exe** : lecture seule pour les utilisateurs. C'est le point le plus important. L'installateur le garantit (Program Files, dossier imposé) ; seul l'usage de l'exe hors installation demande de la vigilance.
- **UAC activé** : sur un poste où le contrôle de compte d'utilisateur est désactivé (`EnableLUA = 0`), tout programme lancé par un administrateur, Signal Watchdogs compris, a les pleins pouvoirs.
- **Signature** : l'exe n'est pas signé. Pour un parc, le signer avec le certificat de signature de code de l'entreprise (`signtool`), ce qui permet aussi de l'autoriser par AppLocker / WDAC. À défaut, diffuser son empreinte SHA-256 (`Get-FileHash`).
- **Confidentialité / RGPD** : les journaux contiennent des données personnelles (noms d'utilisateurs, heures d'ouverture de session, adresses IP des connexions RDP, applications ouvertes). Restreindre la lecture à l'IT, garder la rétention courte (60 jours par défaut) et informer les salariés, voire le CSE : c'est un outil de supervision des postes.
- **Moindre privilège** : utiliser le mode standard par défaut.

## Journaux

| Fichier | Contenu |
|---|---|
| `signalwatchdogs-AAAA-MM-JJ.log` | Journal lisible |
| `events-AAAA-MM-JJ.jsonl` | Tous les événements (1 objet JSON par ligne) |
| `incidents-AAAA-MM-JJ.jsonl` | Incidents corrélés + diagnostic + chronologie |
| `metrics-AAAA-MM-JJ.csv` | CPU / RAM / charge GPU / VRAM / encodeur / température toutes les 10 s |
| `status.json` | Résumé du poste (Vue parc) |

Rétention par défaut : 60 jours (réglable).

## Limites

- Une coupure **purement électrique** (renégociation HDCP, écran qui perd le signal sans signaler de
  déconnexion Hot-Plug) est invisible pour Windows. C'est la raison d'être du raccourci de signalement :
  « écran noir signalé + aucun événement Windows » est en soi un diagnostic (câble / écran).
- La télémétrie avancée (température, PCIe) n'est disponible que pour NVIDIA via `nvidia-smi`.
- Sans droits administrateur, le dossier `LiveKernelReports` peut être inaccessible. Les autres sources
  fonctionnent ; leur état est visible dans *Diagnostic > Sources de surveillance*.
- Installateur et exe non signés : SmartScreen ou l'antivirus peuvent demander une confirmation au premier lancement.

## Compilation

Prérequis : SDK .NET (6 ou plus), pack de ciblage .NET Framework 4.8 et Inno Setup 7.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1             # tests + compilation → dist\SignalWatchdogs-Setup-<version>.exe
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Portable   # ajoute aussi l'exe seul (dépannage sans installation)
```

Pour signer : configurer un SignTool dans Inno Setup (`ISCC /S…`) et ajouter `SignTool=` dans `installer\SignalWatchdogs.iss`.
La version vient de `<Version>` dans `SignalWatchdogs.csproj`.

## Structure

```text
src/Core          bus d'événements, journalisation, moteur d'incidents, règles de diagnostic
src/Native        P/Invoke : QueryDisplayConfig, cfgmgr32, DXGI, PDH, notifications
src/Monitors      sondes : topologie, fenêtre de notifications, journaux Windows, GPU, PnP, processus, gels
src/Diagnostics   audit de configuration, rapport HTML
src/UI            interface WinForms (thème sombre, graphiques, frise, carte des écrans)
tests/            scénarios du moteur de diagnostic (dotnet build tests\SignalWatchdogs.Tests)
installer/        script Inno Setup (SignalWatchdogs.iss)
```
