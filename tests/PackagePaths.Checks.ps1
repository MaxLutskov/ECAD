$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
. (Join-Path $projectRoot 'scripts/PackagePaths.ps1')
$fixtureRoot = Join-Path $projectRoot ('tmp/package-checks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$first = New-EcadPackageRoot -ProjectRoot $fixtureRoot -Version '0.17'
foreach ($relative in @('win-x64/Projects/custom.ecad', 'win-x64/Libraries/custom.ecadlib', 'linux-x64/Projects/custom.ecad')) {
    $file = Join-Path $first $relative
    New-Item -ItemType Directory -Path (Split-Path $file) -Force | Out-Null
    [IO.File]::WriteAllText($file, 'user data')
}
$second = New-EcadPackageRoot -ProjectRoot $fixtureRoot -Version '0.17'
if ($first -eq $second) { throw 'Repeated packaging reused a directory.' }
foreach ($file in Get-ChildItem -LiteralPath $first -Recurse -File) {
    if ([IO.File]::ReadAllText($file.FullName) -ne 'user data') { throw 'User data changed.' }
}
if ((Get-ChildItem -LiteralPath $first -Recurse -File).Count -ne 3) { throw 'User files disappeared.' }
foreach ($invalid in @('../escape', '0.17/../escape')) {
    $rejected = $false
    try { New-EcadPackageRoot -ProjectRoot $fixtureRoot -Version $invalid | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid path accepted.' }
}
Write-Host 'PASS Repeated packaging preserves Projects/Libraries and rejects invalid versions.'
