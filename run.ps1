$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget/packages'
    if (-not (Test-Path -LiteralPath 'src/ECAD.Desktop/obj/project.assets.json')) {
        dotnet restore src/ECAD.Desktop/ECAD.Desktop.csproj --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Не вдалося відновити залежності.' }
    }
    dotnet run --project src/ECAD.Desktop/ECAD.Desktop.csproj --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося запустити ECAD.' }
}
finally { Pop-Location }
