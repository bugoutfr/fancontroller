param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent

if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) {
        $IsccPath = $compiler.Source
    }
    else {
        $candidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        )
        $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6 is required. Install it with: winget install --id JRSoftware.InnoSetup --exact --scope user'
}

$outputDir = Join-Path $windowsRoot 'artifacts\installer'
$publishDir = Join-Path $windowsRoot ("artifacts\setup-payload-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
New-Item -ItemType Directory -Path $publishDir | Out-Null
Push-Location $windowsRoot
try {
    dotnet publish .\src\FanController.App\FanController.App.csproj -c Release -r win-x64 `
        -p:Platform=x64 "-p:Version=$Version" -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the self-contained Windows application failed.' }

    foreach ($file in @('FanController.App.exe', 'FanController.App.pri', 'coreclr.dll', 'Assets\FanController.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $file) -PathType Leaf)) {
            throw "The published application is incomplete: $file is missing."
        }
    }

    & $IsccPath "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DInstallerOutputDir=$outputDir" `
        .\installer\FanController.iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

    $setupPath = Join-Path $outputDir "FanController-Setup-$Version-win-x64.exe"
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw 'Inno Setup did not produce the expected installer.'
    }
    Get-Item -LiteralPath $setupPath
    Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
