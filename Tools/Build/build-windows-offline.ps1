[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UnityEditorPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageVersion,

    [ValidateRange(10, 600)]
    [int]$StartupTimeoutSeconds = 120,

    [switch]$SkipSmokeTest
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$BuildRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'Builds'))
$StagingRoot = [System.IO.Path]::GetFullPath((Join-Path $BuildRoot '.staging'))
$FinalParent = [System.IO.Path]::GetFullPath((Join-Path $BuildRoot 'Windows'))
$FinalDirectory = [System.IO.Path]::GetFullPath((Join-Path $FinalParent $PackageVersion))
$StagingDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $StagingRoot ("windows-$PackageVersion-" + [Guid]::NewGuid().ToString('N'))))
$PlayerPath = Join-Path $StagingDirectory 'rouge.exe'
$UnityLogPath = Join-Path $StagingDirectory 'UnityBuild.log'
$PlayerLogPath = Join-Path $StagingDirectory 'Player.log'
$SuccessMarker = '[Roguelike] Application startup completed.'

function Assert-ChildPath([string]$Parent, [string]$Child) {
    $parentPrefix = $Parent.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $Child.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside expected root: $Child"
    }
}

function Stop-OwnedPlayer([System.Diagnostics.Process]$Process) {
    if ($null -eq $Process -or $Process.HasExited) {
        return
    }

    $null = $Process.CloseMainWindow()
    if (-not $Process.WaitForExit(5000)) {
        Stop-Process -Id $Process.Id -Force
        $Process.WaitForExit()
    }
}

if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity Editor executable does not exist: $UnityEditorPath"
}

if (Test-Path -LiteralPath (Join-Path $RepoRoot 'Temp\UnityLockfile')) {
    throw "This worktree is already open in Unity: $RepoRoot"
}

Assert-ChildPath $BuildRoot $StagingRoot
Assert-ChildPath $BuildRoot $FinalDirectory
Assert-ChildPath $StagingRoot $StagingDirectory

if (Test-Path -LiteralPath $FinalDirectory) {
    throw "Validated output already exists. Use a new package version: $FinalDirectory"
}

New-Item -ItemType Directory -Force -Path $StagingDirectory | Out-Null

& (Join-Path $RepoRoot 'Tools\Config\validate.ps1')

$unityArguments = @(
    '-batchmode',
    '-quit',
    '-projectPath', $RepoRoot,
    '-buildTarget', 'Win64',
    '-executeMethod', 'Roguelike.Infrastructure.Editor.OfflineWindowsBuild.BuildFromCommandLine',
    '-packageVersion', $PackageVersion,
    '-playerOutput', $PlayerPath,
    '-logFile', $UnityLogPath
)

$unityProcess = Start-Process `
    -FilePath $UnityEditorPath `
    -ArgumentList $unityArguments `
    -PassThru `
    -WindowStyle Hidden
$unityProcess.WaitForExit()

if ($unityProcess.ExitCode -ne 0) {
    throw "Unity offline build failed with exit code $($unityProcess.ExitCode). Log: $UnityLogPath"
}

if (-not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) {
    throw "Unity returned success but the player executable is missing: $PlayerPath"
}

if (-not $SkipSmokeTest) {
    $playerProcess = $null
    try {
        $playerArguments = @(
            '-screen-fullscreen', '0',
            '-screen-width', '1280',
            '-screen-height', '720',
            '-logFile', $PlayerLogPath
        )
        $playerProcess = Start-Process `
            -FilePath $PlayerPath `
            -ArgumentList $playerArguments `
            -PassThru `
            -WindowStyle Hidden

        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        $startupCompleted = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            if (Test-Path -LiteralPath $PlayerLogPath) {
                $logContent = Get-Content -Raw -LiteralPath $PlayerLogPath -ErrorAction SilentlyContinue
                if ($null -ne $logContent -and $logContent.Contains($SuccessMarker)) {
                    $startupCompleted = $true
                    break
                }
            }

            if ($playerProcess.HasExited) {
                break
            }

            Start-Sleep -Milliseconds 500
        }

        if (-not $startupCompleted) {
            throw "Windows player did not complete offline startup within $StartupTimeoutSeconds seconds. Log: $PlayerLogPath"
        }
    }
    finally {
        Stop-OwnedPlayer $playerProcess
    }
}

New-Item -ItemType Directory -Force -Path $FinalParent | Out-Null
Move-Item -LiteralPath $StagingDirectory -Destination $FinalDirectory

Write-Host "Windows offline build completed: $FinalDirectory"
