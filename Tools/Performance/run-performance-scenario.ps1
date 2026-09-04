[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UnityEditorPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageVersion,

    [Parameter(Mandatory)]
    [ValidateRange(1, 2147483647)]
    [int]$ScenarioId,

    [ValidateRange(30, 1800)]
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$BuildScript = Join-Path $RepoRoot 'Tools\Build\build-windows-offline.ps1'
$BuildDirectory = Join-Path $RepoRoot "Builds\Windows\$PackageVersion"
$PlayerPath = Join-Path $BuildDirectory 'rouge.exe'
$EvidenceDirectory = Join-Path $BuildDirectory "Performance\Scenario-$ScenarioId"
$ResultPath = Join-Path $EvidenceDirectory 'performance-result.json'
$ScreenshotPath = Join-Path $EvidenceDirectory 'performance-screenshot.png'
$PlayerLogPath = Join-Path $EvidenceDirectory 'Player.log'

& $BuildScript `
    -UnityEditorPath $UnityEditorPath `
    -PackageVersion $PackageVersion `
    -SkipSmokeTest

if (-not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) {
    throw "Performance player does not exist: $PlayerPath"
}

New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

$playerArguments = @(
    '-performanceScenario', $ScenarioId,
    '-performanceOutput', $ResultPath,
    '-performanceScreenshot', $ScreenshotPath,
    '-logFile', $PlayerLogPath
)

$playerProcess = Start-Process `
    -FilePath $PlayerPath `
    -ArgumentList $playerArguments `
    -WorkingDirectory $BuildDirectory `
    -PassThru

if (-not $playerProcess.WaitForExit($TimeoutSeconds * 1000)) {
    $null = $playerProcess.CloseMainWindow()
    if (-not $playerProcess.WaitForExit(5000)) {
        Stop-Process -Id $playerProcess.Id -Force
        $playerProcess.WaitForExit()
    }

    throw "Performance scenario timed out after $TimeoutSeconds seconds. Log: $PlayerLogPath"
}

if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
    throw "Performance Player exited without a JSON result. ExitCode=$($playerProcess.ExitCode) Log=$PlayerLogPath"
}

$result = Get-Content -Raw -LiteralPath $ResultPath -Encoding UTF8 | ConvertFrom-Json
$summary = [PSCustomObject]@{
    ScenarioId = $result.scenarioId
    Passed = $result.pass
    Entities = $result.entityCount
    Frames = $result.frameCount
    AverageFps = [Math]::Round([double]$result.averageFps, 2)
    P95FrameTimeMs = [Math]::Round([double]$result.p95FrameTimeMs, 3)
    OnePercentLowFps = [Math]::Round([double]$result.onePercentLowFps, 2)
    MaxGcAllocBytesPerFrame = $result.maxGcAllocBytesPerFrame
    Result = $ResultPath
    Screenshot = $ScreenshotPath
    Log = $PlayerLogPath
}
$summary | Format-List | Out-Host

if ($playerProcess.ExitCode -ne 0 -or -not $result.pass) {
    throw "Performance scenario failed. ExitCode=$($playerProcess.ExitCode) Result=$ResultPath"
}

Write-Host "Performance scenario passed: $ResultPath"
