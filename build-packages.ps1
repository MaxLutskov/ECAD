param([string]$Version = '0.16')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "output/v$Version"))
$allowedOutputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'output')) + [IO.Path]::DirectorySeparatorChar
if (-not ($outputRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($allowedOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Некоректний шлях пакета.'
}

$packages = @(
    @{ Runtime = 'win-x64'; Launcher = 'Start ECAD.cmd' },
    @{ Runtime = 'linux-x64'; Launcher = 'start-ecad.sh' }
)

foreach ($package in $packages) {
    $packageRoot = [IO.Path]::GetFullPath((Join-Path $outputRoot $package.Runtime))
    if (-not ($packageRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Некоректний шлях платформи.'
    }
    if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
    $appDirectory = New-Item -ItemType Directory -Path (Join-Path $packageRoot 'App') -Force
    $libraryDirectory = New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Libraries') -Force
    $projectDirectory = New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Projects') -Force

    dotnet publish (Join-Path $projectRoot 'src/ECAD.Desktop/ECAD.Desktop.csproj') -c Release -r $package.Runtime --self-contained false -o $appDirectory.FullName
    if ($LASTEXITCODE -ne 0) { throw "Не вдалося зібрати пакет $($package.Runtime)." }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'examples/libraries/iec-automation-demo.ecadlib') -Destination $libraryDirectory.FullName
    Copy-Item -LiteralPath (Join-Path $projectRoot 'examples/libraries/drives-and-motors-demo.ecadlib') -Destination $libraryDirectory.FullName

    [IO.File]::WriteAllText((Join-Path $libraryDirectory.FullName 'README.txt'), @"
Тут зберігаються бібліотеки ECAD (*.ecadlib).
Два файли DEMO можна імпортувати та вільно редагувати. Їхні технічні дані потрібно звірити перед реальним використанням.
"@)
    [IO.File]::WriteAllText((Join-Path $projectDirectory.FullName 'README.txt'), @"
ECAD автоматично відкриває цю папку для збереження та завантаження креслень (*.ecad).
Власні підпапки проєктів можна створювати тут.
"@)
    [IO.File]::WriteAllText((Join-Path $packageRoot 'README.txt'), @"
ECAD $Version

$($package.Launcher) — запуск програми.
Projects — креслення ECAD (*.ecad).
Libraries — бібліотеки пристроїв (*.ecadlib).
App — технічні файли програми; для звичайної роботи відкривати цю папку не потрібно.

Пакет потребує встановлений .NET 10 Runtime.
"@)

    if ($package.Runtime -eq 'win-x64') {
        [IO.File]::WriteAllText((Join-Path $packageRoot $package.Launcher), "@echo off`r`nstart `"`" `"%~dp0App\ECAD.Desktop.exe`"`r`n")
    }
    else {
        [IO.File]::WriteAllText((Join-Path $packageRoot $package.Launcher), "#!/usr/bin/env sh`nSCRIPT_DIR=`$(CDPATH= cd -- `"`$(dirname -- `"`$0`")`" && pwd)`nexec dotnet `"`$SCRIPT_DIR/App/ECAD.Desktop.dll`"`n")
    }

    $requiredPaths = @('App', 'Libraries', 'Projects', 'README.txt', $package.Launcher)
    foreach ($relativePath in $requiredPaths) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $relativePath))) {
            throw "У пакеті $($package.Runtime) немає $relativePath."
        }
    }
    if (Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Extension -in @('.dll', '.json', '.pdb', '.so')) {
        throw "Технічний файл потрапив у корінь пакета $($package.Runtime)."
    }
    if ((Get-ChildItem -LiteralPath $libraryDirectory.FullName -Filter '*.ecadlib').Count -lt 2) {
        throw "У пакеті $($package.Runtime) немає демонстраційних бібліотек."
    }
}

Write-Host "Готові пакети: $outputRoot"
