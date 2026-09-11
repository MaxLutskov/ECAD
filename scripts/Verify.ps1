param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet restore ECAD.slnx --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build ECAD.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/ECAD.Checks -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'ECAD checks failed.' }
    & (Join-Path $projectRoot 'tests/PackagePaths.Checks.ps1')
    Write-Host 'ECAD verification completed.'
}
finally { Pop-Location }
