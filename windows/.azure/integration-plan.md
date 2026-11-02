# FanController desktop — état de livraison

- Plan : `.azure/project-plan.md`, approuvé le 2 novembre 2026.
- Cible : WinUI 3 / .NET 10, Windows 11 x64, français, non empaqueté self-contained.
- Solution : `FanController.sln`.
- Application : `src\FanController.App\FanController.App.csproj`.
- Domaine/protocole : `src\FanController.Core\FanController.Core.csproj`.
- Tests : `tests\FanController.Tests\FanController.Tests.csproj`.
- Build : `dotnet build FanController.sln -c Release -p:Platform=x64`.
- Tests : `dotnet test tests\FanController.Tests\FanController.Tests.csproj -c Release`.
- Publication : `dotnet publish src\FanController.App\FanController.App.csproj -c Release -r win-x64 -p:Platform=x64 -o artifacts\desktop`.
- Exécution : `artifacts\desktop\FanController.App.exe`.
- UI réelle : `powershell.exe -NoProfile -File scripts\Smoke-Desktop.ps1`.
- Source protocole : `..\esp32\README.md` ; firmware inchangé.
- Dépendance essentielle : carte ESP32 BLE, radio/pilote Bluetooth Windows.
- Routes HTTP / ports / backend / base de données / migrations / seed : sans objet.
- Azure / services cloud / frontend web / mock applicatif : aucun.
- Aucun seam de fausses données à remplacer ; transport réel déjà branché.
- Commandes BLE : get_config, get_status, set_profile, set_fan, save, defaults.
- Vérification matérielle restante : recette README, carte et radio réelles.
