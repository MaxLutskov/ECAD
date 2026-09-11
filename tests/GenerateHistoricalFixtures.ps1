$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot
$fixtureDirectory = Join-Path $PSScriptRoot 'fixtures'
$generationRoot = Join-Path $repositoryRoot ('tmp/historical-fixtures-' + [Guid]::NewGuid().ToString('N'))
$revisions = @(@(6,'fb9d9f2'), @(7,'fdd9f18'), @(8,'77f7257'), @(9,'626806c'), @(10,'a4a4bc2'), @(11,'842d55b'))
foreach ($entry in $revisions) {
    $schema = $entry[0]; $revision = $entry[1]
    $work = Join-Path $generationRoot "schema-$schema"
    New-Item -ItemType Directory -Path (Join-Path $work 'Core') -Force | Out-Null
    $sourcePaths = git -C $repositoryRoot ls-tree -r --name-only $revision -- src/ECAD.Core
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read historical source tree.' }
    foreach ($sourcePath in $sourcePaths | Where-Object { $_ -like '*.cs' }) {
        $content = git -C $repositoryRoot show "${revision}:$sourcePath"
        if ($LASTEXITCODE -ne 0) { throw "Cannot read $sourcePath" }
        [IO.File]::WriteAllText((Join-Path $work ('Core/' + [IO.Path]::GetFileName($sourcePath))), ($content -join "`n"), [Text.UTF8Encoding]::new($false))
    }
    $defines = (7..11 | Where-Object { $_ -le $schema } | ForEach-Object { "GE$_" }) -join ';'
    [IO.File]::WriteAllText((Join-Path $work 'Generate.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><DefineConstants>$defines</DefineConstants></PropertyGroup></Project>")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/HistoricalGenerator.cs.txt') -Destination (Join-Path $work 'Program.cs')
    dotnet run --project (Join-Path $work 'Generate.csproj') -- (Join-Path $fixtureDirectory "schema-$schema.ecad")
    if ($LASTEXITCODE -ne 0) { throw "Historical schema $schema generation failed." }
    Write-Host "Generated schema $schema using $revision"
}
