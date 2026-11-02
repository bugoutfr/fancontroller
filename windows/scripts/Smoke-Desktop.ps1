param(
    [string]$Executable = "$PSScriptRoot\..\artifacts\desktop\FanController.App.exe",
    [switch]$Scan
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmokeWindow {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
}
'@
$process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Arret premature : $($process.ExitCode)" }
    } while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw 'Fenetre WinUI introuvable.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $handle = $process.MainWindowHandle
    Start-Sleep -Seconds 1
    function Find-Control([string]$Name) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Name)
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { throw "Controle introuvable : $Name" }
        return $element
    }
    $search = Find-Control 'ScanButton'
    if (-not $search.Current.IsEnabled) { throw 'Recherche initialement desactivee.' }
    foreach ($name in @('ConnectButton', 'DisconnectButton')) {
        if ((Find-Control $name).Current.IsEnabled) { throw "Action activee sans connexion : $name" }
    }
    function Select-Tab([string]$Name) {
        $tab = Find-Control $Name
        $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 500
    }
    Select-Tab 'SettingsTab'
    foreach ($name in @('ApplyButton', 'SaveButton')) {
        if ((Find-Control $name).Current.IsEnabled) { throw "Action activee sans connexion : $name" }
    }
    Select-Tab 'HistoryTab'
    Select-Tab 'DashboardTab'
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
