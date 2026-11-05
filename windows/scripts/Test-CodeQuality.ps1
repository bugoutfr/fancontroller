param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet build .\FanController.sln -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Build or public API documentation validation failed.' }

    dotnet test .\tests\FanController.Tests\FanController.Tests.csproj -c $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Host tests failed.' }

    dotnet format whitespace .\FanController.sln --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'Formatting differs from .editorconfig. Run dotnet format whitespace .\FanController.sln.' }
}
finally {
    Pop-Location
}
