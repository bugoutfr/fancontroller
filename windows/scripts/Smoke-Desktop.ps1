param(
    [string]$Executable = "$PSScriptRoot\..\artifacts\desktop\FanController.App.exe",
    [switch]$Scan,
    [switch]$AutoConnect,
    [switch]$Startup,
    [switch]$RequireRssi,
    [switch]$EditCurves
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmokeWindow {
    private delegate bool EnumCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    public static IntPtr FindWindow(uint process) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, data) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == process && SendMessage(window, 0x7F, (IntPtr)1, IntPtr.Zero) != IntPtr.Zero) {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
$launchOptions = @{ FilePath = (Resolve-Path $Executable); PassThru = $true }
if ($Startup) { $launchOptions.ArgumentList = '--startup' }
$process = Start-Process @launchOptions
try {
    if ($Startup) {
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 250
            $hiddenHandle = [SmokeWindow]::FindWindow($process.Id)
        } while ($hiddenHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline)
        if ($hiddenHandle -eq [IntPtr]::Zero) { throw 'Fenetre de demarrage introuvable.' }
        Start-Sleep -Seconds 8
        if ([SmokeWindow]::IsWindowVisible($hiddenHandle)) { throw 'Fenetre visible au demarrage Windows.' }
        # Une seconde activation automatique ne doit pas afficher la fenetre.
        $automatic = Start-Process -FilePath (Resolve-Path $Executable) -ArgumentList '--startup' -PassThru
        if (-not $automatic.WaitForExit(15000)) { throw 'Activation automatique non redirigee.' }
        if ([SmokeWindow]::IsWindowVisible($hiddenHandle)) { throw 'Activation automatique ayant restaure la fenetre.' }
        $manual = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
        if (-not $manual.WaitForExit(15000)) { throw 'Activation manuelle non redirigee.' }
        Write-Output 'Demarrage masque et activation automatique silencieuse confirmes.'
    }
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Arret premature : $($process.ExitCode)" }
    } while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Fenetre WinUI introuvable.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $handle = $process.MainWindowHandle
    foreach ($kind in @(0, 1)) {
        if ([SmokeWindow]::SendMessage($handle, 0x7F, [IntPtr]$kind, [IntPtr]::Zero) -eq [IntPtr]::Zero) {
            throw "Icone de fenetre absente (WM_GETICON $kind)."
        }
    }
    Start-Sleep -Seconds 1
    function Find-Control([string]$Name) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Name)
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { throw "Controle introuvable : $Name" }
        return $element
    }
    function Select-Tab([string]$Name) {
        $tab = Find-Control $Name
        $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 500
    }
    Select-Tab 'SettingsTab'
    $startupToggle = (Find-Control 'WindowsStartupCheckBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $originalState = $startupToggle.Current.ToggleState
    try {
        $startupToggle.Toggle()
        Start-Sleep -Milliseconds 250
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
        try { $command = $key.GetValue('FanController') } finally { $key.Dispose() }
        if ($originalState -eq [System.Windows.Automation.ToggleState]::Off) {
            $expected = '"' + (Resolve-Path $Executable).Path + '" --startup'
            if ($command -ne $expected) { throw 'Commande de demarrage incorrecte.' }
        } elseif ($null -ne $command) { throw 'Commande de demarrage non supprimee.' }
    } finally {
        if ($startupToggle.Current.ToggleState -ne $originalState) {
            $startupToggle.Toggle()
            Start-Sleep -Milliseconds 250
        }
    }
    if ($startupToggle.Current.ToggleState -ne $originalState) { throw 'Reglage de demarrage non restaure.' }
    Write-Output 'Case de demarrage Windows et registre verifies ; reglage initial restaure.'
    $toggle = (Find-Control 'CurveEditingToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Edition active au lancement.' }
    $toggle.Toggle()
    Start-Sleep -Milliseconds 200
    [void](Find-Control 'CurveEditorTitle')
    $toggle.Toggle()
    Start-Sleep -Milliseconds 200
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Edition des courbes non desactivee.' }
    if ($AutoConnect) {
        if ($Scan) { throw 'AutoConnect et Scan ne doivent pas etre combines.' }
        $deadline = (Get-Date).AddSeconds(65)
        do {
            $status = Find-Control 'ConnectionStatus'
            if ($status.Current.Name -like 'Connect*') { break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        if ($status.Current.Name -notlike 'Connect*') { throw 'Connexion automatique non etablie.' }
        if (-not (Find-Control 'DisconnectButton').Current.IsEnabled) { throw 'Deconnexion indisponible apres connexion automatique.' }
        $scanCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ScanButton')
        $scanElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $scanCondition)
        if ($null -ne $scanElement -and -not $scanElement.Current.IsOffscreen) {
            throw 'Formulaire manuel encore visible apres connexion automatique.'
        }
        if ((Find-Control 'ControllerName').Current.Name -like '*lecture*') { throw 'Nom du controleur non lu.' }
        if ((Find-Control 'ControllerIdentifier').Current.Name -notmatch '([0-9A-F]{2}:){5}[0-9A-F]{2}') {
            throw 'Adresse BLE non affichee.'
        }
        $deadline = (Get-Date).AddSeconds(25)
        do {
            $quality = (Find-Control 'LinkQuality').Current.Name
            if ($quality -match '-?\d+ dBm' -or (-not $RequireRssi -and $quality -like '*1.1.3*')) { break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        if ($RequireRssi -and $quality -notmatch '-?\d+ dBm') { throw 'RSSI de la liaison active non recu.' }
        if ($quality -notmatch '-?\d+ dBm' -and $quality -notlike '*1.1.3*') { throw 'Etat du signal radio incorrect.' }
        Write-Output "Nom, adresse BLE et affichage du signal verifies : $quality"
        Write-Output 'Connexion automatique au dernier controleur, formulaire masque ; aucune action de configuration.'
    } else {
        $search = Find-Control 'ScanButton'
        if (-not $search.Current.IsEnabled) { throw 'Recherche initialement desactivee.' }
        foreach ($name in @('ConnectButton', 'DisconnectButton')) {
            if ((Find-Control $name).Current.IsEnabled) { throw "Action activee sans connexion : $name" }
        }
    }
    Select-Tab 'SettingsTab'
    if ($EditCurves) {
        if (-not $AutoConnect) { throw 'EditCurves exige AutoConnect ; aucune ecriture ESP32 ne sera effectuee.' }
        $toggle.Toggle()
        Start-Sleep -Milliseconds 500
        function Find-NamedControls([string]$name) {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $name)
            return $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        }
        $temperatureName = 'Temp' + [char]0xE9 + 'rature du point'
        $beforeAdd = (Find-NamedControls $temperatureName).Count
        $addOnChart = Find-NamedControls 'Ajouter un point sur la courbe'
        $deleteOnChart = Find-NamedControls ('Supprimer le point s' + [char]0xE9 + 'lectionn' + [char]0xE9)
        if ($deleteOnChart[0].Current.IsEnabled) { throw 'Suppression graphique active sans selection.' }
        $addOnChart[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
        if ((Find-NamedControls $temperatureName).Count -ne $beforeAdd + 1) { throw 'Ajout graphique non effectue.' }
        if (-not (Find-Control 'ApplyButton').Current.IsEnabled) { throw 'Ajout graphique ayant invalide le profil.' }
        if (-not $deleteOnChart[0].Current.IsEnabled) { throw 'Nouveau point non selectionne pour suppression.' }
        $deleteOnChart[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
        if ((Find-NamedControls $temperatureName).Count -ne $beforeAdd) { throw 'Suppression graphique non effectuee.' }
        if ((Find-Control 'ApplyButton').Current.IsEnabled) { throw 'Ajout/suppression ne restaure pas la configuration.' }
        Write-Output 'Boutons graphiques +/corbeille verifies ; ajout valide et suppression du point selectionne.'
        $temperatures = Find-NamedControls $temperatureName
        if ($temperatures.Count -lt 6) { throw 'Pas assez de points pour tester la suppression du milieu.' }
        $pointIndex = 1
        $temperaturePattern = $temperatures[$pointIndex].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $originalTemperatures = @($temperatures | ForEach-Object {
            $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        })
        $duties = Find-NamedControls 'PWM du point'
        $dutyPattern = $duties[$pointIndex].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $culture = [System.Globalization.CultureInfo]::GetCultureInfo('fr-FR')
        $temperature = [double]::Parse($temperaturePattern.Current.Value, $culture)
        $duty = [double]::Parse($dutyPattern.Current.Value, $culture)
        $chart = Find-Control 'EditableCurve'
        $scroll = (Find-Control 'SettingsScrollViewer').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        for ($percent = 0; $percent -le 100; $percent += 5) {
            $scroll.SetScrollPercent(-1, $percent)
            Start-Sleep -Milliseconds 120
            $rect = $chart.Current.BoundingRectangle
            $windowRect = $window.Current.BoundingRectangle
            if ($rect.Height -ge 249 -and $rect.Top -gt $windowRect.Top + 180 -and $rect.Bottom -lt $windowRect.Bottom - 20) { break }
        }
        if ($percent -gt 100) { throw 'Graphique non visible pour le glisser.' }
        Add-Type -AssemblyName System.Windows.Forms
        $cursor = [System.Windows.Forms.Cursor]::Position
        [void][SmokeWindow]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x13)
        try {
            [void][SmokeWindow]::SetForegroundWindow($handle)
            Start-Sleep -Milliseconds 500
            $rect = $chart.Current.BoundingRectangle
            $scale = [SmokeWindow]::GetDpiForWindow($handle) / 96.0
            $x = $rect.Left + 50 * $scale + ($temperature + 20) / 140 * ($rect.Width - 80 * $scale)
            $y = $rect.Top + 10 * $scale + (100 - $duty) / 100 * ($rect.Height - 80 * $scale)
            [void][SmokeWindow]::SetCursorPos([int]$x, [int]$y)
            [SmokeWindow]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 150
            for ($step = 1; $step -le 10; $step++) {
                [SmokeWindow]::mouse_event(1, 3, [uint32]::MaxValue, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 40
            }
            [SmokeWindow]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 300
        } finally {
            [SmokeWindow]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
            [void][SmokeWindow]::SetWindowPos($handle, [IntPtr](-2), 0, 0, 0, 0, 0x13)
            [void][SmokeWindow]::SetCursorPos($cursor.X, $cursor.Y)
        }
        $moved = [double]::Parse($temperaturePattern.Current.Value, $culture)
        if ($moved -le $temperature) {
            throw "Le glisser souris ne modifie pas la temperature : $temperature/$duty -> $moved/$($dutyPattern.Current.Value) ; chart=$rect ; scale=$scale ; cible=$x,$y."
        }
        $trash = Find-NamedControls 'Supprimer ce point'
        $before = $temperatures.Count
        $trash[$pointIndex].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
        $remainingTemperatures = Find-NamedControls $temperatureName
        if ($remainingTemperatures.Count -ne $before - 1) { throw 'La corbeille ne supprime pas le point choisi.' }
        if ($remainingTemperatures[$pointIndex].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $originalTemperatures[$pointIndex + 1]) {
            throw 'La corbeille a supprime une autre ligne que celle choisie.'
        }
        if (-not (Find-Control 'ApplyButton').Current.IsEnabled) { throw 'Brouillon de courbe invalide apres glisser/suppression.' }
        (Find-Control 'RevertButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
        if ((Find-NamedControls $temperatureName).Count -ne $before) { throw 'Annulation ne restaure pas les points.' }
        if ((Find-Control 'ApplyButton').Current.IsEnabled) { throw 'Brouillon encore modifie apres annulation.' }
        $toggle.Toggle()
        Write-Output 'Glisser souris et corbeille de point intermediaire verifies ; brouillon restaure, aucune ecriture BLE.'
    }
    foreach ($name in $(if ($AutoConnect) { @('ApplyButton') } else { @('ApplyButton', 'SaveButton') })) {
        if ((Find-Control $name).Current.IsEnabled) { throw "Action activee sans connexion : $name" }
    }
    Select-Tab 'HistoryTab'
    Select-Tab 'SettingsTab'
    if ($Scan) {
        $invoke = $search.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Start-Sleep -Seconds 17
        $process.Refresh()
        if ($process.HasExited) { throw 'Arret pendant le scan BLE.' }
        if (-not $search.Current.IsEnabled) { throw 'Recherche non reactivee apres le scan.' }
        # Aucune connexion ni ecriture GATT.
        Write-Output 'Scan BLE termine sans arret de l application (aucune connexion automatique).'
    }
    Select-Tab 'DashboardTab'
    [void]$process.CloseMainWindow()
    Start-Sleep -Milliseconds 700
    $process.Refresh()
    if ($process.HasExited -or [SmokeWindow]::IsWindowVisible($handle)) { throw 'Fermer ne masque pas dans le systray.' }
    # Un second lancement doit restaurer la premiere instance, sans doubler l acquisition.
    $second = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
    try {
        if (-not $second.WaitForExit(8000)) { throw 'Le second lancement ne redirige pas vers la premiere instance.' }
    } finally { if (-not $second.HasExited) { Stop-Process -Id $second.Id }; $second.Dispose() }
    Start-Sleep -Milliseconds 700
    if (-not [SmokeWindow]::IsWindowVisible($handle)) { throw 'Le second lancement ne restaure pas la fenetre.' }
    [void][SmokeWindow]::ShowWindow($handle, 6)
    Start-Sleep -Milliseconds 700
    if ([SmokeWindow]::IsWindowVisible($handle)) { throw 'Reduire ne masque pas dans le systray.' }
    [void][SmokeWindow]::ShowWindow($handle, 9)
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    (Find-Control 'QuitButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $process.WaitForExit(8000)) { throw 'Quitter ne termine pas le processus.' }
    Write-Output "OK : onglets, commandes protegees, fermer/reduire en systray, instance unique/restauration et sortie explicite."
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id }
    }
    $process.Dispose()
}
