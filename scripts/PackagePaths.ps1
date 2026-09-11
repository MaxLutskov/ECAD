function New-EcadPackageRoot {
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)][string]$Version)
    if ($Version -notmatch '^\d+\.\d+(?:\.\d+)?$') { throw 'Некоректна версія пакета.' }
    $outputDirectory = Join-Path ([IO.Path]::GetFullPath($ProjectRoot)) 'output'
    if (Test-Path -LiteralPath $outputDirectory) {
        $item = Get-Item -LiteralPath $outputDirectory -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Папка output має бути звичайною папкою, не посиланням.'
        }
    }
    else { New-Item -ItemType Directory -Path $outputDirectory -ErrorAction Stop | Out-Null }
    $candidate = Join-Path $outputDirectory "v$Version"
    if (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $outputDirectory ("v{0}-build-{1}-{2}" -f $Version,
            (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8))
    }
    # Never reuse or delete an existing package, including a directory junction.
    return (New-Item -ItemType Directory -Path $candidate -ErrorAction Stop).FullName
}
