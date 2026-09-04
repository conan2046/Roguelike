[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$LubanDll = Join-Path $RepoRoot 'Tools\Luban\Luban.dll'
$ConfigFile = Join-Path $RepoRoot 'Config\luban.conf'
$CommittedCodeDir = Join-Path $RepoRoot 'Assets\Scripts\Generated\Config'
$CommittedDataDir = Join-Path $RepoRoot 'Assets\StreamingAssets\Config\Luban'
$ValidationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('roguelike-luban-' + [Guid]::NewGuid().ToString('N'))
$GeneratedCodeDir = Join-Path $ValidationRoot 'code'
$GeneratedDataDir = Join-Path $ValidationRoot 'data'

function Get-RelativeHashes([string]$Root, [switch]$NormalizeCodeLineEndings) {
    if (-not (Test-Path -LiteralPath $Root)) {
        throw "Generated directory is missing: $Root"
    }

    $result = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object Extension -ne '.meta' | Sort-Object FullName) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($NormalizeCodeLineEndings -and $file.Extension -eq '.cs') {
            $content = [System.IO.File]::ReadAllText($file.FullName)
            $normalized = $content.Replace("`r`n", "`n").Replace("`r", "`n")
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
            $result[$relative] = [System.Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData($bytes))
        }
        else {
            $result[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $result
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
        throw "Luban validation generation failed with exit code $LASTEXITCODE."
    }

    $expected = Get-RelativeHashes $GeneratedCodeDir -NormalizeCodeLineEndings
    foreach ($entry in (Get-RelativeHashes $GeneratedDataDir).GetEnumerator()) {
        $expected['data/' + $entry.Key] = $entry.Value
    }

    $actual = Get-RelativeHashes $CommittedCodeDir -NormalizeCodeLineEndings
    foreach ($entry in (Get-RelativeHashes $CommittedDataDir).GetEnumerator()) {
        $actual['data/' + $entry.Key] = $entry.Value
    }

    $allPaths = @($expected.Keys + $actual.Keys | Sort-Object -Unique)
    $differences = foreach ($relativePath in $allPaths) {
        if ($expected[$relativePath] -ne $actual[$relativePath]) {
            $relativePath
        }
    }

    if ($differences) {
        throw "Committed Luban output is stale:`n$($differences -join "`n")"
    }

    Write-Host 'Luban validation completed. Committed outputs match Excel sources.'
}
finally {
    if (Test-Path -LiteralPath $ValidationRoot) {
        Remove-Item -LiteralPath $ValidationRoot -Recurse -Force
    }
}
