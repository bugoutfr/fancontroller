# FanController

Controleur autonome de quatre ventilateurs PWM sur ESP32, accompagne d'un
client Windows local pour la configuration et le suivi par Bluetooth BLE.
Aucun service cloud ni backend n'est necessaire.

## Fonctionnalites

- Deux sondes NTC 10 kOhm / beta 3950, chacune associee a deux ventilateurs.
- Un profil par sonde, avec interpolation lineaire entre les points.
- Forcage individuel du PWM et mesure des vitesses par les entrees TACH.
- Securite thermique prioritaire sur les consignes manuelles.
- Application des reglages en RAM et sauvegarde explicite en NVS pour les
  retrouver au prochain demarrage.
- Appairage BLE persistant autorise physiquement par le bouton BOOT.
- Client Windows en francais : graphiques des profils, historique local
  temperatures/RPM sur 30 jours et fonctionnement en zone de notification.
- Connexion automatique au dernier controleur memorise encore appaire dans
  Windows et reconnexion apres coupure, sans rejouer les ecritures interrompues.

La regulation continue sur l'ESP32 sans ordinateur ni connexion Bluetooth.
Le pourcentage commande le PWM : ce n'est pas un asservissement de la vitesse
en tours par minute.

## Organisation et documentation

| Repertoire | Contenu | Documentation principale |
|---|---|---|
| [esp32](esp32/) | Firmware Arduino / PlatformIO, configuration materielle et tests C++ | [Guide du firmware](esp32/README.md) |
| [windows](windows/) | Client WinUI 3 / .NET 10 pour Windows 11 x64 et tests .NET | [Guide du client Windows](windows/README.md) |

### Firmware et montage

Le [guide ESP32](esp32/README.md) detaille le cablage GPIO, les ponts NTC,
le filtrage, la calibration, les protections thermiques, le protocole JSON BLE,
l'appairage, la persistance NVS et les commandes de compilation/test.

- Schema electrique : [image PNG](esp32/docs/schema-electrique.png) et
  [version vectorielle SVG](esp32/docs/schema-electrique.svg).
- Parametres materiels : [HardwareConfig.h](esp32/include/HardwareConfig.h).
- Environnement et dependances : [platformio.ini](esp32/platformio.ini).

### Client Windows

Le [guide Windows](windows/README.md) couvre les prerequis, la compilation,
la publication, l'utilisation, l'historique, le systray et le depannage BLE.

Documents complementaires :

- [Plan initial du client](windows/.azure/project-plan.md).
- [Etat de livraison initial](windows/.azure/integration-plan.md).
- [Compte rendu de verification](windows/.azure/verification.md).

Les plans et comptes rendus decrivent leurs etapes de livraison respectives ;
certaines indications precedant les evolutions d'appairage et d'interface
sont historiques. Les guides principaux de chaque sous-projet decrivent
le fonctionnement actuel. Le nom du dossier `.azure` ne signifie pas que
le projet utilise Azure.

## Demarrage rapide

### ESP32

Depuis le repertoire [esp32](esp32/), avec PlatformIO :

```powershell
pio run -e esp32dev
pio run -e esp32dev -t upload
pio device monitor -b 115200
```

Verifier le [cablage et les precautions d'alimentation](esp32/README.md#cablage)
avant le televersement. Ne pas cumuler USB alimente et 5 V sur VIN sans
confirmation de l'isolation des alimentations de la carte.

### Windows

Depuis le repertoire [windows](windows/), avec les prerequis du
[guide de compilation](windows/README.md#construire-tester-lancer) :

```powershell
dotnet restore .\FanController.sln
dotnet test .\tests\FanController.Tests\FanController.Tests.csproj -c Release
dotnet publish .\src\FanController.App\FanController.App.csproj -c Release -r win-x64 -p:Platform=x64 -o .\artifacts\desktop
.\artifacts\desktop\FanController.App.exe
```

Distribuer tout le dossier de publication, pas uniquement l'executable.
Les sorties de compilation, dependances telechargees et publications sont
exclues de Git par les fichiers `.gitignore`.

### Premier appairage

1. Apres le demarrage normal de l'ESP32, maintenir **BOOT 2 secondes puis
   relacher** pour ouvrir une fenetre d'appairage de 60 secondes.
2. Rechercher le controleur dans le client Windows, puis connecter/appairer.
3. Les connexions suivantes reutilisent les cles memorisees.

**BOOT 10 secondes puis relacher** revoque le proprietaire ; supprimer aussi
l'appairage Windows avant de recommencer. Ne pas maintenir BOOT pendant un
reset. Le mode **Just Works** chiffre la liaison mais ne protege pas contre
une interception active au premier appairage : effectuer celui-ci dans un
environnement de confiance.

## Tests

- Tests C++ du domaine, de l'integration firmware et de l'appairage :
  [instructions ESP32](esp32/README.md#compilation-et-essais).
- Tests .NET et verification de la fenetre reelle :
  [instructions Windows](windows/README.md#vérification-et-limites) et
  [script de smoke test](windows/scripts/Smoke-Desktop.ps1).

Les tests sur ordinateur ne remplacent pas la verification du montage,
des capteurs, des sorties PWM/TACH et du Bluetooth sur le materiel reel.
