# Default: regenerate from placement + validated routing.json, then validate.
param([switch]$Existing)
$ErrorActionPreference = 'Stop'
$bin = 'C:\Program Files\KiCad\10.0\bin'
Push-Location $PSScriptRoot
try {
    if (-not $Existing) {
        & "$bin\python.exe" generate.py
        if ($LASTEXITCODE) { throw 'CAD generation failed' }
    }
    & "$bin\kicad-cli.exe" sch erc FanController.kicad_sch --format json --output erc.json --exit-code-violations
    if ($LASTEXITCODE) { throw 'Schematic ERC failed' }
    # No DRC exclusions: warnings, connectivity and parity must all be clean.
    & "$bin\kicad-cli.exe" pcb drc FanController.kicad_pcb --format json --output drc.json --schematic-parity --refill-zones --save-board --exit-code-violations
    if ($LASTEXITCODE) { throw 'PCB DRC execution failed' }
    & "$bin\python.exe" validate.py
    if ($LASTEXITCODE) { throw 'Connectivity/physical consistency validation failed' }
    & "$bin\kicad-cli.exe" sch export pdf FanController.kicad_sch --output FanController-schematic.pdf
    if ($LASTEXITCODE) { throw 'Schematic PDF export failed' }
    & "$bin\kicad-cli.exe" sch export svg FanController.kicad_sch --output preview
    if ($LASTEXITCODE) { throw 'Schematic preview export failed' }
    & "$bin\kicad-cli.exe" pcb export svg FanController.kicad_pcb --output preview\PCB-front.svg --mode-single --layers F.Cu,F.SilkS,F.Fab,Edge.Cuts --fit-page-to-board --exclude-drawing-sheet
    if ($LASTEXITCODE) { throw 'PCB preview export failed' }
    & "$bin\kicad-cli.exe" pcb export svg FanController.kicad_pcb --output preview\PCB-back.svg --mode-single --layers B.Cu,B.SilkS,Edge.Cuts --fit-page-to-board --exclude-drawing-sheet
    if ($LASTEXITCODE) { throw 'PCB back preview export failed' }
} finally {
    Pop-Location
}
