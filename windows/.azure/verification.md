# Verification executee — client pour firmware 1.1.0

Répertoire : `C:\Code\Arduino\FanController\windows`.

| Commande | Résultat |
|---|---|
| `dotnet build FanController.sln -c Release -p:Platform=x64` | Succès, 0 erreur, 0 avertissement |
| `dotnet test tests\FanController.Tests\FanController.Tests.csproj -c Release` | 65 reussis, 0 echoue, 0 ignore |
| `dotnet publish src\FanController.App\FanController.App.csproj -c Release -r win-x64 -p:Platform=x64 -o artifacts\desktop` | Succès |
| `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\Smoke-Desktop.ps1 -Scan` | Fenetre reelle, onglets, commandes protegees hors connexion, scan, fermer/reduire en systray, relancement vers instance unique/restauration, Quitter termine le processus |

- Binaire : `artifacts\desktop\FanController.App.exe` ; distribuer le dossier complet.
- Framework : .NET 10, Windows App SDK stable 2.6.4, WinUI 3.
- Nombres fractionnaires ADC/RPM/PWM vérifiés contre les types réels du firmware,
  puis couverts par tests JSON et service.
- Les données simulées et le faux transport existent uniquement dans les tests.
- Aucune infrastructure web/cloud generee. Firmware versionne 1.1.0 et
  appairage physique/chiffre implementes conjointement dans le dossier voisin.
- Le smoke test initial déclenche le parcours de scan, pas une connexion.
- Diagnostic matériel ultérieur : `GetGattServicesForUuidAsync(Uncached)`
  reproduit une exception Windows `0x80070016` ; l'énumération complète
  `GetGattServicesAsync(Uncached)` réussit et retourne le service UART et RX/TX.
  Le client utilise désormais cette seconde méthode, comme BowAnalyzer.
- Avec le transport et le protocole de production corrigés, un outil console
  temporaire a validé sur l'ESP32 réel : abonnement TX, lecture de configuration,
  lecture/désérialisation/validation du statut (2 sondes, 4 ventilateurs), puis
  cinq lectures successives espacées d'au moins deux secondes. Aucune commande
  de modification/save envoyée. Nouvelle publication et 54 tests réussis.
- L'automatisation supplémentaire de la sélection du périphérique WinUI n'a
  pas réussi à activer le bouton Connecter ; elle ne constitue donc pas une
  validation du tableau de bord connecté. Connexion et polling réels validés
  au niveau transport/protocole, pas au niveau de ce parcours UI.
- Aucune mesure physique ni cycle NVS réel validé. Des ADC flottants sans
  sondes peuvent renvoyer des températures arbitraires avec `fault: none`.
- Recette BLE/matérielle complète dans `README.md`.

## Nouvelles validations et limites

- Historique : persistance/reouverture, separation par controleur, temperature
  en defaut, retention exacte a la lecture, purge au lancement sans acquisition,
  ecritures concurrentes, fichier corrompu signale, extrema et detection des
  interruptions avant agregation bornee ; tests sans fichiers utilisateur.
- Courbes de profil : points lineaires et invalidation lors des edits invalides.
- Cartes stables conservees lors des mesures ; mode simple avec securites
  visibles, mode avance avec details ; notifications sans changements inutiles.
- `firmware_version` accepte en JSON, champ optionnel pour lecture d'un ancien
  statut (le nouveau client exige toutefois une liaison BLE chiffree).
- Firmware : build PlatformIO reussi ; suites C++ domaine/integration/appairage
  reussies. Tests du bouton BOOT, appui au boot ignore, rebouclage temps,
  expiration, autorisation uniquement chiffree/bondee avec cle 16 octets,
  proprietaire persistant, refus inconnu, revocation physique, absence
  d'eviction des cles et acceptation du preavis FULL lors du premier appairage.
- Aucun televersement effectue. L'appairage reel Windows/ESP32, la reconnexion
  apres reboot et la revocation reelle exigent une recette avec le bouton.
  Les anciennes lectures BLE reussies documentees ci-dessus ne valident pas
  ces nouveaux chemins. Pas de validation des graphiques avec telemetrie reelle
  sur le nouveau firmware avant cette recette.
