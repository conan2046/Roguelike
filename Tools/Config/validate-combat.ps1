[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$ProjectPath = Join-Path $PSScriptRoot 'CombatConfigChecks/CombatConfigChecks.csproj'
$DataPath = Join-Path $RepoRoot 'Assets/StreamingAssets/Config/Luban'

& dotnet run --project $ProjectPath -- $DataPath
if ($LASTEXITCODE -ne 0) {
    throw "Combat configuration checks failed with exit code $LASTEXITCODE."
}
