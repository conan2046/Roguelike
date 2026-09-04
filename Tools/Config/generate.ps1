[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$LubanDll = Join-Path $RepoRoot 'Tools\Luban\Luban.dll'
$ConfigFile = Join-Path $RepoRoot 'Config\luban.conf'
$CodeDir = Join-Path $RepoRoot 'Assets\Scripts\Generated\Config'
$DataDir = Join-Path $RepoRoot 'Assets\StreamingAssets\Config\Luban'
$GenerationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('roguelike-luban-generate-' + [Guid]::NewGuid().ToString('N'))
$GeneratedCodeDir = Join-Path $GenerationRoot 'code'
$GeneratedDataDir = Join-Path $GenerationRoot 'data'

if (-not (Test-Path -LiteralPath $LubanDll)) {
    throw "Luban tool is missing: $LubanDll"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found. Install .NET 8 Runtime.'
}

$RuntimeList = (& dotnet --list-runtimes) -join "`n"
if ($RuntimeList -notmatch 'Microsoft\.NETCore\.App 8\.') {
    throw '.NET 8 Runtime is required by Luban v4.11.0.'
}

$ExpectedCodeDir = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'Assets\Scripts\Generated\Config'))
$ExpectedDataDir = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'Assets\StreamingAssets\Config\Luban'))
if ([System.IO.Path]::GetFullPath($CodeDir) -ne $ExpectedCodeDir -or
    [System.IO.Path]::GetFullPath($DataDir) -ne $ExpectedDataDir) {
    throw 'Refusing to clean unexpected generation directories.'
}

function Sync-GeneratedDirectory([string]$Source, [string]$Target) {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null

    $sourceFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($Source, $file.FullName)
        $sourceFiles[$relative] = $file.FullName
    }

    foreach ($file in Get-ChildItem -LiteralPath $Target -Recurse -File | Where-Object Extension -ne '.meta') {
        $relative = [System.IO.Path]::GetRelativePath($Target, $file.FullName)
        if (-not $sourceFiles.ContainsKey($relative)) {
            Remove-Item -LiteralPath $file.FullName -Force
            $metaPath = $file.FullName + '.meta'
            if (Test-Path -LiteralPath $metaPath) {
                Remove-Item -LiteralPath $metaPath -Force
            }
        }
    }

    foreach ($entry in $sourceFiles.GetEnumerator()) {
        $destination = Join-Path $Target $entry.Key
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        $needsCopy = -not (Test-Path -LiteralPath $destination)
        if (-not $needsCopy) {
            $needsCopy = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        }
        if ($needsCopy) {
            Copy-Item -LiteralPath $entry.Value -Destination $destination -Force
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $GeneratedCodeDir, $GeneratedDataDir | Out-Null

    & dotnet $LubanDll `
        -t client `
        -c cs-bin `
        -d bin `
        --conf $ConfigFile `
        -x "outputCodeDir=$GeneratedCodeDir" `
        -x "outputDataDir=$GeneratedDataDir"

    if ($LASTEXITCODE -ne 0) {
        throw "Luban generation failed with exit code $LASTEXITCODE."
    }

    Sync-GeneratedDirectory $GeneratedCodeDir $CodeDir
    Sync-GeneratedDirectory $GeneratedDataDir $DataDir
    Write-Host 'Luban generation completed.'
}
finally {
    if (Test-Path -LiteralPath $GenerationRoot) {
        Remove-Item -LiteralPath $GenerationRoot -Recurse -Force
    }
}
