# Controleur de ventilateurs ESP32

Firmware Arduino / PlatformIO pour ESP32 classique (`esp32dev`).
Le client Windows .NET est dans [windows](../windows/README.md). Le controle local fonctionne
sans telephone, ordinateur ni connexion Bluetooth.

## Organisation du code

[main.cpp](src/main.cpp) ne fait que construire les objets, initialiser les
services et appeler leurs mises a jour. Il ne contient ni protocole, ni
regulation, ni acces NVS.

| Classe | Responsabilite | Implementation |
|---|---|---|
| `ThermalController` | Acquisition NTC, regulation, PWM, boost de demarrage, interruptions TACH et RPM | [ThermalController.cpp](src/ThermalController.cpp) |
| `ConfigurationStore` | Configuration courante, indicateur de modification, chargement/sauvegarde NVS et erreurs de stockage | [ConfigurationStore.cpp](src/ConfigurationStore.cpp) |
| `CommandProcessor` | Validation et execution des commandes, reponses JSON et assemblage du statut | [CommandProcessor.cpp](src/CommandProcessor.cpp) |
| `BleTransport` | GATT, connexion/abonnement, file de reception, fragmentation et delais du transport | [BleTransport.cpp](src/BleTransport.cpp) |
| `PairingSecurity` | Autorisation physique BOOT, proprietaire unique et cles BLE persistantes | [PairingSecurity.cpp](src/PairingSecurity.cpp) |

Les dependances sont explicites, passees par reference au constructeur :
`BleTransport` appelle `CommandProcessor`, qui utilise `ConfigurationStore`
et consulte `ThermalController`. La boucle principale fournit a la regulation
une vue constante de la configuration courante.

L'etat mutable appartient aux instances, sans variables globales partagees entre
modules. Les callbacks BLE ne touchent pas aux profils ni au materiel :
ils transmettent les paquets via une file FreeRTOS ; les commandes sont executees
dans la boucle principale. Les compteurs TACH sont proteges par section critique.
Les objets sont construits une seule fois et vivent pendant toute l'execution,
car les interruptions et callbacks conservent leurs adresses.

- [HardwareConfig.h](include/HardwareConfig.h) : broches, polarite PWM,
  impulsions par tour et periodes de regulation.
- [Controller.h](include/Controller.h) : structures du domaine et calculs purs
  (conversion NTC, interpolation, securite, RPM), sans Arduino ni BLE.
- [ConfigJson.h](include/ConfigJson.h) : conversion/validation des profils et
  configurations JSON, partagee entre protocole et stockage.

Les fonctions mathematiques et de serialisation restent de simples fonctions
sans etat : pas de classe artificielle uniquement pour les regrouper.
Le protocole BLE v1 et le format NVS restent inchanges.

## Cablage

Schema fonctionnel correspondant aux photos du montage :
[image PNG](docs/schema-electrique.png) et
[version vectorielle SVG](docs/schema-electrique.svg).
Les etiquettes identiques designent les memes connexions. Verifier le brochage
des ventilateurs et les tensions avant alimentation. Ne pas cumuler USB alimente
et 5 V MOLEX sur VIN sans verifier l'isolation des alimentations de la carte.

| Fonction | Ventilateur 1 | Ventilateur 2 | Ventilateur 3 | Ventilateur 4 |
|---|---|---|---|---|
| PWM | GPIO15 | GPIO16 | GPIO17 | GPIO18 |
| TACH | GPIO13 | GPIO34 | GPIO14 | GPIO27 |
| Sonde associee | 1 | 1 | 2 | 2 |

- Sonde 1 : GPIO33 ; sonde 2 : GPIO32 (ADC1).
- Chaque pont : **3,3 V -- resistance fixe 10 kOhm -- ADC -- NTC 10 kOhm B3950 -- GND**.
  La resistance nominale de la NTC est specifiee a 25 degres C.
- TACH : pull-up **externe 5,2 kOhm vers 3,3 V**, entree `INPUT`, interruption sur
  front descendant. Pas de pull-up interne ; GPIO34 n'en possede pas.
  Ne jamais amener 5 V sur une entree ESP32.
- Filtrage TACH : un condensateur **100 pF (marquage 101)** entre chaque
  GPIO TACH et GND, pres de l'entree. Avec 5,2 kOhm, constante de temps RC
  d'environ 0,52 microseconde (hors capacites du cable et du ventilateur).
  Les sorties TACH sont supposees a collecteur/drain ouvert.
- Filtrage NTC : un condensateur **100 nF (marquage 104)** en parallele sur
  chaque NTC, donc entre ADC et GND, idealement pres de l'entree ADC.
  A 25 degres C, la resistance equivalente du pont vaut environ 5 kOhm :
  constante de temps d'environ 0,5 ms, compatible avec les mesures toutes
  les 250 ms. Aucun changement du firmware n'est requis pour ces condensateurs.
- PWM : sortie 25 kHz / 10 bits, vers convertisseur de niveau 3,3 V -> 5 V,
  puis entree PWM du ventilateur 4 fils. Masse commune avec les alimentations.
  Ne pas alimenter les moteurs depuis les GPIO ni depuis le regulateur 3,3 V.
- Les convertisseurs sont supposes **non inverseurs**, compatibles avec 25 kHz
  et l'entree PWM des ventilateurs. Verifier leurs niveaux et fronts a l'oscilloscope.
  Pour un etage inverseur, modifier `PwmInverted` dans [HardwareConfig.h](include/HardwareConfig.h).
- GPIO15 est une broche de strapping : le convertisseur ne doit pas imposer un
  niveau incompatible au reset. GPIO16/17 doivent etre libres (certaines cartes
  avec PSRAM les utilisent). Verifier le modele reel de carte.
- Les RPM supposent **2 impulsions par tour**. Adapter `PulsesPerRevolution`
  dans [HardwareConfig.h](include/HardwareConfig.h) aux fiches techniques des ventilateurs.

## Regulation

- Deux profils independants ; un profil contient 2 a 8 points
  `{temperature, duty}`. Temperatures strictement croissantes de -20 a 120 degres C ;
  rapports cycliques non decroissants de 0 a 100 %.
- Interpolation lineaire entre points, valeur du premier/dernier point en dehors
  de la courbe. Profil initial : `(25,30), (35,40), (45,60), (55,80), (65,100)`.
- Conversion par equation beta avec NTC 10 kOhm, B3950 et resistance fixe 10 kOhm.
  Acquisition toutes les 250 ms, moyenne de 16 mesures ADC calibrees en millivolts.
  La formule suppose une alimentation de pont a 3 300 mV ; adapter `SupplyMv`
  dans [Controller.h](include/Controller.h) a une mesure reelle si necessaire.
  Le beta et la tolerance des resistances limitent aussi la precision.
- Calibration fixe globale : `TemperatureOffsetC` dans
  [HardwareConfig.h](include/HardwareConfig.h), ajoutee apres conversion NTC.
  Correction commune aux deux sondes : **-0,7 degre C**
  (23,6 mesures sans correction pour 22,9 reels).
  Temperature corrigee utilisee pour le BLE, les profils et le seuil de
  securite de 80 degres C ; les millivolts restent bruts et les defauts
  electriques ne sont pas corriges. Valeurs compilees dans le firmware,
  ni reglables par BLE ni stockees en NVS : recompilation/televersement requis.
  Cet etalonnage a un point corrige un decalage autour de 22,9 degres C,
  pas la precision sur toute la plage ; mesurer apres stabilisation thermique.
- Chaque ventilateur a un mode `auto` ou `fixed`. Le forcage est un **rapport
  cyclique PWM**, pas une consigne RPM asservie. Le TACH sert a mesurer les RPM.
- Demarrage a 100 % pendant environ une seconde ; meme impulsion au passage
  de 0 % a une consigne positive. 0 % ne garantit pas l'arret de tous les modeles
  de ventilateurs : verifier leur comportement et leur rapport minimal de demarrage.
- **Priorite de securite** : sonde en defaut OU temperature >= **80 degres C** =>
  les deux ventilateurs associes passent a 100 %, meme en mode `fixed`.
  Le mode demande est conserve et redevient actif apres disparition du defaut.
  Le seuil est defini par `SafetyTemperature` dans [Controller.h](include/Controller.h).
  Sans hysteresis : eviter d'exploiter le systeme pres du seuil.
- Detection electrique : <= 50 mV = `short_circuit`, >= 3 050 mV =
  `open_circuit` (ou saturation ADC), temperature calculee hors -20..120 =
  `out_of_range`. Les defauts ont `temperature: null`. Un froid extreme peut
  saturer l'ADC et etre traite comme un defaut ; ces libelles ne remplacent pas
  un diagnostic physique.
- En cas d'echec PWM detecte, la broche est maintenue au niveau logique de 100 %
  et `pwm_ok` devient `false`. Aucune garantie de refroidissement pendant reset,
  coupure d'alimentation, panne materielle ou plantage du microcontroleur :
  utiliser une protection thermique independante si necessaire.
- Pas de regulation RPM ni de relance automatique en cas de TACH a zero.
  La future application peut signaler un ventilateur bloque via les RPM.

## Memoire persistante

Les commandes modifient la configuration en RAM. `save` enregistre **les deux
profils et les quatre modes/rapports fixes** dans une seule cle NVS, avec
verification de relecture. Le boot restaure cette configuration apres validation.
Ainsi, un forcage sauvegarde revient aussi au boot.

`dirty: true` indique une modification depuis le dernier chargement/enregistrement.
`defaults` restaure uniquement la RAM ; appeler `save` pour rendre cette
restauration persistante. Pas d'ecriture flash a chaque mesure ou commande.

Premier boot : profils par defaut. Configuration NVS invalide : message serie,
`storage: "invalid"`, utilisation des valeurs par defaut sans ecraser la cle.
NVS indisponible : `storage: "unavailable"` ; `save` echoue explicitement.
Etats possibles : `defaults`, `loaded`, `saved`, `invalid`, `unavailable`,
`write_error`, `verify_error`. Un echec de sauvegarde ne signifie pas que les
changements en RAM ont ete annules.

## Protocole BLE v1 pour Windows .NET

Nom annonce : `ESP32-FanController`. Un seul client a la fois.

| Element GATT | UUID | Propriete |
|---|---|---|
| Service | `6e400001-b5a3-f393-e0a9-e50e24dcca9e` | Service UART |
| RX (Windows -> ESP32) | `6e400002-b5a3-f393-e0a9-e50e24dcca9e` | Write, avec reponse GATT |
| TX (ESP32 -> Windows) | `6e400003-b5a3-f393-e0a9-e50e24dcca9e` | Notify |

### Appairage physique et chiffrement (firmware 1.1.0)

- Premier PC : apres demarrage normal, maintenir **BOOT (GPIO0) 2 secondes
  puis relacher**. Une fenetre de **60 secondes** autorise le premier appairage.
  Dans le client Windows, rechercher puis connecter/appairer le controleur.
- Un seul proprietaire est memorise en NVS, independamment des profils.
  Les connexions suivantes reutilisent les cles, sans appui sur BOOT.
  Un appareil inconnu est refuse hors fenetre ; commandes et reponses exigent
  une session chiffree et liee au proprietaire, avec cle de 128 bits.
- **BOOT 10 secondes puis relacher** revoque les cles et le proprietaire.
  Supprimer aussi le controleur des appareils Bluetooth Windows, puis ouvrir
  une nouvelle fenetre de 2 secondes. `defaults` et `save` ne changent pas les cles.
- Ne pas maintenir BOOT pendant un reset : cela peut lancer le chargeur ROM.
  Un BOOT deja enfonce au demarrage n'ouvre pas la fenetre d'appairage.
- BLE **LE Secure Connections Just Works** : chiffrement, mais **pas de
  protection MITM lors du premier appairage**. Effectuer celui-ci dans un lieu
  de confiance, pres de la carte. Le bouton autorise le premier demandeur,
  pas une identite Windows affichee sur la carte. Ce n'est pas un code secret.
- Aucun renouvellement automatique des cles existantes, ni eviction automatique
  d'un proprietaire quand le stockage est plein. En cas de perte des cles Windows,
  utiliser la revocation physique plutot que reappairer silencieusement.
  Les erreurs de NVS/appairage sont journalisees et refusent l'acces BLE ;
  la regulation thermique locale reste autonome.

La recette radio/appairage reel doit etre effectuee apres televersement ;
les tests sur ordinateur ne remplacent pas cette verification.

Correctifs 1.1.1 : l'authentification et l'expiration de session sont
serialisees ; le delai utilise un horodatage pris apres la connexion, pas
celui d'un ancien tour de boucle. Une fenetre fermee par un appairage reussi
ne provoque pas de deconnexion. La revocation suspend les annonces BLE avant
de supprimer les cles (NimBLE peut refuser la suppression d'une IRK pendant
les annonces), verifie leur disparition puis reprend les annonces.
En cas d'erreur, le diagnostic precise l'etape et l'acces reste refuse.
Apres une revocation echouee avec 1.1.0 : televerser 1.1.1, redemarrer normalement,
puis refaire BOOT 10 secondes et supprimer l'appairage Windows avant de reappairer.

Correctif 1.1.2 : l'option NimBLE `sm_sc_only` impose aussi une authentification
MITM (niveau 4) aux attributs GATT proteges, incompatible avec Just Works :
une session chiffree pouvait donc recevoir ATT `0x05` sur RX.
Cette option est desactivee ; les appairages legacy sont desormais exclus a
la compilation par `MYNEWT_VAL_BLE_SM_LEGACY=0`, avec assertions de compilation
verifiant legacy desactive et Secure Connections active. Le chiffrement RX,
le proprietaire unique et la cle de 16 octets restent obligatoires.
Les cles Secure Connections deja creees peuvent etre conservees.

Ajout 1.1.3 : `get_status.data.ble_rssi_dbm` fournit le RSSI en dBm de la
liaison active, mesure par le recepteur ESP32 (sens Windows vers ESP32).
La lecture utilise `ble_gap_conn_rssi` au traitement de la commande, sans
relancer les annonces ni modifier l'appairage. Plage valide : -127 a +20 dBm.
En cas d'echec ou de valeur HCI 127 (indisponible), le champ vaut `null`
et un diagnostic serie est emis ; aucune ancienne mesure n'est reutilisee.
Les cles et les profils existants sont conserves apres televersement.

### Transport

1. Scanner le service avec `BluetoothLEAdvertisementWatcher` ou enumerer les
   appareils BLE, puis utiliser `BluetoothLEDevice` et les API GATT
   `Windows.Devices.Bluetooth` (cible .NET Windows).
2. Appairer/chiffrer la liaison, puis s'abonner a TX (`ValueChanged` puis CCCD `Notify`) **avant** d'ecrire RX.
   Une ecriture avant abonnement provoque une deconnexion explicite.
3. Envoyer un objet JSON UTF-8 termine par **LF (`\n`)**. CRLF est accepte.
   Limite : 1 024 octets avant LF. Fragmenter en blocs de **20 octets** pour
   fonctionner meme avec le MTU BLE minimal, via `WriteWithResponse`.
   Chaque ecriture GATT accepte au maximum 512 octets si le MTU le permet.
4. Attendre la reponse JSON complete avant la commande suivante. Un succes GATT
   signifie seulement que le bloc est transmis, pas que la commande est valide.
5. Les reponses TX sont fragmentees en blocs d'au plus 20 octets, espaces
   d'au moins 20 ms. **Concatener les octets jusqu'a LF, puis decoder UTF-8/JSON**.
   Une notification n'est pas un message complet. Prevoir un delai de reponse
   de 15 secondes (statut/configuration peuvent depasser 1 ko).
6. La reponse contient `ok` et, en cas de succes, `cmd`. Les lectures contiennent
   `data`. Une erreur contient `ok:false`, `error` et `message`, sans `cmd`.
   Il n'y a pas d'identifiant de requete : une seule commande en vol.
7. Pas de telemetrie spontanee : interroger `get_status` apres chaque reponse
   complete, par exemple toutes les 2 secondes.
8. Ligne partielle sans nouvel octet pendant 5 secondes : `frame_timeout`.
   Ligne trop longue ou contenant NUL : `invalid_frame`, puis rejet jusqu'au LF
   suivant. Terminer cette ligne avant d'envoyer une nouvelle commande.
   Une file RX saturee ou une erreur d'emission provoque une deconnexion et un
   diagnostic serie. A la reconnexion, reabonner TX, relire la configuration et
   le statut : une commande executee juste avant une rupture peut avoir pris effet
   sans que Windows ait recu sa reponse. La regulation continue hors connexion.

### Commandes

Chaque exemple ci-dessous doit etre suivi d'un LF.

```json
{"cmd":"get_config"}
{"cmd":"get_status"}
{"cmd":"set_profile","sensor":1,"points":[{"temperature":25,"duty":30},{"temperature":40,"duty":50},{"temperature":60,"duty":100}]}
{"cmd":"set_profile","sensor":2,"points":[{"temperature":20,"duty":20},{"temperature":65,"duty":100}]}
{"cmd":"set_fan","fan":3,"mode":"fixed","duty":55}
{"cmd":"set_fan","fan":3,"mode":"auto"}
{"cmd":"save"}
{"cmd":"defaults"}
```

Les indices de sondes et ventilateurs commencent a **1**.
Une commande invalide ne modifie pas la configuration.
Les modifications sont appliquees lors du prochain cycle de controle (<= 250 ms
hors ecriture flash). `save` est la seule commande d'enregistrement.

Exemples de reponses :

```json
{"ok":true,"cmd":"set_fan","dirty":true}
{"ok":true,"cmd":"save","dirty":false}
{"ok":false,"error":"invalid_mode","message":"Expected mode: auto, or mode: fixed with duty: 0..100"}
```

`get_config.data` :

```json
{
  "version":1,
  "profiles":[
    [{"temperature":25,"duty":30},{"temperature":35,"duty":40},{"temperature":45,"duty":60},{"temperature":55,"duty":80},{"temperature":65,"duty":100}],
    [{"temperature":25,"duty":30},{"temperature":35,"duty":40},{"temperature":45,"duty":60},{"temperature":55,"duty":80},{"temperature":65,"duty":100}]
  ],
  "fans":[
    {"mode":"auto","duty":0},{"mode":"auto","duty":0},
    {"mode":"auto","duty":0},{"mode":"auto","duty":0}
  ]
}
```

La reponse `get_config` contient aussi `dirty` et `storage` a la racine.
`get_status.data` contient :

- `uptime_ms` (compteur 32 bits, reboucle apres environ 49,7 jours),
  `dirty`, `storage`, `storage_available`.
- `firmware_version` : version semantique, actuellement `"1.1.3"`, definie dans
  [FirmwareVersion.h](include/FirmwareVersion.h). Distincte de la version 1 du
  format de configuration NVS ; les profils sauvegardes restent compatibles.
- `ble_rssi_dbm` : puissance du signal recu par l'ESP32 sur la liaison active,
  entier de -127 a +20 dBm, ou `null` si indisponible (depuis 1.1.3).
- `sensors` : deux objets `sensor`, `millivolts`, `temperature` (degres C ou null),
  `fault` (`none`, `short_circuit`, `open_circuit`, `out_of_range`), `safety`.
- `fans` : quatre objets `fan`, `sensor`, `mode`, `fixed_duty`, `target_duty`
  (apres securite), `applied_duty` (commande PWM, boost inclus), `rpm`,
  `pwm_ok`, `boost`. Les RPM sont actualises chaque seconde.

## Dependances et maintenance

Seulement deux bibliotheques externes, issues de projets etablis et maintenus :

- [NimBLE-Arduino 2.5.2](https://github.com/h2zero/NimBLE-Arduino/releases/tag/2.5.2),
  version stable publiee le 25 octobre 2026 : couche BLE Arduino reposant sur
  Apache NimBLE.
- [ArduinoJson 7.4.3](https://github.com/bblanchon/ArduinoJson/releases/tag/v7.4.3),
  version stable publiee le 2 mars 2026 : serialisation et validation JSON.

Versions stables verifiees le 2 novembre 2026, epinglees pour rendre les builds
reproductibles. Pour les mettre a jour, modifier les versions dans
[platformio.ini](platformio.ini), recompiler, relancer les tests C++ puis la
recette BLE/materielle. Eviter les versions de developpement et les mises a jour
automatiques non validees.

PWM, ADC, interruptions TACH et stockage `Preferences` utilisent directement
Arduino-ESP32 / ESP-IDF, sans autre bibliotheque tierce.

[secure_nimble.py](scripts/secure_nimble.py), execute avant compilation, applique
un correctif local reproductible a NimBLE-Arduino 2.5.2 : refuser
`BLE_GAP_EVENT_REPEAT_PAIRING` au lieu de supprimer l'ancien bond puis reessayer.
Le script est idempotent et bloque la compilation si la structure attendue
change. Le callback de stockage refuse les debordements sans evincer de cles ;
une notification preventive `FULL` n'empeche pas le premier appairage.
Reexaminer ces deux protections avant toute mise a jour de NimBLE.

## Compilation et essais

Depuis le terminal PlatformIO de VS Code :

```powershell
pio run -e esp32dev
pio run -e esp32dev -t upload
pio device monitor -b 115200
```

Le code adapte l'API LEDC aux versions Arduino-ESP32 2 et 3. L'environnement
installe utilise Espressif32 7.1.3 / Arduino-ESP32 3.3.12 ; cette combinaison est
celle validee par compilation. Les bibliotheques BLE et JSON sont epinglees
dans [platformio.ini](platformio.ini).

Tests C++ sur ordinateur, apres la premiere compilation (dependance ArduinoJson
presente dans `.pio/libdeps/esp32dev`) :

```powershell
# Dans un Developer PowerShell / Native Tools terminal de Visual Studio :
New-Item -ItemType Directory -Force .pio\host-tests | Out-Null
cl /nologo /EHsc /std:c++17 /W4 /Iinclude /I".pio\libdeps\esp32dev\ArduinoJson\src" test\test_controller.cpp /Fe:".pio\host-tests\controller-tests.exe" /Fo:".pio\host-tests\controller-tests.obj"
if ($LASTEXITCODE -eq 0) { & .pio\host-tests\controller-tests.exe }

cl /nologo /EHsc /std:c++17 /W4 /Itest\host /Iinclude /I".pio\libdeps\esp32dev\ArduinoJson\src" /DARDUINOJSON_ENABLE_ARDUINO_STRING=1 test\test_firmware.cpp src\CommandProcessor.cpp src\ConfigurationStore.cpp src\ThermalController.cpp /Fe:".pio\host-tests\firmware-tests.exe" /Fo.pio\host-tests\
if ($LASTEXITCODE -eq 0) { & .pio\host-tests\firmware-tests.exe }

cl /nologo /EHsc /std:c++17 /W4 /Itest\host /Iinclude test\test_pairing.cpp src\PairingSecurity.cpp /Fe:".pio\host-tests\pairing-tests.exe" /Fo.pio\host-tests\
if ($LASTEXITCODE -eq 0) { & .pio\host-tests\pairing-tests.exe }
```

Ces tests couvrent conversion NTC et defauts, interpolation et validation,
association des ventilateurs, forcage et priorite securite, calcul RPM,
serialisation/deserialisation et rejet de configurations invalides.

[test_firmware.cpp](test/test_firmware.cpp) teste aussi les vraies classes
`CommandProcessor`, `ConfigurationStore` et `ThermalController`, avec des
substituts Arduino/Preferences dans [test/host](test/host). Il couvre les formes
des reponses JSON v1, les indices et commandes invalides sans modification,
les deux groupes de ventilateurs, le boost, la securite, les callbacks TACH,
la restauration apres reboot simule, les erreurs NVS/PWM et le debordement
du compteur de temps. Ces substituts sont uniquement inclus par la commande
de test sur ordinateur, jamais par le build ESP32.

Ces tests ne remplacent pas les essais ADC/PWM/TACH/BLE/NVS sur la carte ;
la pile radio et le transport BLE reel ne sont pas simules.

### Recette materielle avant utilisation

1. Verifier les tensions TACH (jamais > 3,3 V), la masse commune et les signaux
   PWM a 25 kHz apres conversion. Confirmer le nombre d'impulsions TACH par tour.
2. Une resistance de 10 kOhm a la place de chaque NTC doit donner environ
   1 650 mV / 25 degres C. Comparer ensuite les NTC a un thermometre.
3. Faire varier chaque sonde : seuls ses deux ventilateurs suivent son profil.
4. Tester le mode fixe sur les quatre sorties, le retour en automatique,
   0 %, 100 % et le redemarrage depuis 0 %.
5. Debrancher puis court-circuiter chaque sonde : seuls ses deux ventilateurs
   passent a 100 %, meme forces a 0 %. Simuler >= 80 degres C pour le seuil.
6. Via un client BLE GATT (par exemple un outil de diagnostic BLE), modifier les
   deux profils et des modes fixes, appeler `save`, couper/rebrancher et verifier
   `get_config`, puis `get_status`. Verifier aussi qu'un changement sans `save`
   ne revient pas au boot.
7. Tester fragmentation de 20 octets, commande invalide, deconnexion/reconnexion,
   puis fonctionnement autonome sans Windows.
