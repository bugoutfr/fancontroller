# Plan approuve - FanController Windows

Plan valide par l'utilisateur le 2 novembre 2026.

## Cible

- Application desktop locale WinUI 3, .NET 10, Windows 11 uniquement.
- Interface utilisateur en francais, dependances externes peu nombreuses,
  etablies et maintenues.
- Aucun service Azure, cloud, backend, base de donnees ou authentification.
- Repertoire : C:\Code\Arduino\FanController\windows.
- Ne pas modifier le firmware ESP32.

## Fonctionnalites

- Recherche BLE, selection, connexion et deconnexion explicites.
- Affichage des deux temperatures et des quatre RPM/PWM, modes et defauts.
- Deux profils de 2 a 8 points, temperatures strictement croissantes de -20 a
  120 degres C, PWM non decroissant de 0 a 100 % ; edition avec validation.
- Forcage individuel de chaque ventilateur et retour en mode automatique.
- Lecture de configuration, application en RAM ESP32, sauvegarde NVS explicite.
- Distinguer modifications locales non appliquees et dirty du firmware.
- Erreurs visibles ; actions concurrentes controlees et polling serialise.
- Apres rupture : reconnexion explicite puis relecture de configuration/statut,
  sans reapplication silencieuse des modifications.

## Architecture

Classes courtes et coherentes : modeles, transport BLE Windows natif,
protocole JSON, service du controleur et ViewModels ; code-behind UI limite.
Preferer System.Text.Json et API Windows natives aux couches BLE tierces.
Utiliser une version stable actuelle et verifiee de Windows App SDK.
Application non empaquetee si possible pour faciliter le lancement local.

## Contrat BLE

Source de verite : C:\Code\Arduino\FanController\esp32\README.md.
Service UART 6e400001-b5a3-f393-e0a9-e50e24dcca9e, RX 002 WriteWithResponse,
TX 003 Notify. Abonnement TX avant RX, JSON UTF-8 + LF, blocs TX/RX de 20
octets, une requete en vol, reassemblage octets jusqu'a LF avant decodage,
timeout de 15 secondes minimum. Timeout/desynchronisation => fermer connexion,
ne pas associer une reponse tardive a la requete suivante.
Commandes get_config, get_status, set_profile, set_fan, save et defaults.
Polling environ toutes les 2 secondes apres fin de la reponse precedente.
BLE firmware sans authentification : avertir dans la documentation.

## Verification

- Restaurer puis compiler avec les SDK disponibles.
- Tests du framing/fragmentation, validation, format JSON, serialisation des
  requetes, erreurs, timeouts et absence de concurrence si possible.
- Documenter commandes build/run/test et limites des essais sans carte BLE.
- Pas de mock data dans le fonctionnement normal.
