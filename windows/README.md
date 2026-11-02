# FanController — client Windows local

Client **desktop WinUI 3 / .NET 10**, interface française, **Windows 11 x64 uniquement**.
BLE natif Windows ; aucun navigateur, Azure, cloud, backend ni compte applicatif.
Appairage BLE persistant autorise physiquement sur la carte (firmware 1.1.0).
Les seules mesures affichées en usage normal viennent de la carte.

## Construire, tester, lancer

Depuis `C:\Code\Arduino\FanController\windows`, PowerShell :

```powershell
dotnet restore .\FanController.sln
dotnet build .\FanController.sln -c Release -p:Platform=x64
dotnet test .\tests\FanController.Tests\FanController.Tests.csproj -c Release
dotnet publish .\src\FanController.App\FanController.App.csproj -c Release -r win-x64 -p:Platform=x64 -o .\artifacts\desktop
.\artifacts\desktop\FanController.App.exe
```

En développement :

```powershell
dotnet run --project .\src\FanController.App\FanController.App.csproj -p:Platform=x64
```

Prérequis de compilation : SDK .NET **10.0.114** (`global.json`), outils Windows
SDK/XAML et BuildTools Visual Studio 18 (ici
`C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools`).
Le SDK 8.0.427 seul ne suffit pas. La restauration initiale exige NuGet.
Le Windows SDK de ciblage est 10.0.22621, minimum d'exécution Windows 11
10.0.22000. Le package BuildTools fournit les outils PRI/XAML.

La publication est **non empaquetée, self-contained .NET et Windows App SDK** :
pas de MSIX, d'installation du runtime .NET ni du runtime Windows App SDK.
Distribuer **tout** `artifacts\desktop`, pas uniquement l'EXE. Le fichier
`FanController.App.pri` contient le XAML et est explicitement ajouté à la
publication CLI. Les DLL natives et sous-dossiers de langues sont nécessaires.
Les pilotes Bluetooth et composants système Windows restent requis.

### Dépendances épinglées

Versions stables vérifiées le **2 novembre 2026** sur NuGet :

| Usage | Package | Version |
|---|---|---|
| WinUI 3, runtime desktop | Microsoft.WindowsAppSDK | **2.6.4** |
| Outils de compilation Windows | Microsoft.Windows.SDK.BuildTools | **10.0.28000.2957** |
| Tests uniquement | Microsoft.NET.Test.Sdk | 18.11.0 |
| Tests uniquement | xunit | 2.9.3 |
| Tests uniquement | xunit.runner.visualstudio | 4.0.1 |

Le [canal stable Microsoft](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-channels)
référence Windows App SDK 2.6.4, publié le 27 octobre 2026.
[Métadonnées NuGet](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/2.6.4/microsoft.windowsappsdk.nuspec).
Aucune preview. Pas de bibliothèque BLE ni MVVM supplémentaire : API Windows,
`System.Text.Json`, `INotifyPropertyChanged` et `ICommand`.

## Utilisation

1. Alimenter l'ESP32 avec le firmware décrit dans `..\esp32\README.md`.
   Activer Bluetooth dans Windows ; autoriser l'accès s'il est demandé.
   Premier appairage : apres le demarrage normal, maintenir **BOOT 2 s puis
   relacher** ; connecter sous **60 s**. Ne pas maintenir BOOT pendant un reset.
   Un seul PC proprietaire ; ses cles sont reutilisees aux connexions suivantes.
2. **Rechercher (15 s)**, sélectionner `ESP32-FanController`, **Connecter**.
   Une annonce UART sans nom est listée en attendant sa scan response.
   La connexion abonne TX puis relit configuration et statut, sans écriture
   de configuration et sans réapplication de brouillon.
3. Lire les deux températures (ou « Indisponible »), défauts/sécurités, quatre
   RPM, PWM cible/appliqué, PWM fixe, modes, boost et état des sorties PWM.
   Polling environ 2 s **après** la réponse précédente. Après déconnexion,
   les dernières mesures restent affichées mais sont explicitement périmées.
   Les cartes sont créées une seule fois et leurs propriétés sont actualisées
   sur place, sans vider les listes ni réinitialiser le défilement à chaque mesure.
4. Éditer les deux profils et modes. Températures à virgule française,
   PWM avec décimales acceptées. Chaque profil exige 2 à 8 points, −20 à 120 °C strictement
   croissants, PWM de 0 à 100 % non décroissants. Un nouveau point vide de
   sens (initialisé à 0/0) doit être corrigé avant application.
   « Retirer » supprime le dernier point ; les autres restent éditables.
   Chaque ventilateur a son propre mode Automatique/Fixe et son PWM fixe ;
   le champ PWM est ignoré en automatique. Les sécurités firmware priment.
5. **1. Appliquer en RAM** envoie les deux profils puis les quatre modes,
   attend un cycle de contrôle et relit configuration/statut. Cela **ne
   sauvegarde pas** la NVS. Le brouillon est figé pendant les actions.
6. **2. Enregistrer la RAM en NVS** envoie uniquement `save` puis relit.
   Ce bouton exige un brouillon identique à la configuration lue :
   appliquer ou abandonner d'abord les modifications locales.
   Le dirty **local** et le dirty **RAM distante / NVS** sont distincts.
7. **Annuler les modifications** remet les champs a la derniere configuration
   lue sur l'ESP32, sans ecriture BLE. La configuration est lue automatiquement
   a chaque connexion. Les controles techniques de relecture et de remise
   a zero ne sont plus exposes dans l'interface.

Une application de six commandes n'est **pas atomique**. En cas de rupture,
certaines modifications peuvent avoir pris effet sans réponse. Toute erreur
GATT/protocole, annulation en vol ou expiration ferme et invalide la session.
Une reconnexion relit l'état réel et **conserve** le brouillon
préexistant, désormais éventuellement différent. Vérifier/reconcilier avant
de réappliquer ; aucun retry d'écriture.
Le brouillon est conservé pendant les déconnexions, pas après fermeture du logiciel.

### Connexion et reconnexion automatiques

Apres une connexion et une lecture initiale reussies, l'adresse du controleur
est memorisee dans `%LOCALAPPDATA%\FanController\last-device.txt`. Au prochain
lancement, seul ce controleur est reconnecte automatiquement, et uniquement
s'il est toujours appaire dans Windows. Aucun autre appareil n'est choisi,
meme s'il est le seul detecte ; le premier choix reste manuel.

Apres une perte de liaison, les tentatives continuent tant que l'application
tourne, avec delais progressifs de 2 a 30 secondes (plafond 30 s entre les
tentatives, en plus du temps de connexion). Elles n'ouvrent jamais un nouvel
appairage. La reconnexion abonne TX et relit configuration/statut, sans envoyer
de `set_profile`, `set_fan`, `save` ou `defaults`. Une ecriture interrompue
n'est jamais rejouee : verifier l'etat relu et le brouillon avant de reappliquer.

Le formulaire recherche/appairage est masque une fois connecte ; il reste
accessible si la connexion echoue, si le controleur est absent ou s'il n'est
plus appaire. **Deconnecter / suspendre la reconnexion** interrompt les
tentatives jusqu'a un clic sur Connecter ou au prochain lancement.
Lancer une recherche manuelle suspend aussi les tentatives pour ne pas
concurrencer le choix d'un autre appareil.

### Interface, courbes et arriere-plan

Les trois onglets partagent les memes marges de contenu. Leurs zones de
defilement occupent toute la largeur : les scrollbars sont au bord droit
de la fenetre et ne sont pas placees dans les marges des cartes.

- **Tableau de bord** : mesures en direct, securites et version du firmware.
  Les commandes de connexion sont dans Parametres, pas sur le tableau de bord.
- **Parametres** : courbes par sonde et modes auto/fixe par ventilateur.
  L'encart Connexion affiche le nom fourni par Windows, l'adresse BLE et
  la puissance du signal RSSI en dBm, recue par l'ESP32 sur la liaison active
  (actualisee a chaque lecture des mesures, toutes les deux secondes environ).
  Cette mesure exige le firmware **1.1.3 ou ulterieur**. Plus la valeur est
  proche de zero, plus le signal est fort. Elle mesure le sens Windows vers
  ESP32, contrairement au scan effectue par Windows ; les valeurs peuvent
  donc differer. Le RSSI du scan n'est jamais reutilise comme signal actuel.
  Ancien firmware ou mesure echouee : signal indique indisponible.
  Hors connexion, la qualite est indiquee indisponible ;
  le nom et l'adresse restent ceux du dernier controleur tente.
  Les confirmations d'application et d'enregistrement sont affichees pres
  des boutons de configuration, pas dans l'encart Connexion.
  Les segments representent l'**interpolation lineaire**, pas des marches :
  30 °C / 40 % et 40 °C / 60 % donnent 50 % a 35 °C.
  Le toggle **Lecture / Edition**, a droite du titre des courbes, affiche ou masque
  l'edition des points sans effacer le brouillon. Il ne modifie pas le tableau
  de bord. En edition et connecte, glisser un point directement sur le graphique
  ajuste temperature et PWM au dixieme ; les axes restent fixes (-20 a 120 °C,
  0 a 100 %) pendant l'edition. Le deplacement est limite par les points voisins
  pour conserver temperatures croissantes et PWM non decroissant.
  Les champs numeriques restent utilisables au clavier.
  Chaque ligne dispose d'une corbeille ; conserver au moins deux points est
  obligatoire. Deplacement et suppression modifient seulement le brouillon.
  Les boutons + et corbeille sont aussi presents en bas a droite du graphique.
  + insere et selectionne un point au milieu du plus grand intervalle de
  temperatures, avec un PWM interpole : la forme de la courbe est conservee.
  Cliquer sur un point le selectionne (contour blanc) pour le supprimer.
  L'ajout est desactive pour un profil invalide ou deja compose de huit points.
  Le parcours est Modifier / Appliquer en RAM / Enregistrer sur
  l'ESP32, avec un court statut et un bouton pour annuler les edits locaux.
  Les informations techniques RAM/NVS ne sont pas affichees ; les boutons
  Appliquer RAM / Enregistrer ESP32 restent distincts.
- **Fermer ou reduire** masque dans la zone de notification et conserve la
  connexion/acquisition. Double-clic sur l'icone ou menu **Ouvrir** pour revenir.
  Relancer l'EXE restaure l'instance deja ouverte, sans seconde acquisition.
  **Quitter** (fenetre ou menu de l'icone) termine reellement le programme.
  La case **Lancer au demarrage de Windows**, dans Parametres, active le
  lancement dans le systray a l'ouverture de la session Windows. Elle est
  desactivee par defaut et ne necessite pas de droits administrateur.
  La reconnexion automatique fonctionne aussi lorsque la fenetre est masquee.
  Un lancement manuel restaure la fenetre.

Ce reglage utilise uniquement la valeur `FanController` de
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, avec le chemin complet
de l'executable et l'argument `--startup`. Decocher supprime cette valeur.
Si le dossier de publication est deplace, reactiver la case depuis le nouvel
emplacement. Garder le dossier complet de publication sur le disque.
Une desactivation par Windows ou un outil systeme peut empecher le lancement.

L'icone ventilateur est commune a l'executable, a la fenetre/barre des taches
et au systray. Le fichier [ICO multi-resolution](src/FanController.App/Assets/FanController.ico)
contient neuf tailles de 16 a 256 pixels ; un
[apercu PNG](src/FanController.App/Assets/FanController.png) est fourni.
Pour regenerer ces ressources sans dependance supplementaire, executer
`powershell.exe -NoProfile -File .\scripts\New-AppIcon.ps1` sur Windows.

### Historique local

L'onglet **Historique** trace temperatures et RPM sur 1 h, 24 h, 7 ou 30 jours.
Les donnees sont conservees entre les lancements ; le controleur peut etre
selectionne sans connexion BLE pour consulter les fichiers deja presents.
Chaque statut recu (lecture initiale, polling et relecture apres action) est
enregistre dans `%LOCALAPPDATA%\FanController\history`, fichier JSONL par
controleur et jour UTC. La retention est de **30 jours** : nettoyage au lancement,
lors d'une actualisation et pendant l'acquisition ; suppression par jour entier.
Le graphique filtre aussi exactement l'intervalle demande.

Les traces sont agregees en 600 intervalles en preservant les extrema et
des limites de sessions ; les fichiers gardent les echantillons originaux.
Les temperatures en defaut et les reconnexions interrompent les lignes.
Les coupures d'acquisition de plus de 30 secondes sont detectees **avant**
l'agregation pour ne pas couper artificiellement les courbes longues.
Rafraichissement visuel environ 10 s ; la frequence d'enregistrement suit les
reponses reelles, pas un echantillonnage garanti de 2 s.
Ni collecte ni rattrapage lorsque le PC dort, le logiciel est quitte ou le BLE
est deconnecte. Une erreur disque/fichier est affichee et journalisee sans
couper la regulation ni la connexion BLE.

## Architecture

```text
FanController.sln
src\FanController.Core\
  Models.cs                 contrat JSON v1 et validation
  AdvertisementIndex.cs     fusion annonce / scan response par adresse
  LineFramer.cs              accumulation d'octets, décodage UTF-8 strict à LF
  ProtocolSession.cs         requête unique, fragmentation, délai, invalidation
  ControllerService.cs       transactions et polling sérialisés
  IBleTransport.cs           frontière testable
  HistoryStore.cs            fichiers JSONL, retention et agregation bornee
  PlotSeries.cs              donnees des graphiques
src\FanController.App\
  Bluetooth\                 watcher actif et GATT natifs Windows
  ViewModels\                commandes, édition, état local et télémétrie
  Controls\LineChart.cs       graphiques WinUI natifs, sans package graphique
  TrayIcon.cs                zone de notification et restauration
  MainWindow.xaml            interface française ; code-behind de composition
tests\FanController.Tests\   faux transport uniquement dans les tests
scripts\Smoke-Desktop.ps1    contrôle réel de fenêtre et commandes UI Automation
```

Contrat : UART `6e400001-b5a3-f393-e0a9-e50e24dcca9e`, RX `002`
WriteWithResponse, TX `003` Notify. Handler puis CCCD TX **avant** RX.
Scan actif sans filtre de service exclusif : le nom peut être dans une scan
response séparée, fusionnée par adresse avec l'annonce précédente.
Pendant le scan, chaque appareil conserve le meme objet dans la liste et la
selection n'est pas remplacee. Une annonce identique ne declenche aucune
notification d'interface ; seuls les changements de nom ou de RSSI actualisent
le libelle, sans reconstruire la liste deroulante.
La découverte GATT énumère tous les services sans cache puis sélectionne le
service UART. La découverte Windows directement filtrée par UUID échouait
avec `ERROR_BAD_COMMAND` (`0x80070016`) sur le poste testé, contrairement à
l'énumération complète également utilisée par BowAnalyzer.

Commandes JSON UTF-8 + LF, au plus 1 024 octets avant LF, blocs **20 octets**,
WriteWithResponse attendu pour chaque bloc. Réponse accumulée en **octets**
avant décodage strict, CRLF accepté ; garde-fou de réponse 32 Kio.
Délai de **20 secondes par requête**, incluant les écritures. Découverte
initiale limitée séparément à 35 s. Aucun identifiant de requête firmware :
sémaphore de protocole + sémaphore de transaction ; aucun chevauchement
polling/actions. Erreur, trame dupliquée/inattendue, JSON invalide, réponse
incomplète, commande différente ou délai dépassé invalident la session et
ferment le transport. Les octets tardifs de l'ancienne session sont ignorés.
Les propriétés de télémétrie manquantes sont rejetées, pas remplacées par
de fausses valeurs zéro.
Millivolts, RPM et PWM sont des nombres réels dans le firmware (moyenne ADC,
interpolation, calcul RPM) : le client accepte les valeurs fractionnaires,
ainsi que les profils et modes fixes fractionnaires, sans les tronquer.

## Vérification et limites

Validation exécutée le 2 novembre 2026 : **build Release sans erreur ni
avertissement, 72 tests réussis, publication self-contained et smoke test
de la fenêtre réelle (onglets, scan, masquage/restauration systray, instance unique
et sortie explicite) réussis**.
Détails : `.azure\verification.md`. Après correction de la découverte,
une connexion à l'ESP32 réel, la lecture de configuration/statut et cinq
lectures de télémétrie successives ont également réussi avec le transport et
le protocole de production depuis un outil console de diagnostic. Aucune
commande de modification ni sauvegarde NVS n'a été envoyée pendant cet essai.
Cela ne valide pas encore l'affichage WinUI en connexion réelle ni les capteurs.
Le nouvel appairage securise doit encore etre teste sur la carte apres
televersement de la version 1.1.0 ; la lecture materielle ci-dessus precede
cette evolution.

Les tests couvrent frontières UTF-8 à chaque octet, LF/CRLF, fragments,
limites de trame/profil, contrat JSON, indices, éditeurs français, modes,
scan response, sérialisation des requêtes/actions/polling, erreurs sans `cmd`,
GATT, timeout de lecture/écriture, annulation et réponse tardive, application
partielle, distinction RAM/save/defaults et relecture sans écriture.

Test UI réel, à exécuter dans une session Windows interactive (Windows PowerShell
5.1 pour UI Automation) après publication :

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Smoke-Desktop.ps1
# Optionnel : démarre seulement un scan radio, sans connexion ni écriture :
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Smoke-Desktop.ps1 -Scan
# Apres connexion manuelle reussie avec cette version puis Quitter :
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Smoke-Desktop.ps1 -AutoConnect
```

Le smoke test par defaut suppose qu'aucun dernier controleur n'est memorise.
Le mode `-AutoConnect` utilise les vraies cles et le dernier controleur
memorise, attend la connexion, verifie le formulaire masque et ne modifie
pas les profils. Ne pas combiner `-Scan` et `-AutoConnect`.

Les tests sans carte **ne prouvent pas** la radio, les permissions GATT du poste,
le comportement de ses pilotes, les valeurs ADC/TACH/PWM, ni la persistance NVS.
Recette matérielle : rechercher (nom en scan response), connecter, comparer les
mesures au moniteur série, tester chaque ventilateur et ses sécurités, changer
les deux profils, appliquer sans sauvegarder puis redémarrer, sauvegarder puis
redémarrer, couper la liaison pendant application/save et reconnecter pour
vérifier le résultat sans réapplication automatique. Tester aussi le refus
d'accès Bluetooth, la radio désactivée et une connexion déjà occupée.

Le firmware 1.1.0 exige une liaison **chiffree et appairee au proprietaire**.
Le client demande explicitement le chiffrement a Windows pour RX/TX.
**Just Works ne protege pas d'un MITM au premier appairage** : le faire a
proximite, dans un environnement de confiance. Pour changer de PC ou si Windows
a perdu les cles : **BOOT 10 s puis relacher**, supprimer l'appareil Bluetooth
dans Windows, puis BOOT 2 s et reconnecter. Les profils ne sont pas effaces.
Ne pas utiliser ce client pour continuer a exploiter l'ancien firmware sans
protection : televerser la version 1.1.0 d'abord.
Le client ne desactive aucune securite thermique. Aucun acces reseau
applicatif n'est necessaire apres publication.

## Dépannage Bluetooth

- Un resultat Windows `Paired` / `AlreadyPaired` est traite comme un succes :
  `ProtectionLevelUsed` ne suffit pas a determiner le chiffrement effectif
  d'une session GATT. Les caracteristiques RX/TX exigent toujours
  `EncryptionRequired`, et le firmware controle le proprietaire et les cles.
- Les echecs d'ecriture RX et d'abonnement TX affichent le code ATT quand
  Windows le fournit : `0x05` authentification, `0x08` autorisation,
  `0x0C` taille de cle, `0x0F` chiffrement, `0x01` handle invalide.
  `ProtocolError` seul ne permet pas de conclure a une panne radio.
- `BLE disconnected: 531` sur l'ESP32 correspond à `0x213` : fermeture par le
  correspondant. Ce code ne donne pas la cause de l'échec côté Windows.
- Les erreurs COM de connexion affichent maintenant l'étape et le HRESULT,
  même lorsque Windows fournit un message vide. L'échec d'une commande UI
  est journalisé avec sa pile d'appels. Si `0x80070016` persiste avec ce client,
  fermer les autres clients BLE, redémarrer l'ESP32 et, si nécessaire, supprimer
  son ancienne entrée dans les paramètres Bluetooth avant de relancer.
- L'absence de sondes n'empêche ni la connexion ni la lecture du statut.
  Attention : des GPIO ADC sans pont diviseur connecté flottent et peuvent
  produire des températures numériques plausibles mais fausses, pas
  nécessairement « Indisponible ». Les essais sans sondes ne valident pas
  les températures ; une résistance de 10 kOhm à la place d'une NTC dans
  un pont complet permet de tester une lecture proche de 25 °C.

Les erreurs de commandes UI et les exceptions UI non gérées sont journalisées localement dans
`%LOCALAPPDATA%\FanController\erreurs.log` ; aucun journal n'est envoyé.
